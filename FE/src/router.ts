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

const RESET_EVENT = "cupboard:tab-reset";

/**
 * A bottom-nav tab: on another page it navigates there; on that page already it starts the page afresh, as arriving
 * from another tab does (Gary, 2026-10-22): the screen remounts, at the top.
 */
export function openTab(path: string): void {
    if (window.location.pathname !== path) {
        navigate(path);
        return;
    }
    scrollPositions.delete(path);
    window.dispatchEvent(new Event(RESET_EVENT));
    window.scrollTo(0, 0);
}

const subscribeResets = (onChange: () => void) => {
    const counted = () => {
        resets++;
        onChange();
    };
    window.addEventListener(RESET_EVENT, counted);
    return () => window.removeEventListener(RESET_EVENT, counted);
};
let resets = 0;

/** Runs before a reset's remount (e.g. to forget a remembered search). Returns the unsubscribe. */
export function onTabReset(run: (path: string) => void): () => void {
    const listener = () => run(window.location.pathname);
    window.addEventListener(RESET_EVENT, listener);
    return () => window.removeEventListener(RESET_EVENT, listener);
}

/** How many times a tab has been reset: use it as the tab screens' key, so a reset remounts them. */
export function useTabResets(): number {
    return useSyncExternalStore(subscribeResets, () => resets);
}

/** The screen this one was opened from, if it was opened inside the app (#103). */
export const cameFrom = (): string | null => (window.history.state as { from?: string } | null)?.from ?? null;

/** ‹: a real history step back when this screen was opened inside the app (so ‹, the browser's back and the iOS
 * swipe agree, and history doesn't grow), otherwise to `fallback` (e.g. a deep link to item detail). */
export function goBack(fallback: string): void {
    if (cameFrom()) window.history.back();
    else navigate(fallback);
}

/** Settings (#122): Scanners, This phone and, later, AI models. It was the Scanners screen at `/scanners` until then. */
export const SETTINGS_PATH = "/settings";

/** The old address of Settings: bookmarks and links to it are sent to `SETTINGS_PATH`. */
export const LEGACY_SCANNERS_PATH = "/scanners";
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
