import type { ApiClient } from "./api";
import swText from "../public/sw.js?raw";
import {
    createSeenReporter,
    pushSupport,
    refreshPushSubscription,
    setPreferences,
    subscribe,
    unsubscribe,
} from "./push";

const IPHONE = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_4 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148";
const MAC_CHROME = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Chrome/130";
// A real P-256 public key (RFC 8291's), as the API would serve it.
const PUBLIC_KEY = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";

function env(
    options: {
        ua?: string;
        push?: boolean;
        standalone?: boolean;
        permission?: NotificationPermission;
        existing?: boolean;
    } = {},
) {
    const {
        ua = MAC_CHROME,
        push = true,
        standalone = false,
        permission = "default",
        existing = false,
    } = options;
    const json = { endpoint: "https://fcm.googleapis.com/fcm/send/x", keys: { p256dh: "p", auth: "a" } };
    const sub = { endpoint: json.endpoint, toJSON: () => json, unsubscribe: vi.fn(async () => true) };
    const pushManager = {
        getSubscription: vi.fn(async () => (existing ? sub : null)),
        subscribe: vi.fn(async () => sub),
    };
    const requestPermission = vi.fn(async () => "granted" as NotificationPermission);
    const nav = {
        userAgent: ua,
        maxTouchPoints: 0,
        ...(push ? { serviceWorker: { ready: Promise.resolve({ pushManager }) } } : {}),
    } as unknown as Navigator;
    const win = {
        matchMedia: () => ({ matches: standalone }),
        ...(push ? { PushManager: class {}, Notification: { requestPermission, permission } } : {}),
    } as unknown as Window;
    const api = {
        pushConfig: vi.fn(async () => ({ publicKey: PUBLIC_KEY as string | null })),
        putPushSubscription: vi.fn(async () => ({ id: "id", deviceLabel: "Mac", preferences: {} })),
        deletePushSubscription: vi.fn(async () => ({ removed: true })),
        setPushPreferences: vi.fn(async () => ({ id: "id", deviceLabel: "Mac", preferences: {} })),
        pushSeen: vi.fn(async () => undefined),
    };
    return {
        e: { nav, win },
        api: api as unknown as ApiClient & typeof api,
        pushManager,
        requestPermission,
        sub,
        json,
    };
}

describe("pushSupport", () => {
    test("supported where the browser has service workers, PushManager and Notification", () => {
        expect(pushSupport(env().e)).toBe("supported");
    });

    test("an iPhone in Safari (not installed) needs the Home Screen; elsewhere it's unsupported", () => {
        expect(pushSupport(env({ ua: IPHONE, push: false }).e)).toBe("needsInstall");
        expect(pushSupport(env({ ua: IPHONE, push: false, standalone: true }).e)).toBe("unsupported");
        expect(pushSupport(env({ push: false }).e)).toBe("unsupported");
    });
});

describe("subscribe", () => {
    test("asks permission, subscribes with the API's key, and saves the subscription", async () => {
        const { e, api, pushManager, requestPermission, json } = env();

        expect(await subscribe(api, e)).toBe("subscribed");

        expect(requestPermission).toHaveBeenCalled();
        const options = pushManager.subscribe.mock.calls[0] as unknown as [
            { userVisibleOnly: boolean; applicationServerKey: Uint8Array },
        ];
        expect(options[0].userVisibleOnly).toBe(true);
        expect(options[0].applicationServerKey).toHaveLength(65);
        expect(api.putPushSubscription).toHaveBeenCalledWith(json);
    });

    test("stops when permission is refused, the keys aren't set, or push isn't supported", async () => {
        const denied = env();
        denied.requestPermission.mockResolvedValueOnce("denied");
        expect(await subscribe(denied.api, denied.e)).toBe("denied");
        expect(denied.api.putPushSubscription).not.toHaveBeenCalled();

        const noKeys = env();
        noKeys.api.pushConfig.mockResolvedValueOnce({ publicKey: null });
        expect(await subscribe(noKeys.api, noKeys.e)).toBe("notConfigured");

        const iphone = env({ ua: IPHONE, push: false });
        expect(await subscribe(iphone.api, iphone.e)).toBe("unsupported");
    });
});

test("unsubscribe removes the subscription in the browser and in the API", async () => {
    const { e, api, sub } = env({ existing: true });
    await unsubscribe(api, e);
    expect(sub.unsubscribe).toHaveBeenCalled();
    expect(api.deletePushSubscription).toHaveBeenCalledWith(sub.endpoint);
});

test("setPreferences saves for this device's subscription, if there is one", async () => {
    const on = env({ existing: true });
    expect(await setPreferences(on.api, { stockChanges: true }, on.e)).toBe(true);
    expect(on.api.setPushPreferences).toHaveBeenCalledWith(on.sub.endpoint, { stockChanges: true });

    const off = env();
    expect(await setPreferences(off.api, { stockChanges: true }, off.e)).toBe(false);
});

describe("refreshPushSubscription (on app open)", () => {
    test("re-saves an existing subscription, never asking for permission", async () => {
        const { e, api, requestPermission, json } = env({ permission: "granted", existing: true });
        await refreshPushSubscription(api, e);
        expect(api.putPushSubscription).toHaveBeenCalledWith(json);
        expect(requestPermission).not.toHaveBeenCalled();
    });

    test("subscribes again if the browser dropped it but permission is still granted", async () => {
        const { e, api, pushManager } = env({ permission: "granted" });
        await refreshPushSubscription(api, e);
        expect(pushManager.subscribe).toHaveBeenCalled();
        expect(api.putPushSubscription).toHaveBeenCalled();
    });

    test("does nothing until notifications are turned on", async () => {
        const { e, api } = env({ permission: "default", existing: true });
        await refreshPushSubscription(api, e);
        expect(api.putPushSubscription).not.toHaveBeenCalled();
    });
});

describe("markSeen", () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    test("batches keys for a moment and sends them once, only while visible", async () => {
        const doc = { visibilityState: "visible" } as Document;
        const api = { pushSeen: vi.fn(async () => undefined) };
        const markSeen = createSeenReporter(api, doc, 1500);

        markSeen("scan:1");
        markSeen("scan:2");
        markSeen("scan:1");
        expect(api.pushSeen).not.toHaveBeenCalled();
        await vi.advanceTimersByTimeAsync(1500);
        expect(api.pushSeen).toHaveBeenCalledExactlyOnceWith(["scan:1", "scan:2"]);
    });

    test("ignores keys while the page is hidden, and holds pending ones until it's visible", async () => {
        const doc = { visibilityState: "hidden" } as { visibilityState: DocumentVisibilityState };
        const api = { pushSeen: vi.fn(async () => undefined) };
        const markSeen = createSeenReporter(api, doc as Document, 1500);

        markSeen("scan:hidden");
        await vi.advanceTimersByTimeAsync(2000);
        expect(api.pushSeen).not.toHaveBeenCalled();

        doc.visibilityState = "visible";
        markSeen("scan:3");
        doc.visibilityState = "hidden";
        await vi.advanceTimersByTimeAsync(1500);
        expect(api.pushSeen).not.toHaveBeenCalled();
    });
});

describe("the service worker's push handlers", () => {
    function worker(
        windows: Array<{
            url: string;
            focus: () => Promise<unknown>;
            navigate: (u: string) => Promise<unknown>;
        }> = [],
    ) {
        const listeners: Record<string, (event: unknown) => void> = {};
        const showNotification = vi.fn(async () => undefined);
        const openWindow = vi.fn(async () => null);
        const self = {
            addEventListener: (type: string, fn: (event: unknown) => void) => (listeners[type] = fn),
            skipWaiting: () => undefined,
            registration: { showNotification },
            clients: { claim: async () => undefined, matchAll: async () => windows, openWindow },
            location: { origin: "https://kitchen.garymcmonagle.com" },
        };
        new Function("self", swText)(self);
        const run = async (type: string, event: Record<string, unknown>) => {
            let work: Promise<unknown> = Promise.resolve();
            listeners[type]({ ...event, waitUntil: (p: Promise<unknown>) => (work = p) });
            await work;
        };
        return { run, showNotification, openWindow };
    }

    test("a push shows its notification from the declarative payload, with the tag and tap URL", async () => {
        const { run, showNotification } = worker();
        const payload = {
            web_push: 8030,
            notification: {
                title: "Cupboard updated",
                body: "Added Milk",
                navigate: "https://kitchen.garymcmonagle.com/?from=push",
                tag: "stock",
            },
        };

        await run("push", { data: { json: () => payload, text: () => JSON.stringify(payload) } });

        expect(showNotification).toHaveBeenCalledWith(
            "Cupboard updated",
            expect.objectContaining({
                body: "Added Milk",
                tag: "stock",
                data: { navigate: "https://kitchen.garymcmonagle.com/?from=push" },
            }),
        );
    });

    test("even an empty or unreadable push shows something (Safari revokes silent ones)", async () => {
        const { run, showNotification } = worker();
        await run("push", {
            data: {
                json: () => {
                    throw new Error("not JSON");
                },
                text: () => "hello",
            },
        });
        await run("push", {});
        expect(showNotification).toHaveBeenCalledTimes(2);
        expect(showNotification).toHaveBeenNthCalledWith(
            1,
            "Cupboard",
            expect.objectContaining({ body: "hello" }),
        );
    });

    test("tapping focuses an open app window and takes it to the URL", async () => {
        const open = {
            url: "https://kitchen.garymcmonagle.com/scanners",
            focus: vi.fn(async () => undefined),
            navigate: vi.fn(async () => undefined),
        };
        const { run, openWindow } = worker([open]);
        const close = vi.fn();

        await run("notificationclick", {
            notification: { close, data: { navigate: "https://kitchen.garymcmonagle.com/?from=push" } },
        });

        expect(close).toHaveBeenCalled();
        expect(open.focus).toHaveBeenCalled();
        expect(open.navigate).toHaveBeenCalledWith("https://kitchen.garymcmonagle.com/?from=push");
        expect(openWindow).not.toHaveBeenCalled();
    });

    test("tapping with no app open opens one, and never a URL on another site", async () => {
        const { run, openWindow } = worker();
        await run("notificationclick", {
            notification: { close: () => undefined, data: { navigate: "/scanners" } },
        });
        await run("notificationclick", {
            notification: { close: () => undefined, data: { navigate: "https://evil.example/" } },
        });
        expect(openWindow.mock.calls).toEqual([
            ["https://kitchen.garymcmonagle.com/scanners"],
            ["https://kitchen.garymcmonagle.com/"],
        ]);
    });
});
