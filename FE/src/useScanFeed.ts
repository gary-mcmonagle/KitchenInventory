import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { useCallback, useEffect, useRef, useState } from "react";
import { SignInRequiredError, type ApiClient } from "./api";
import {
    FEED_EVENT_TYPES,
    applyCatchUp,
    applyEvent,
    changesStock,
    expire,
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

export function signalRConnection(api: ApiClient): FeedConnection {
    const connection: HubConnection = new HubConnectionBuilder()
        .withUrl(api.feedUrl, {
            accessTokenFactory: () => api.accessToken(),
            withCredentials: false, // bearer token only: no cookies, no credentialed CORS
        })
        .withAutomaticReconnect([0, 2000, 10000, 30000])
        .configureLogging(LogLevel.Warning)
        .build();
    return {
        on: (event, handler) => connection.on(event, handler),
        onreconnected: (handler) => connection.onreconnected(() => handler()),
        start: () => connection.start(),
        stop: () => connection.stop(),
        get state() {
            return connection.state;
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
) {
    const [feed, setFeed] = useState<FeedState>(new Map());
    const [now, setNow] = useState(() => Date.now());
    const lastSeen = useRef<string | undefined>(undefined);
    const refetchTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    const stockChanged = useRef(onStockChanged);
    stockChanged.current = onStockChanged;
    const connectRef = useRef(connect); // the first one is used for the screen's lifetime

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
            if (connection.state !== HubConnectionState.Disconnected) return;
            try {
                await connection.start();
            } catch (error) {
                if (!(error instanceof SignInRequiredError))
                    console.warn("Scan feed couldn't connect", error);
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
