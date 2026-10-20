import type { ApiClient, ShoppingListItem } from "./api";

/**
 * The shopping list's copy on this phone (#85, spike #83): the list as last seen, the **outbox** of changes not yet
 * sent, and the names seen (for suggestions offline). One record, written whole, so the screen and the outbox never
 * disagree. The server is the source of truth: this is a cache plus a short queue, never the only copy for long.
 */
export interface ListSnapshot {
    items: ShoppingListItem[];
    asOf?: string;
    outbox: OutboxEntry[];
    names: string[];
}

/** A change waiting to be sent, in the order it was made. Its `changeId` makes a resend safe. */
export type OutboxEntry =
    | {
          op: "add";
          changeId: string;
          itemId: string;
          name: string;
          productId: string | null;
          source: string;
          at: string;
      }
    | { op: "tick" | "untick" | "remove"; changeId: string; itemId: string; at: string }
    | { op: "clear"; changeId: string; at: string };

export interface ListPersistence {
    load(): Promise<ListSnapshot | null>;
    save(snapshot: ListSnapshot): Promise<void>;
}

/** Sends one outbox entry through the list's normal (idempotent) endpoints. */
export async function send(api: ApiClient, entry: OutboxEntry): Promise<ShoppingListItem[]> {
    switch (entry.op) {
        case "add":
            return [
                (
                    await api.addToList(entry.itemId, {
                        changeId: entry.changeId,
                        name: entry.name,
                        productId: entry.productId,
                        source: entry.source,
                    })
                ).item,
            ];
        case "tick":
        case "untick":
            return [
                (await api.tickListItem(entry.itemId, entry.op === "tick", entry.changeId, entry.at)).item,
            ];
        case "remove":
            return [(await api.removeListItem(entry.itemId, entry.changeId)).item];
        case "clear":
            await api.clearTicked();
            return [];
    }
}

const DB = "cupboard";
const STORE = "shoppingList";
const KEY = "list";

/** IndexedDB on the phone; falls back to memory where there's none (private modes, tests). */
export function indexedDbPersistence(
    factory: IDBFactory | undefined = globalThis.indexedDB,
): ListPersistence {
    if (!factory) return memoryPersistence();
    const open = () =>
        new Promise<IDBDatabase>((resolve, reject) => {
            const request = factory.open(DB, 1);
            request.onupgradeneeded = () => request.result.createObjectStore(STORE);
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    const run = async <T>(mode: IDBTransactionMode, work: (store: IDBObjectStore) => IDBRequest<T>) => {
        const db = await open();
        try {
            return await new Promise<T>((resolve, reject) => {
                const request = work(db.transaction(STORE, mode).objectStore(STORE));
                request.onsuccess = () => resolve(request.result);
                request.onerror = () => reject(request.error);
            });
        } finally {
            db.close();
        }
    };
    return {
        load: async () => (await run<ListSnapshot | undefined>("readonly", (s) => s.get(KEY))) ?? null,
        save: async (snapshot) => {
            await run("readwrite", (s) => s.put(snapshot, KEY));
        },
    };
}

export function memoryPersistence(
    initial: ListSnapshot | null = null,
): ListPersistence & { current: ListSnapshot | null } {
    const store = {
        current: initial,
        load: async () => store.current,
        save: async (snapshot: ListSnapshot) => {
            store.current = snapshot;
        },
    };
    return store;
}

/** Asks the browser to keep this site's storage (spike #83: harmless if refused; iPhone decides by heuristics). */
export function askToPersist(storage: StorageManager | undefined = globalThis.navigator?.storage): void {
    storage?.persist?.().catch(() => undefined);
}
