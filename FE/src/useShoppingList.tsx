import {
    createContext,
    useCallback,
    useContext,
    useEffect,
    useMemo,
    useRef,
    useState,
    type ReactNode,
} from "react";
import { HubConnectionState } from "@microsoft/signalr";
import { ApiError, SignInRequiredError, type ApiClient, type ShoppingListItem } from "./api";
import {
    askToPersist,
    indexedDbPersistence,
    send,
    type ListPersistence,
    type ListSnapshot,
    type OutboxEntry,
} from "./listStore";
import {
    applyItem,
    applyRemoved,
    doneItems,
    EMPTY_LIST,
    newId,
    openItems,
    replaceAll,
    type ListState,
} from "./shoppingList";
import { signalRConnection, type FeedConnection } from "./useScanFeed";

/** "Syncing…" while the outbox is being sent, then "Synced" for a moment (#85). */
export type SyncState = "idle" | "syncing" | "synced";

export interface ShoppingList {
    loaded: boolean;
    failed: boolean;
    open: ShoppingListItem[];
    done: ShoppingListItem[];
    /** No connection (#85): changes go into the outbox and wait. */
    offline: boolean;
    /** Changes made on this phone not yet sent. */
    waiting: number;
    /** Item ids with a change waiting to be sent ("Not synced"). */
    pending: ReadonlySet<string>;
    sync: SyncState;
    /** Online, but the sign-in can't be renewed: the outbox is kept until the person signs in again. */
    needsSignIn: boolean;
    /** Every name seen on the list, for suggestions while offline. */
    knownNames: readonly string[];
    /** Adds at once (the row appears straight away); the API's answer and live events fill in the rest. */
    add(name: string, productId?: string | null, source?: string): void;
    tick(item: ShoppingListItem, done: boolean): void;
    remove(item: ShoppingListItem): void;
    clearTicked(): void;
    reload(): void;
}

const Context = createContext<ShoppingList | null>(null);

/** The list, kept live for the whole signed-in app (the List tab's badge shows its count on every screen). */
export function useShoppingList(): ShoppingList {
    const list = useContext(Context);
    if (!list) throw new Error("useShoppingList needs a ShoppingListProvider");
    return list;
}

/** The list when there is one (screens shown outside the signed-in app, e.g. in tests, have none). */
export function useOptionalShoppingList(): ShoppingList | null {
    return useContext(Context);
}

/** The count for the List tab's badge, or 0 outside a provider (e.g. in screen tests). */
export function useOpenCount(): number {
    return useContext(Context)?.open.length ?? 0;
}

export const SYNC_EVERY_MS = 30_000;
export const SYNCED_FOR_MS = 3_000;

const isOnline = () => typeof navigator === "undefined" || navigator.onLine !== false;

/** A failure that means "no connection right now" rather than "this change is wrong". */
function isNetworkFailure(error: unknown): boolean {
    if (!isOnline()) return true;
    if (error instanceof TypeError) return true; // fetch couldn't reach the server
    return !!error && typeof error === "object" && (error as { name?: unknown }).name === "BrowserAuthError";
}

/**
 * The shared shopping list (#84), working offline too (#85, spike #83). The copy on this phone (IndexedDB) shows at
 * once, online or not. Every change is applied locally and queued in an **outbox**, which is sent in order through the
 * list's idempotent endpoints when there's a connection: on open, `online`, coming back to the foreground, after each
 * change, and every 30 s while visible with something waiting. Live SignalR events and `since` catch-ups keep it up to
 * date, without overwriting items that still have a change waiting. Offline, nobody is sent to sign-in; if the
 * sign-in can't be renewed once online, the outbox is kept and the screen says so.
 */
export function ShoppingListProvider({
    api,
    children,
    connect = signalRConnection,
    persistence,
}: {
    api: ApiClient;
    children: ReactNode;
    connect?: (api: ApiClient) => FeedConnection;
    persistence?: ListPersistence;
}) {
    const store = useMemo(() => persistence ?? indexedDbPersistence(), [persistence]);
    const [state, setState] = useState<ListState>(EMPTY_LIST);
    const [outbox, setOutbox] = useState<OutboxEntry[]>([]);
    const [names, setNames] = useState<string[]>([]);
    const [loaded, setLoaded] = useState(false);
    const [failed, setFailed] = useState(false);
    const [offline, setOffline] = useState(() => !isOnline());
    const [sync, setSync] = useState<SyncState>("idle");
    const [needsSignIn, setNeedsSignIn] = useState(false);
    const asOf = useRef<string | undefined>(undefined);
    const restored = useRef(false);
    const ready = useRef<Promise<void>>(Promise.resolve());
    const sending = useRef(false);
    const outboxRef = useRef<OutboxEntry[]>([]);
    const connectRef = useRef(connect);
    outboxRef.current = outbox;

    const pendingIds = (entries: OutboxEntry[]) =>
        new Set(entries.flatMap((e) => ("itemId" in e ? [e.itemId] : [])));

    /** Server news, except for items with a change still waiting here: this phone's view wins until it's sent. */
    const fromServer = useCallback((items: ShoppingListItem[], replace: boolean) => {
        const waiting = pendingIds(outboxRef.current);
        setState((s) => {
            const keep = items.filter((i) => !waiting.has(i.id));
            return replace
                ? replaceAll({ ...s, items: new Map([...s.items].filter(([id]) => waiting.has(id))) }, keep)
                : keep.reduce(applyItem, s);
        });
        setNames((current) => mergeNames(current, items));
    }, []);

    const fail = useCallback((error: unknown, what: string) => {
        if (error instanceof SignInRequiredError) return;
        console.warn(what, error);
    }, []);

    const catchUp = useCallback(async () => {
        if (!isOnline()) {
            setOffline(true);
            return;
        }
        try {
            const since = asOf.current;
            const answer = await api.shoppingList(since);
            asOf.current = answer.asOf;
            fromServer(answer.items, !since);
            setLoaded(true);
            setFailed(false);
            setOffline(false);
        } catch (error) {
            if (isNetworkFailure(error)) setOffline(true);
            else fail(error, "Couldn't load the shopping list");
            if (!asOf.current && !restored.current) setFailed(true);
        }
    }, [api, fail, fromServer]);

    /** Sends the outbox in order. Stops at the first connection problem and keeps the rest for later. */
    const flush = useCallback(async () => {
        if (sending.current || outboxRef.current.length === 0) return;
        if (!isOnline()) {
            setOffline(true);
            return;
        }
        sending.current = true;
        setSync("syncing");
        let sentAll = true;
        try {
            while (outboxRef.current.length > 0) {
                const entry = outboxRef.current[0];
                try {
                    const items = await send(api, entry);
                    const rest = outboxRef.current.filter((e) => e.changeId !== entry.changeId);
                    outboxRef.current = rest;
                    setOutbox(rest);
                    const stillWaiting = pendingIds(rest);
                    for (const item of items) {
                        if (!stillWaiting.has(item.id)) setState((s) => applyItem(s, item));
                    }
                    setOffline(false);
                    setNeedsSignIn(false);
                } catch (error) {
                    if (error instanceof SignInRequiredError) {
                        setNeedsSignIn(true); // a sign-in redirect is under way; the outbox is saved on the phone
                        sentAll = false;
                        break;
                    }
                    if (isNetworkFailure(error)) {
                        setOffline(true);
                        sentAll = false;
                        break;
                    }
                    if (
                        error instanceof ApiError &&
                        error.status >= 400 &&
                        error.status < 500 &&
                        error.status !== 429
                    ) {
                        // The server refused this change (e.g. the item no longer exists): drop it, carry on.
                        console.warn("A shopping list change was refused and dropped", error);
                        const rest = outboxRef.current.filter((e) => e.changeId !== entry.changeId);
                        outboxRef.current = rest;
                        setOutbox(rest);
                        continue;
                    }
                    fail(error, "Couldn't sync the shopping list");
                    sentAll = false;
                    break;
                }
            }
        } finally {
            sending.current = false;
        }
        if (sentAll) {
            setSync("synced");
            await catchUp();
        } else {
            setSync("idle");
        }
    }, [api, catchUp, fail]);

    // Restore this phone's copy first (instant, even offline); the network waits for it (below).
    useEffect(() => {
        let live = true;
        askToPersist();
        ready.current = store
            .load()
            .then((snapshot) => {
                if (!live || !snapshot) return;
                restored.current = true;
                asOf.current = snapshot.asOf;
                outboxRef.current = [...snapshot.outbox, ...outboxRef.current];
                setOutbox(outboxRef.current);
                setNames(snapshot.names);
                setState((s) => replaceAll(s, snapshot.items));
                setLoaded(true);
            })
            .catch((error: unknown) => console.warn("Couldn't read the list saved on this phone", error));
        return () => {
            live = false;
        };
    }, [store]);

    // Save the copy whenever it changes (one record: the list and the outbox together).
    useEffect(() => {
        if (!loaded && outbox.length === 0) return;
        const snapshot: ListSnapshot = {
            items: [...state.items.values()],
            asOf: asOf.current,
            outbox,
            names,
        };
        store
            .save(snapshot)
            .catch((error: unknown) => console.warn("Couldn't save the list on this phone", error));
    }, [store, state, outbox, names, loaded]);

    // "Synced" shows briefly.
    useEffect(() => {
        if (sync !== "synced") return;
        const timer = setTimeout(() => setSync("idle"), SYNCED_FOR_MS);
        return () => clearTimeout(timer);
    }, [sync]);

    // Live updates (SignalR) and the triggers.
    useEffect(() => {
        let live = true;
        const connection = connectRef.current(api);
        connection.on("shoppingListItemChanged", (payload) =>
            fromServer([payload as ShoppingListItem], false),
        );
        connection.on("shoppingListItemRemoved", (payload) => {
            const { id, version } = payload as { id: string; version: number };
            setState((s) => applyRemoved(s, id, version));
        });
        connection.on("shoppingListCleared", (payload) => {
            const { ids } = payload as { ids: string[] };
            setState((s) => ids.reduce((acc, id) => applyRemoved(acc, id, Number.MAX_SAFE_INTEGER), s));
        });
        connection.onreconnected(() => void flush().then(() => catchUp()));

        const start = async () => {
            await ready.current;
            if (
                connection.state === HubConnectionState.Disconnected ||
                connection.state === HubConnectionState.Connecting
            ) {
                try {
                    await connection.start();
                } catch (error) {
                    if (!isNetworkFailure(error))
                        fail(error, "The shopping list couldn't connect for live updates");
                }
            }
            if (live) {
                await flush();
                await catchUp();
            }
        };
        void start();

        const onVisible = () => {
            if (document.visibilityState === "visible") void start();
        };
        const onOnline = () => {
            setOffline(false);
            void start();
        };
        const onOffline = () => setOffline(true);
        const every = setInterval(() => {
            if (document.visibilityState === "visible" && outboxRef.current.length > 0) void flush();
        }, SYNC_EVERY_MS);
        document.addEventListener("visibilitychange", onVisible);
        window.addEventListener("online", onOnline);
        window.addEventListener("offline", onOffline);
        return () => {
            live = false;
            clearInterval(every);
            document.removeEventListener("visibilitychange", onVisible);
            window.removeEventListener("online", onOnline);
            window.removeEventListener("offline", onOffline);
            void connection.stop();
        };
    }, [api, catchUp, fail, flush, fromServer]);

    const queue = useCallback(
        (entry: OutboxEntry) => {
            outboxRef.current = [...outboxRef.current, entry];
            setOutbox(outboxRef.current);
            void flush();
        },
        [flush],
    );

    const value = useMemo<ShoppingList>(() => {
        const open = openItems(state);
        return {
            loaded,
            failed,
            open,
            done: doneItems(state),
            offline,
            waiting: outbox.length,
            pending: pendingIds(outbox),
            sync,
            needsSignIn,
            knownNames: names,
            add: (name, productId = null, source = productId ? "suggestion" : "typed") => {
                const trimmed = name.trim();
                if (!trimmed) return;
                const existing = open.find((i) => i.name.trim().toLowerCase() === trimmed.toLowerCase());
                if (existing) return; // already on the list (the server checks too)
                const id = newId();
                const now = new Date().toISOString();
                setState((s) =>
                    applyItem(s, {
                        id,
                        name: trimmed,
                        note: null,
                        productId,
                        aisleId: null,
                        aisleName: null,
                        findingAisle: !productId && isOnline(), // offline it waits as "Not synced" instead
                        source,
                        status: "open",
                        addedByName: "You",
                        addedAt: now,
                        doneByName: null,
                        doneAt: null,
                        updatedAt: now,
                        version: 0,
                        removed: false,
                    }),
                );
                setNames((current) => mergeNames(current, [{ name: trimmed }]));
                queue({
                    op: "add",
                    changeId: newId(),
                    itemId: id,
                    name: trimmed,
                    productId,
                    source,
                    at: now,
                });
            },
            tick: (item, done) => {
                const now = new Date().toISOString();
                setState((s) => {
                    const items = new Map(s.items);
                    items.set(item.id, {
                        ...item,
                        status: done ? "done" : "open",
                        doneAt: done ? now : null,
                        doneByName: done ? "You" : null,
                    });
                    return { ...s, items };
                });
                queue({ op: done ? "tick" : "untick", changeId: newId(), itemId: item.id, at: now });
            },
            remove: (item) => {
                setState((s) => applyRemoved(s, item.id, item.version));
                queue({ op: "remove", changeId: newId(), itemId: item.id, at: new Date().toISOString() });
            },
            clearTicked: () => {
                setState((s) => doneItems(s).reduce((acc, i) => applyRemoved(acc, i.id, i.version), s));
                queue({ op: "clear", changeId: newId(), at: new Date().toISOString() });
            },
            reload: () => {
                asOf.current = undefined;
                void catchUp();
            },
        };
    }, [state, outbox, names, loaded, failed, offline, sync, needsSignIn, queue, catchUp]);

    return <Context.Provider value={value}>{children}</Context.Provider>;
}

/** Names seen on the list, newest first, without repeats (for suggestions offline). */
function mergeNames(current: string[], items: Array<{ name: string }>): string[] {
    const seen = new Set<string>();
    return [...items.map((i) => i.name.trim()), ...current]
        .filter((n) => {
            const key = n.toLowerCase();
            if (!n || seen.has(key)) return false;
            seen.add(key);
            return true;
        })
        .slice(0, 500);
}
