import type { ApiClient } from "./api";

/** Push notification helpers (#77). No screens: the notifications card and the features decide when to use them. */

export type PushSupport = "supported" | "needsInstall" | "unsupported";

export interface PushEnvironment {
    nav: Navigator;
    win: Window;
}

const defaultEnv = (): PushEnvironment => ({ nav: navigator, win: window });

const isAppleMobile = (nav: Navigator) =>
    /iPhone|iPad|iPod/.test(nav.userAgent) || (/Macintosh/.test(nav.userAgent) && nav.maxTouchPoints > 1);

const isInstalled = ({ nav, win }: PushEnvironment) =>
    win.matchMedia?.("(display-mode: standalone)").matches === true ||
    (nav as Navigator & { standalone?: boolean }).standalone === true;

/** Can this device get pushes? On iPhone and iPad only once the app is added to the Home Screen (spike #75). */
export function pushSupport(env: PushEnvironment = defaultEnv()): PushSupport {
    const { nav, win } = env;
    if ("serviceWorker" in nav && "PushManager" in win && "Notification" in win) return "supported";
    return isAppleMobile(nav) && !isInstalled(env) ? "needsInstall" : "unsupported";
}

/** A short label for this device, sent with its subscription ("iPhone", "Android", "Mac", ...). */
export function deviceLabel(nav: Navigator = navigator): string {
    const ua = nav.userAgent;
    return /iPhone/.test(ua)
        ? "iPhone"
        : /iPad/.test(ua) || (/Macintosh/.test(ua) && nav.maxTouchPoints > 1)
          ? "iPad"
          : /Android/.test(ua)
            ? "Android"
            : /Macintosh/.test(ua)
              ? "Mac"
              : /Windows/.test(ua)
                ? "Windows"
                : /Linux/.test(ua)
                  ? "Linux"
                  : "Browser";
}

export type SubscribeResult = "subscribed" | "denied" | "unsupported" | "notConfigured";

function toKey(base64Url: string): Uint8Array {
    const raw = atob(base64Url.replace(/-/g, "+").replace(/_/g, "/"));
    return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}

/**
 * Turns notifications on for this device. **Call it only from a tap**: iOS refuses a permission prompt that isn't
 * from a user gesture. Asks permission, subscribes with the API's VAPID key, and saves the subscription.
 */
export async function subscribe(
    api: ApiClient,
    env: PushEnvironment = defaultEnv(),
): Promise<SubscribeResult> {
    if (pushSupport(env) !== "supported") return "unsupported";
    const permission = await (env.win as Window & typeof globalThis).Notification.requestPermission();
    if (permission !== "granted") return "denied";
    const { publicKey } = await api.pushConfig();
    if (!publicKey) return "notConfigured";
    const registration = await env.nav.serviceWorker.ready;
    const subscription =
        (await registration.pushManager.getSubscription()) ??
        (await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: toKey(publicKey) as BufferSource,
        }));
    await api.putPushSubscription({ ...subscription.toJSON(), deviceLabel: deviceLabel(env.nav) });
    return "subscribed";
}

/** Turns notifications off for this device: unsubscribes in the browser and removes it from the API. */
export async function unsubscribe(api: ApiClient, env: PushEnvironment = defaultEnv()): Promise<void> {
    if (pushSupport(env) !== "supported") return;
    const subscription = await (await env.nav.serviceWorker.ready).pushManager.getSubscription();
    if (!subscription) return;
    const endpoint = subscription.endpoint;
    await subscription.unsubscribe();
    await api.deletePushSubscription(endpoint);
}

/** Saves which notification types this device wants. False if notifications aren't on here. */
export async function setPreferences(
    api: ApiClient,
    preferences: Record<string, boolean>,
    env: PushEnvironment = defaultEnv(),
): Promise<boolean> {
    if (pushSupport(env) !== "supported") return false;
    const subscription = await (await env.nav.serviceWorker.ready).pushManager.getSubscription();
    if (!subscription) return false;
    await api.setPushPreferences(subscription.endpoint, preferences);
    return true;
}

/**
 * On every app open (#77): if notifications are on here, re-save the subscription so the API knows the device is
 * still in use (subscriptions not seen for 30 days are removed). If the browser dropped it but permission is still
 * granted, subscribe again (no prompt is shown when permission is already granted). Never asks for permission.
 */
export async function refreshPushSubscription(
    api: ApiClient,
    env: PushEnvironment = defaultEnv(),
): Promise<void> {
    const win = env.win as Window & typeof globalThis;
    if (pushSupport(env) !== "supported" || win.Notification.permission !== "granted") return;
    const registration = await env.nav.serviceWorker.ready;
    let subscription = await registration.pushManager.getSubscription();
    if (!subscription) {
        const { publicKey } = await api.pushConfig();
        if (!publicKey) return;
        subscription = await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: toKey(publicKey) as BufferSource,
        });
    }
    await api.putPushSubscription({ ...subscription.toJSON(), deviceLabel: deviceLabel(env.nav) });
}

/**
 * Seen receipts (#77): `markSeen("scan:<id>")` when the app shows an event on a visible screen, so the server leaves
 * this person out of the push for it. Keys are batched for a moment and only sent while the page is visible;
 * fire-and-forget (a lost receipt only means an extra push).
 */
export function createSeenReporter(
    api: Pick<ApiClient, "pushSeen">,
    doc: Document = document,
    delayMs = 1500,
) {
    const pending = new Set<string>();
    let timer: ReturnType<typeof setTimeout> | undefined;

    const flush = () => {
        timer = undefined;
        if (doc.visibilityState !== "visible" || pending.size === 0) return;
        const keys = [...pending].slice(0, 50);
        keys.forEach((k) => pending.delete(k));
        api.pushSeen(keys).catch((error: unknown) => console.warn("Couldn't send seen receipts", error));
        if (pending.size > 0) timer = setTimeout(flush, delayMs);
    };

    return function markSeen(eventKey: string): void {
        if (doc.visibilityState !== "visible") return; // only what someone could actually see counts
        pending.add(eventKey);
        timer ??= setTimeout(flush, delayMs);
    };
}
