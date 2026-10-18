import { useSyncExternalStore } from "react";

/** A tiny router: the app has a handful of screens, so no library. The Static Web App serves index.html for any path. */
const subscribe = (onChange: () => void) => {
    window.addEventListener("popstate", onChange);
    return () => window.removeEventListener("popstate", onChange);
};

export function usePath(): string {
    return useSyncExternalStore(subscribe, () => window.location.pathname);
}

export function navigate(path: string): void {
    if (window.location.pathname === path) return;
    window.history.pushState(null, "", path);
    window.dispatchEvent(new PopStateEvent("popstate"));
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
