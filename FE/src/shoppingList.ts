import { AISLES, aisleOf } from "./aisles";
import type { ShoppingListItem } from "./api";

/**
 * The shopping list's state (#84): items by id. Every change (from the API's answer, a live SignalR event or a catch-up)
 * goes through `applyItem`, which keeps the higher `version`, so a late or repeated event never undoes a newer change.
 * Removed items leave their version behind, so a stale "changed" event can't bring them back.
 */
export interface ListState {
    items: ReadonlyMap<string, ShoppingListItem>;
    removed: ReadonlyMap<string, number>;
}

export const EMPTY_LIST: ListState = { items: new Map(), removed: new Map() };

export function applyItem(state: ListState, item: ShoppingListItem): ListState {
    const current = state.items.get(item.id);
    const gone = state.removed.get(item.id);
    if ((current && current.version > item.version) || (gone !== undefined && gone >= item.version))
        return state;
    if (item.removed) return applyRemoved(state, item.id, item.version);
    const items = new Map(state.items);
    items.set(item.id, item);
    return { items, removed: state.removed };
}

export function applyRemoved(state: ListState, id: string, version: number): ListState {
    const current = state.items.get(id);
    if (current && current.version > version) return state;
    const items = new Map(state.items);
    items.delete(id);
    const removed = new Map(state.removed);
    removed.set(id, Math.max(version, removed.get(id) ?? 0));
    return { items, removed };
}

/** A full load: the server's list, plus anything newer this app already has (e.g. an add still on its way). */
export function replaceAll(state: ListState, fresh: ShoppingListItem[]): ListState {
    let next: ListState = { items: new Map(), removed: state.removed };
    for (const item of fresh) next = applyItem(next, item);
    for (const local of state.items.values()) {
        const known = next.items.get(local.id);
        if (!known ? local.version === 0 : local.version > known.version) next = applyItem(next, local);
    }
    return next;
}

export const openItems = (state: ListState) =>
    [...state.items.values()]
        .filter((i) => i.status === "open")
        .sort((a, b) => a.addedAt.localeCompare(b.addedAt) || a.id.localeCompare(b.id));

export const doneItems = (state: ListState) =>
    [...state.items.values()]
        .filter((i) => i.status === "done")
        .sort((a, b) => (b.doneAt ?? "").localeCompare(a.doneAt ?? ""));

export interface ListGroup {
    title: string | null;
    items: ShoppingListItem[];
}

/** "Sort by aisle": items still waiting for an aisle under "Just added", then each aisle in shop order. */
export function groupByAisle(items: ShoppingListItem[]): ListGroup[] {
    const waiting = items.filter((i) => i.findingAisle);
    const groups: ListGroup[] = waiting.length ? [{ title: "Just added", items: waiting }] : [];
    for (const aisle of AISLES) {
        const inAisle = items.filter((i) => !i.findingAisle && aisleOf(i.aisleId).id === aisle.id);
        if (inAisle.length) groups.push({ title: aisle.name, items: inAisle });
    }
    return groups;
}

const UK = "Europe/London";
const ukDay = (d: Date) => d.toLocaleDateString("en-CA", { timeZone: UK });

/** "just now", "08:12" (today), "yesterday", "Sat" (this week), "15 Oct": when it was added or ticked. */
export function listWhen(iso: string, now: Date = new Date()): string {
    const at = new Date(iso);
    const ms = now.getTime() - at.getTime();
    if (ms < 60_000) return "just now";
    if (ukDay(at) === ukDay(now))
        return at.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", timeZone: UK });
    if (ukDay(at) === ukDay(new Date(now.getTime() - 86_400_000))) return "yesterday";
    if (ms < 6 * 86_400_000) return at.toLocaleDateString("en-GB", { weekday: "short", timeZone: UK });
    return at.toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: UK });
}

/** A new id for an item or a change: the app makes them, so retries (and, later, offline changes) apply once. */
export const newId = (): string =>
    typeof crypto !== "undefined" && "randomUUID" in crypto
        ? crypto.randomUUID()
        : "10000000-1000-4000-8000-100000000000".replace(/[018]/g, (c) =>
              (Number(c) ^ (Math.floor(Math.random() * 256) & (15 >> (Number(c) / 4)))).toString(16),
          );
