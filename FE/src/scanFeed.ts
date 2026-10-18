import { groupBarcode } from "./format";

/** The scan feed's events (#54, spike #38's contract; docs/api.md). */
interface ScanBase {
    scanId: string;
    deviceId: string;
    role: "in" | "out";
    occurredAt: string;
}

export type FeedEvent =
    | (ScanBase & { type: "scanReceived"; barcode: string; scannedAt: string })
    | (ScanBase & {
          type: "scanIdentified";
          outcome: "checkedIn" | "checkedOut";
          productId: string;
          displayName: string;
          brand: string | null;
          size: string | null;
          unitId: string;
      })
    | (ScanBase & {
          type: "scanNeedsIdentification";
          productId: string;
          barcode: string;
          displayName: string;
          productStatus: "noMatch" | "lookupFailed";
          unitId: string;
      })
    | (ScanBase & {
          type: "checkOutUnmatched";
          barcode: string;
          productId: string | null;
          displayName: string | null;
          /** An in-store label (#58). */
          storeLabel?: boolean;
      })
    | (ScanBase & { type: "scanSkipped"; barcode: string; reason: string })
    | (ScanBase & { type: "storeLabelCheckIn"; barcode: string })
    | {
          type: "storeLabelResolved";
          scanId: string;
          productId: string;
          displayName: string;
          occurredAt: string;
      }
    | { type: "productNamed"; productId: string; displayName: string; occurredAt: string }
    /** A product's amber toast, or (with scanId) a store label's (#58). */
    | { type: "feedDismissed"; productId: string | null; scanId?: string; occurredAt: string };

export type FeedEventType = FeedEvent["type"];

export const FEED_EVENT_TYPES: FeedEventType[] = [
    "scanReceived",
    "scanIdentified",
    "scanNeedsIdentification",
    "checkOutUnmatched",
    "scanSkipped",
    "storeLabelCheckIn",
    "storeLabelResolved",
    "productNamed",
    "feedDismissed",
];

/** Toast styles, as in the mockup: grey (reading), green (done), amber (needs naming, a store label to pick, or not
 * in stock). */
export type ToastKind = "loading" | "ok" | "warn" | "label" | "unmatched";

export interface FeedToast {
    scanId: string;
    kind: ToastKind;
    title: string;
    role: "in" | "out";
    /** When the scan happened (the first event's time): the subtitle and the order. */
    at: string;
    /** Amber toasts: the product to name or dismiss. */
    productId: string | null;
    /** When this toast goes by itself (green and "not in stock": 10 s; grey: 60 s), as epoch ms. */
    expiresAt: number | null;
}

export const OK_MS = 10_000;
export const LOADING_MS = 60_000;
export const MAX_VISIBLE = 3;

/** Toasts by scan ID. */
export type FeedState = ReadonlyMap<string, FeedToast>;

/**
 * Applies one event (live or from catch-up) at time <c>now</c>. Never moves a toast backwards: once a scan has an
 * outcome, a late "scanReceived" for it is ignored. Amber toasts for the same product collapse into the newest one.
 * Returns the same state object when nothing changed.
 */
export function applyEvent(state: FeedState, event: FeedEvent, now: number): FeedState {
    const next = new Map(state);
    switch (event.type) {
        case "scanReceived": {
            if (state.has(event.scanId)) return state; // already further on
            next.set(event.scanId, {
                scanId: event.scanId,
                kind: "loading",
                title: `Reading ${groupBarcode(event.barcode)}`,
                role: event.role,
                at: event.occurredAt,
                productId: null,
                expiresAt: Date.parse(event.occurredAt) + LOADING_MS,
            });
            return next;
        }
        case "scanIdentified": {
            const verb = event.outcome === "checkedIn" ? "Added" : "Checked out";
            next.set(event.scanId, {
                ...base(state, event),
                kind: "ok",
                title: `${verb} ${event.displayName}`,
                productId: event.productId,
                expiresAt: now + OK_MS,
            });
            return next;
        }
        case "scanNeedsIdentification": {
            for (const [id, toast] of state) {
                if (toast.kind === "warn" && toast.productId === event.productId && id !== event.scanId) {
                    if (Date.parse(toast.at) > Date.parse(event.occurredAt)) return state; // a newer one is shown
                    next.delete(id);
                }
            }
            next.set(event.scanId, {
                ...base(state, event),
                kind: "warn",
                title: `No match for ${groupBarcode(event.barcode)}`,
                productId: event.productId,
                expiresAt: null, // until named or dismissed
            });
            return next;
        }
        case "checkOutUnmatched": {
            next.set(event.scanId, {
                ...base(state, event),
                kind: "unmatched",
                title: event.storeLabel
                    ? "Store label: check it out from the app"
                    : `Not in stock: ${event.displayName ?? groupBarcode(event.barcode)}`,
                productId: event.productId,
                expiresAt: now + OK_MS,
            });
            return next;
        }
        case "scanSkipped":
            if (!state.has(event.scanId)) return state;
            next.delete(event.scanId);
            return next;
        case "storeLabelCheckIn":
            next.set(event.scanId, {
                ...base(state, event),
                kind: "label",
                title: "Store label: what is it?",
                productId: null,
                expiresAt: null, // until assigned or dismissed
            });
            return next;
        case "storeLabelResolved": {
            const toast = state.get(event.scanId);
            if (!toast) return state;
            next.set(event.scanId, {
                ...toast,
                kind: "ok",
                title: `Added ${event.displayName}`,
                productId: event.productId,
                expiresAt: now + OK_MS,
            });
            return next;
        }
        case "feedDismissed":
            if (event.scanId) {
                if (!state.has(event.scanId)) return state;
                next.delete(event.scanId);
                return next;
            }
            return withoutAmber(state, event.productId);
        case "productNamed":
            return withoutAmber(state, event.productId);
    }
}

/** Without that product's amber "No match" toasts (named or dismissed). */
function withoutAmber(state: FeedState, productId: string | null): FeedState {
    const next = new Map(state);
    for (const [id, toast] of state) {
        if (toast.kind === "warn" && toast.productId === productId) next.delete(id);
    }
    return next.size === state.size ? state : next;
}

/** Keeps the time of the scan from its first event, so the order and "2 minutes ago" don't jump. */
function base(
    state: FeedState,
    event: ScanBase,
): Omit<FeedToast, "kind" | "title" | "productId" | "expiresAt"> {
    return { scanId: event.scanId, role: event.role, at: state.get(event.scanId)?.at ?? event.occurredAt };
}

/**
 * Catch-up (GET /api/scan-feed): merges by scan ID like live events, but old news stays quiet. A green or "not in
 * stock" toast is only shown if it happened in the last 10 s, and a grey one in the last 60 s; amber always.
 */
export function applyCatchUp(state: FeedState, events: FeedEvent[], now: number): FeedState {
    let next = state;
    for (const event of events) {
        const age = now - Date.parse(event.occurredAt);
        const quiet =
            ((event.type === "scanIdentified" ||
                event.type === "checkOutUnmatched" ||
                event.type === "storeLabelResolved") &&
                age > OK_MS) ||
            (event.type === "scanReceived" && age > LOADING_MS);
        if (quiet) {
            // Old news: settle a grey toast we're still showing (or a store label someone else assigned), but don't
            // add a new one.
            const scanId = "scanId" in event ? event.scanId : undefined;
            const shown = scanId ? next.get(scanId)?.kind : undefined;
            if (
                scanId &&
                (shown === "loading" || (event.type === "storeLabelResolved" && shown === "label"))
            ) {
                const settled = new Map(next);
                settled.delete(scanId);
                next = settled;
            }
            continue;
        }
        next = applyEvent(next, event, now);
    }
    return next;
}

/** Removes toasts whose time is up. Also says whether a grey one timed out (the app then refetches the stock). */
export function expire(state: FeedState, now: number): { state: FeedState; loadingTimedOut: boolean } {
    let loadingTimedOut = false;
    let next: Map<string, FeedToast> | null = null;
    for (const [id, toast] of state) {
        if (toast.expiresAt !== null && toast.expiresAt <= now) {
            next ??= new Map(state);
            next.delete(id);
            if (toast.kind === "loading") loadingTimedOut = true;
        }
    }
    return { state: next ?? state, loadingTimedOut };
}

/** Newest first; the newest 3 are shown, the rest counted. */
export function visibleToasts(state: FeedState): { shown: FeedToast[]; more: number } {
    const all = [...state.values()].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
    return { shown: all.slice(0, MAX_VISIBLE), more: Math.max(0, all.length - MAX_VISIBLE) };
}

/** Whether an event changes stock, so the stock list should refetch. */
export const changesStock = (event: FeedEvent) =>
    event.type === "scanIdentified" ||
    event.type === "productNamed" ||
    event.type === "checkOutUnmatched" ||
    event.type === "scanNeedsIdentification" ||
    event.type === "storeLabelResolved";

/** The toast subtitle: "Check-in scanner · just now", "· 5 minutes ago", "· 18:42", "· 18:42 yesterday", "· 15 Oct". */
export function toastSubtitle(toast: Pick<FeedToast, "role" | "at">, now: Date = new Date()): string {
    const scanner = toast.role === "in" ? "Check-in scanner" : "Check-out scanner";
    return `${scanner} · ${feedTime(toast.at, now)}`;
}

export function feedTime(iso: string, now: Date = new Date()): string {
    const at = new Date(iso);
    const minutes = Math.floor((now.getTime() - at.getTime()) / 60000);
    if (minutes < 1) return "just now";
    if (minutes < 60) return `${minutes} ${minutes === 1 ? "minute" : "minutes"} ago`;
    const day = (d: Date) => d.toLocaleDateString("en-CA", { timeZone: "Europe/London" });
    const time = at.toLocaleTimeString("en-GB", {
        hour: "2-digit",
        minute: "2-digit",
        timeZone: "Europe/London",
    });
    if (day(at) === day(now)) return time;
    if (day(at) === day(new Date(now.getTime() - 86_400_000))) return `${time} yesterday`;
    return at.toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: "Europe/London" });
}
