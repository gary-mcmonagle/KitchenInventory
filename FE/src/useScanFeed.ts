import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { useCallback, useEffect, useRef, useState } from "react";
import { SignInRequiredError, type ApiClient } from "./api";
import {
    FEED_EVENT_TYPES,
    applyCatchUp,
    applyEvent,
    changesStock,
    expire,
    visibleToasts,
    type FeedEvent,
    type FeedState,
    type FeedToast,
} from "./scanFeed";

/** The bits of a SignalR connection the feed uses, so tests can fake it. */
export interface FeedConnection {
    on(event: string, handler: (payload: unknown) => void): void;
    onreconnected(handler: () => void): void;
    start(): Promise<void>;
    stop(): Promise<void>;
    readonly state: HubConnectionState | string;
}

interface SharedHub {
    connection: HubConnection;
    users: number;
    starting: Promise<void> | null;
    reconnected: Set<() => void>;
}

const hubs = new WeakMap<ApiClient, SharedHub>();

/**
 * One SignalR connection per signed-in app (#84), shared by the scan feed and the shopping list, so a phone counts
 * once against the Free tier's 20 connections. Each caller gets its own handle: `start` joins (connecting if needed),
 * `stop` leaves and removes that handle's handlers; the connection closes when the last one leaves.
 */
export function signalRConnection(api: ApiClient): FeedConnection {
    let hub = hubs.get(api);
    if (!hub) {
        const connection: HubConnection = new HubConnectionBuilder()
            .withUrl(api.feedUrl, {
                accessTokenFactory: () => api.accessToken(),
                withCredentials: false, // bearer token only: no cookies, no credentialed CORS
            })
            .withAutomaticReconnect([0, 2000, 10000, 30000])
            .configureLogging(LogLevel.Warning)
            .build();
        const created: SharedHub = { connection, users: 0, starting: null, reconnected: new Set() };
        connection.onreconnected(() => created.reconnected.forEach((h) => h()));
        hubs.set(api, created);
        hub = created;
    }
    const shared = hub;
    const handlers: Array<[string, (payload: unknown) => void]> = [];
    const reconnected: Array<() => void> = [];
    let joined = false;
    return {
        on: (event, handler) => {
            handlers.push([event, handler]);
            shared.connection.on(event, handler);
        },
        onreconnected: (handler) => {
            reconnected.push(handler);
            shared.reconnected.add(handler);
        },
        start: async () => {
            if (!joined) {
                joined = true;
                shared.users++;
            }
            if (shared.connection.state !== HubConnectionState.Disconnected)
                return shared.starting ?? undefined;
            shared.starting = shared.connection.start().finally(() => (shared.starting = null));
            return shared.starting;
        },
        stop: async () => {
            handlers.forEach(([event, handler]) => shared.connection.off(event, handler));
            reconnected.forEach((h) => shared.reconnected.delete(h));
            if (!joined) return;
            joined = false;
            shared.users--;
            if (shared.users === 0) await shared.connection.stop();
        },
        get state() {
            return shared.connection.state;
        },
    };
}

export const STOCK_REFETCH_DEBOUNCE_MS = 1000;
const TICK_MS = 1000;

/**
 * The live scan feed (#54): connects to SignalR, applies events by scan ID, catches up from the API after every
 * (re)connect and when the app comes back to the foreground, expires toasts, and asks the stock list to refetch
 * (debounced) when stock changed. The feed is a hint: if SignalR is down, catch-up still fills it in.
 */
export function useScanFeed(
    api: ApiClient,
    onStockChanged: () => void,
    connect: (api: ApiClient) => FeedConnection = signalRConnection,
    markSeen: (eventKey: string) => void = () => undefined,
) {
    const [feed, setFeed] = useState<FeedState>(new Map());
    const [now, setNow] = useState(() => Date.now());
    const lastSeen = useRef<string | undefined>(undefined);
    const refetchTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    const stockChanged = useRef(onStockChanged);
    stockChanged.current = onStockChanged;
    const connectRef = useRef(connect); // the first one is used for the screen's lifetime
    const reported = useRef(new Set<string>());

    const refetchStock = useCallback(() => {
        clearTimeout(refetchTimer.current);
        refetchTimer.current = setTimeout(() => stockChanged.current(), STOCK_REFETCH_DEBOUNCE_MS);
    }, []);

    const seen = (event: FeedEvent) => {
        if (!lastSeen.current || event.occurredAt > lastSeen.current) lastSeen.current = event.occurredAt;
    };

    const catchUp = useCallback(async () => {
        try {
            const result = await api.scanFeed(lastSeen.current);
            result.events.forEach(seen);
            setFeed((state) => applyCatchUp(state, result.events, Date.now()));
            if (result.events.some(changesStock)) refetchStock();
        } catch (error) {
            if (!(error instanceof SignInRequiredError)) console.warn("Scan feed catch-up failed", error);
        }
    }, [api, refetchStock]);

    useEffect(() => {
        let live = true;
        const connection = connectRef.current(api);
        for (const type of FEED_EVENT_TYPES) {
            connection.on(type, (payload) => {
                const event = { ...(payload as object), type } as FeedEvent;
                seen(event);
                setFeed((state) => applyEvent(state, event, Date.now()));
                if (changesStock(event)) refetchStock();
            });
        }
        connection.onreconnected(() => void catchUp());

        const start = async () => {
            if (
                connection.state === HubConnectionState.Disconnected ||
                connection.state === HubConnectionState.Connecting
            ) {
                try {
                    await connection.start();
                } catch (error) {
                    if (!(error instanceof SignInRequiredError))
                        console.warn("Scan feed couldn't connect", error);
                }
            }
            if (live) await catchUp(); // fill gaps whether or not the connection came up
        };
        void start();

        // Phones suspend background tabs and drop the socket: reconnect and catch up on return.
        const onVisible = () => {
            if (document.visibilityState !== "visible") return;
            if (connection.state === HubConnectionState.Disconnected) void start();
            else void catchUp();
        };
        document.addEventListener("visibilitychange", onVisible);
        window.addEventListener("online", onVisible);
        const tick = setInterval(() => setNow(Date.now()), TICK_MS);
        return () => {
            live = false;
            document.removeEventListener("visibilitychange", onVisible);
            window.removeEventListener("online", onVisible);
            clearInterval(tick);
            clearTimeout(refetchTimer.current);
            void connection.stop();
        };
    }, [api, catchUp, refetchStock]);

    // Expire toasts as time passes; a grey one that never resolved means the stock may have changed.
    useEffect(() => {
        const { state, loadingTimedOut } = expire(feed, now);
        if (state !== feed) setFeed(state);
        if (loadingTimedOut) refetchStock();
    }, [feed, now, refetchStock]);

    // Seen receipts (#80): a green check-in or check-out toast counts as seen once it's shown on a visible page
    // (live or from catch-up), so this person is left out of the push for it. Rechecked every tick, so a toast still
    // showing when the page comes back counts too.
    useEffect(() => {
        if (document.visibilityState !== "visible") return;
        for (const toast of visibleToasts(feed).shown) {
            if (toast.kind !== "ok" || !toast.seenKey || reported.current.has(toast.seenKey)) continue;
            reported.current.add(toast.seenKey);
            markSeen(toast.seenKey);
        }
    }, [feed, now, markSeen]);

    /** Amber "No match" and store labels: hidden for the whole household. "Not in stock": just here. */
    const dismiss = useCallback(
        async (toast: FeedToast) => {
            setFeed((state) => {
                const next = new Map(state);
                for (const [id, t] of state) {
                    if (
                        id === toast.scanId ||
                        (toast.kind === "warn" && t.kind === "warn" && t.productId === toast.productId)
                    )
                        next.delete(id);
                }
                return next;
            });
            try {
                if (toast.kind === "warn" && toast.productId) await api.dismissFeed(toast.productId);
                if (toast.kind === "label") await api.dismissStoreLabel(toast.scanId);
            } catch (error) {
                if (!(error instanceof SignInRequiredError)) console.warn("Couldn't dismiss", error);
            }
        },
        [api],
    );

    return { feed, now, dismiss };
}
