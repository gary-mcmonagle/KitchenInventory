import { useEffect, useRef } from "react";
import { SignInRequiredError, type ApiClient } from "./api";

/** While the List is on screen, live changes tell the API at most this often (#86). */
export const VIEWED_EVERY_MS = 60_000;

const visible = () => typeof document === "undefined" || document.visibilityState === "visible";
const online = () => typeof navigator === "undefined" || navigator.onLine !== false;

/**
 * Tells the API this person is looking at the List (#86), so a list push only mentions what they haven't seen: when
 * the List screen opens or the app comes back to it, and, while it's on screen, when the list changes (live updates
 * arriving), at most once a minute. Best-effort: a failure just means a push may mention something they saw.
 */
export function useListViewed(
    api: Pick<ApiClient, "shoppingListViewed">,
    changes: unknown,
    now: () => number = Date.now,
) {
    const last = useRef<number | null>(null);
    const first = useRef(true);

    useEffect(() => {
        const tell = () => {
            if (!visible() || !online()) return;
            last.current = now();
            api.shoppingListViewed().catch((error: unknown) => {
                if (!(error instanceof SignInRequiredError))
                    console.warn("Couldn't record that the list was viewed", error);
            });
        };
        tell();
        const onVisibility = () => visible() && tell();
        document.addEventListener("visibilitychange", onVisibility);
        return () => document.removeEventListener("visibilitychange", onVisibility);
    }, [api, now]);

    useEffect(() => {
        if (first.current) {
            first.current = false;
            return; // the open itself was told above
        }
        if (!visible() || !online() || (last.current !== null && now() - last.current < VIEWED_EVERY_MS))
            return;
        last.current = now();
        api.shoppingListViewed().catch((error: unknown) => {
            if (!(error instanceof SignInRequiredError))
                console.warn("Couldn't record that the list was viewed", error);
        });
    }, [api, changes, now]);
}
