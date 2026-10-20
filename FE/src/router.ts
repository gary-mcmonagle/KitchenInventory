import { useSyncExternalStore } from "react";

/** A tiny router: the app has a handful of screens, so no library. The Static Web App serves index.html for any path. */
const subscribe = (onChange: () => void) => {
    window.addEventListener("popstate", onChange);
    return () => window.removeEventListener("popstate", onChange);
};

export function usePath(): string {
    return useSyncExternalStore(subscribe, () => window.location.pathname);
}

/** Where each screen was scrolled when we last left it (spike #99), so going back can land where you were. */
const scrollPositions = new Map<string, number>();

export const savedScroll = (path: string): number => scrollPositions.get(path) ?? 0;

/** For tests: forget saved positions, as after a reload. */
export const forgetScrollPositions = () => scrollPositions.clear();

export function navigate(path: string): void {
    if (window.location.pathname === path) return;
    scrollPositions.set(window.location.pathname, window.scrollY);
    // Remember where we came from (spike #99), so "‹ Stock" can go back instead of stacking a new entry.
    window.history.pushState({ from: window.location.pathname }, "", path);
    window.dispatchEvent(new PopStateEvent("popstate"));
}

/** The screen this one was opened from, if it was opened inside the app (#103). */
export const cameFrom = (): string | null => (window.history.state as { from?: string } | null)?.from ?? null;

/** ‹: a real history step back when this screen was opened inside the app (so ‹, the browser's back and the iOS
 * swipe agree, and history doesn't grow), otherwise to `fallback` (e.g. a deep link to item detail). */
export function goBack(fallback: string): void {
    if (cameFrom()) window.history.back();
    else navigate(fallback);
}

export const SCANNERS_PATH = "/scanners";
export const ADD_PATH = "/add";
export const RECIPES_PATH = "/recipes";
export const LIST_PATH = "/list";

/** "What is this?" for a store-label check-in (#58). */
export const assignPath = (scanId: string) => `/store-labels/${encodeURIComponent(scanId)}`;

/** The scan ID if the path is assigning a store label (#58). */
export function matchAssignPath(path: string): string | null {
    const match = /^\/store-labels\/([^/]+)\/?$/.exec(path);
    return match ? decodeURIComponent(match[1]) : null;
}

export const namePath = (productId: string) => `/products/${encodeURIComponent(productId)}/name`;
export const itemPath = (productId: string) => `/products/${encodeURIComponent(productId)}`;
export const editPath = (productId: string) => `/products/${encodeURIComponent(productId)}/edit`;

/** The product ID if the path is item detail (#53). */
export function matchItemPath(path: string): string | null {
    const match = /^\/products\/([^/]+)\/?$/.exec(path);
    return match ? decodeURIComponent(match[1]) : null;
}

/** The product ID if the path is editing a product's details (#53). */
export function matchEditPath(path: string): string | null {
    const match = /^\/products\/([^/]+)\/edit\/?$/.exec(path);
    return match ? decodeURIComponent(match[1]) : null;
}

/** The product ID if the path is a naming screen. */
export function matchNamePath(path: string): string | null {
    const match = /^\/products\/([^/]+)\/name\/?$/.exec(path);
    return match ? decodeURIComponent(match[1]) : null;
}
