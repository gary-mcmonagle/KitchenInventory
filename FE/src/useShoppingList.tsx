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
import { SignInRequiredError, type ApiClient, type ShoppingListItem } from "./api";
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

export interface ShoppingList {
    loaded: boolean;
    failed: boolean;
    open: ShoppingListItem[];
    done: ShoppingListItem[];
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

/**
 * The shared shopping list (#84) for the signed-in app: loads it, applies live SignalR events by version, and catches
 * up with `since` after every (re)connect and when the app comes back to the foreground. No polling. Writes are
 * optimistic and carry ids the app made, so a retry is applied once.
 */
export function ShoppingListProvider({
    api,
    children,
    connect = signalRConnection,
}: {
    api: ApiClient;
    children: ReactNode;
    connect?: (api: ApiClient) => FeedConnection;
}) {
    const [state, setState] = useState<ListState>(EMPTY_LIST);
    const [loaded, setLoaded] = useState(false);
    const [failed, setFailed] = useState(false);
    const asOf = useRef<string | undefined>(undefined);
    const connectRef = useRef(connect);

    const apply = useCallback((item: ShoppingListItem) => setState((s) => applyItem(s, item)), []);

    const fail = useCallback((error: unknown, what: string) => {
        if (error instanceof SignInRequiredError) return;
        console.warn(what, error);
    }, []);

    const catchUp = useCallback(async () => {
        try {
            const since = asOf.current;
            const answer = await api.shoppingList(since);
            asOf.current = answer.asOf;
            setState((s) => (since ? answer.items.reduce(applyItem, s) : replaceAll(s, answer.items)));
            setLoaded(true);
            setFailed(false);
        } catch (error) {
            fail(error, "Couldn't load the shopping list");
            if (!asOf.current) setFailed(true);
        }
    }, [api, fail]);

    useEffect(() => {
        let live = true;
        const connection = connectRef.current(api);
        connection.on("shoppingListItemChanged", (payload) => apply(payload as ShoppingListItem));
        connection.on("shoppingListItemRemoved", (payload) => {
            const { id, version } = payload as { id: string; version: number };
            setState((s) => applyRemoved(s, id, version));
        });
        connection.on("shoppingListCleared", (payload) => {
            const { ids } = payload as { ids: string[] };
            setState((s) => ids.reduce((acc, id) => applyRemoved(acc, id, Number.MAX_SAFE_INTEGER), s));
        });
        connection.onreconnected(() => void catchUp());

        const start = async () => {
            if (
                connection.state === HubConnectionState.Disconnected ||
                connection.state === HubConnectionState.Connecting
            ) {
                try {
                    await connection.start();
                } catch (error) {
                    fail(error, "The shopping list couldn't connect for live updates");
                }
            }
            if (live) await catchUp();
        };
        void start();

        const onVisible = () => {
            if (document.visibilityState === "visible") void start();
        };
        document.addEventListener("visibilitychange", onVisible);
        window.addEventListener("online", onVisible);
        return () => {
            live = false;
            document.removeEventListener("visibilitychange", onVisible);
            window.removeEventListener("online", onVisible);
            void connection.stop();
        };
    }, [api, apply, catchUp, fail]);

    const write = useCallback(
        (request: Promise<{ item: ShoppingListItem }>, what: string) => {
            request
                .then(({ item }) => apply(item))
                .catch((error: unknown) => {
                    fail(error, what);
                    asOf.current = undefined; // put the list back as the server has it
                    void catchUp();
                });
        },
        [apply, catchUp, fail],
    );

    const value = useMemo<ShoppingList>(() => {
        const open = openItems(state);
        return {
            loaded,
            failed,
            open,
            done: doneItems(state),
            add: (name, productId = null, source = productId ? "suggestion" : "typed") => {
                const trimmed = name.trim();
                if (!trimmed) return;
                const existing = open.find((i) => i.name.trim().toLowerCase() === trimmed.toLowerCase());
                if (existing) return; // already on the list (the server checks too)
                const id = newId();
                const now = new Date().toISOString();
                apply({
                    id,
                    name: trimmed,
                    note: null,
                    productId,
                    aisleId: null,
                    aisleName: null,
                    findingAisle: !productId,
                    source,
                    status: "open",
                    addedByName: "You",
                    addedAt: now,
                    doneByName: null,
                    doneAt: null,
                    updatedAt: now,
                    version: 0,
                    removed: false,
                });
                write(
                    api.addToList(id, { changeId: newId(), name: trimmed, productId, source }),
                    "Couldn't add to the list",
                );
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
                write(api.tickListItem(item.id, done, newId()), "Couldn't tick that off");
            },
            remove: (item) => {
                setState((s) => applyRemoved(s, item.id, item.version));
                write(api.removeListItem(item.id, newId()), "Couldn't remove that");
            },
            clearTicked: () => {
                setState((s) => doneItems(s).reduce((acc, i) => applyRemoved(acc, i.id, i.version), s));
                api.clearTicked().catch((error: unknown) => {
                    fail(error, "Couldn't clear the ticked items");
                    asOf.current = undefined;
                    void catchUp();
                });
            },
            reload: () => {
                asOf.current = undefined;
                void catchUp();
            },
        };
    }, [state, loaded, failed, api, apply, write, fail, catchUp]);

    return <Context.Provider value={value}>{children}</Context.Provider>;
}
