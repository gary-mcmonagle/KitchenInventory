import { HubConnectionState } from "@microsoft/signalr";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { ApiError, type ApiClient } from "./api";
import type { FeedEvent } from "./scanFeed";
import { ScanFeedView } from "./ScanFeedView";
import { ThisPhoneCard } from "./ThisPhoneCard";
import { useScanFeed, type FeedConnection } from "./useScanFeed";

// Stock change pushes (#80): seen receipts for green toasts, and the "This phone" card.

// ---- Seen receipts

class FakeConnection implements FeedConnection {
    handlers = new Map<string, (payload: unknown) => void>();
    state: string = HubConnectionState.Disconnected;
    on(event: string, handler: (payload: unknown) => void) {
        this.handlers.set(event, handler);
    }
    onreconnected() {}
    async start() {
        this.state = HubConnectionState.Connected;
    }
    async stop() {
        this.state = HubConnectionState.Disconnected;
    }
    emit(event: FeedEvent) {
        const { type, ...payload } = event;
        this.handlers.get(type)!(payload);
    }
}

const NOW = Date.parse("2026-10-19T13:00:00.000Z");
const at = (offsetMs = 0) => new Date(NOW + offsetMs).toISOString();

let visibility: DocumentVisibilityState = "visible";

function Harness({
    api,
    connection,
    markSeen,
}: {
    api: ApiClient;
    connection: FakeConnection;
    markSeen: (k: string) => void;
}) {
    const { feed, now, dismiss } = useScanFeed(
        api,
        () => undefined,
        () => connection,
        markSeen,
    );
    return <ScanFeedView feed={feed} now={now} onDismiss={(t) => void dismiss(t)} />;
}

function feedSetup(catchUp: FeedEvent[] = []) {
    const api = { scanFeed: vi.fn(async () => ({ generatedAt: at(), events: catchUp })) };
    const connection = new FakeConnection();
    const markSeen = vi.fn();
    render(<Harness api={api as unknown as ApiClient} connection={connection} markSeen={markSeen} />);
    return { connection, markSeen };
}

const flush = () => act(async () => {});

const identified = (
    scanId: string,
    outcome: "checkedIn" | "checkedOut" = "checkedIn",
    offsetMs = 0,
): FeedEvent => ({
    type: "scanIdentified",
    scanId,
    deviceId: "scanner-in",
    role: outcome === "checkedIn" ? "in" : "out",
    occurredAt: at(offsetMs),
    outcome,
    productId: "p-gravy",
    displayName: "Beef Gravy",
    brand: null,
    size: null,
    unitId: scanId,
});

describe("seen receipts", () => {
    beforeEach(() => {
        vi.useFakeTimers({ toFake: ["Date", "setTimeout", "clearTimeout", "setInterval", "clearInterval"] });
        vi.setSystemTime(NOW);
        visibility = "visible";
        Object.defineProperty(document, "visibilityState", { configurable: true, get: () => visibility });
    });
    afterEach(() => vi.useRealTimers());

    test("a green check-in or check-out toast shown on a visible page is reported once", async () => {
        const { connection, markSeen } = feedSetup();
        await flush();

        act(() => connection.emit(identified("s1")));
        act(() => connection.emit(identified("s1"))); // a replay
        act(() => connection.emit(identified("s2", "checkedOut", 1000)));
        act(() => vi.advanceTimersByTime(3000));

        expect(markSeen.mock.calls).toEqual([["scan:s1"], ["scan:s2"]]);
    });

    test("amber and not-in-stock toasts are never reported", async () => {
        const { connection, markSeen } = feedSetup();
        await flush();

        act(() =>
            connection.emit({
                type: "scanNeedsIdentification",
                scanId: "m1",
                deviceId: "scanner-in",
                role: "in",
                occurredAt: at(),
                productId: "p-x",
                barcode: "501001234567",
                displayName: "Unknown item",
                productStatus: "noMatch",
                unitId: "m1",
            }),
        );
        act(() =>
            connection.emit({
                type: "checkOutUnmatched",
                scanId: "u1",
                deviceId: "scanner-out",
                role: "out",
                occurredAt: at(),
                barcode: "5000354922831",
                productId: null,
                displayName: null,
            }),
        );
        act(() => vi.advanceTimersByTime(2000));

        expect(markSeen).not.toHaveBeenCalled();
    });

    test("a toast that arrives while the page is hidden counts only if it's still showing when the page is back", async () => {
        const { connection, markSeen } = feedSetup();
        await flush();
        visibility = "hidden";

        act(() => connection.emit(identified("s1")));
        act(() => vi.advanceTimersByTime(3000));
        expect(markSeen).not.toHaveBeenCalled();

        visibility = "visible";
        act(() => vi.advanceTimersByTime(1000)); // the next tick
        expect(markSeen).toHaveBeenCalledWith("scan:s1");

        act(() => connection.emit(identified("s2", "checkedIn", 4000)));
        visibility = "hidden";
        act(() => vi.advanceTimersByTime(11_000)); // gone before anyone looked... but it was shown while visible
        expect(markSeen).toHaveBeenCalledWith("scan:s2");
    });

    test("a recent outcome from catch-up is shown and counts; old news isn't shown and doesn't", async () => {
        const { markSeen } = feedSetup([
            identified("recent", "checkedIn", -3000),
            identified("old", "checkedIn", -60_000),
        ]);
        await flush();
        act(() => vi.advanceTimersByTime(1000));

        expect(markSeen.mock.calls).toEqual([["scan:recent"]]);
    });

    test("only the toasts actually shown count, not the '+n more' waiting", async () => {
        const { connection, markSeen } = feedSetup();
        await flush();
        visibility = "hidden";
        for (let i = 0; i < 4; i++) act(() => connection.emit(identified(`s${i}`, "checkedIn", i * 100)));
        visibility = "visible";
        act(() => vi.advanceTimersByTime(1000));

        expect(markSeen.mock.calls.flat().sort()).toEqual(["scan:s1", "scan:s2", "scan:s3"]); // the oldest is waiting
    });
});

// ---- The "This phone" card

const IPHONE = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_4 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148";
const MAC_CHROME = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Chrome/130";

function phone(
    options: {
        ua?: string;
        push?: boolean;
        permission?: NotificationPermission;
        existing?: boolean;
        preferences?: Record<string, boolean>;
        grant?: NotificationPermission;
    } = {},
) {
    const {
        ua = MAC_CHROME,
        push = true,
        permission = "default",
        preferences = {},
        grant = "granted",
    } = options;
    let existing = options.existing ?? false;
    const json = { endpoint: "https://fcm.googleapis.com/fcm/send/x", keys: { p256dh: "p", auth: "a" } };
    const sub = {
        endpoint: json.endpoint,
        toJSON: () => json,
        unsubscribe: vi.fn(async () => {
            existing = false;
            return true;
        }),
    };
    const pushManager = {
        getSubscription: vi.fn(async () => (existing ? sub : null)),
        subscribe: vi.fn(async () => {
            existing = true;
            return sub;
        }),
    };
    const notification = {
        permission,
        requestPermission: vi.fn(async () => {
            notification.permission = grant;
            return grant;
        }),
    };
    const nav = {
        userAgent: ua,
        maxTouchPoints: 0,
        ...(push ? { serviceWorker: { ready: Promise.resolve({ pushManager }) } } : {}),
    } as unknown as Navigator;
    const win = {
        matchMedia: () => ({ matches: false }),
        ...(push ? { PushManager: class {}, Notification: notification } : {}),
    } as unknown as Window;
    const api = {
        pushConfig: vi.fn(async () => ({
            publicKey:
                "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4",
        })),
        putPushSubscription: vi.fn(async () => ({ id: "id", deviceLabel: "Mac", preferences })),
        deletePushSubscription: vi.fn(async () => ({ removed: true })),
        setPushPreferences: vi.fn(async (_: string, p: Record<string, boolean>) => ({
            id: "id",
            deviceLabel: "Mac",
            preferences: p,
        })),
        pushTest: vi.fn(async () => ({ targeted: 1, delivered: 1, removed: 0, failed: 0 })),
    };
    const env = { nav, win };
    render(<ThisPhoneCard api={api as unknown as ApiClient} env={env} />);
    return { api, notification, pushManager, sub, json };
}

const card = () => screen.getByRole("region", { name: /This phone/ });

describe("the This phone card", () => {
    test("not supported", async () => {
        phone({ push: false });
        expect(await screen.findByText("This phone can't show notifications.")).toBeInTheDocument();
        expect(screen.queryByRole("button")).not.toBeInTheDocument();
    });

    test("iPhone in Safari: add to the Home Screen first", async () => {
        phone({ ua: IPHONE, push: false });
        expect(await screen.findByText("Needs the Home Screen app")).toBeInTheDocument();
        expect(card()).toHaveTextContent(
            "To get notifications on iPhone, add Cupboard to your Home Screen and open it from there.",
        );
        expect(screen.getAllByRole("listitem").map((li) => li.textContent)).toEqual([
            "1Tap Share",
            "2Choose Add to Home Screen",
            "3Open Cupboard from your Home Screen",
        ]);
    });

    test("blocked", async () => {
        phone({ permission: "denied" });
        expect(await screen.findByText(/Notifications are blocked for Cupboard/)).toBeInTheDocument();
        expect(card()).toHaveTextContent("Blocked in your settings");
        expect(screen.queryByRole("button")).not.toBeInTheDocument();
    });

    test("off: permission is asked only from the Turn on tap, and stock changes and the list are on by default", async () => {
        const { api, notification, json } = phone();
        const turnOn = await screen.findByRole("button", { name: "Turn on notifications" });
        expect(card()).toHaveTextContent("Off for this phone");
        expect(notification.requestPermission).not.toHaveBeenCalled();

        await act(async () => fireEvent.click(turnOn));

        expect(notification.requestPermission).toHaveBeenCalledTimes(1);
        expect(api.putPushSubscription).toHaveBeenCalledWith(
            expect.objectContaining({ endpoint: json.endpoint }),
        );
        expect(api.setPushPreferences).toHaveBeenCalledWith(json.endpoint, {
            stockChanges: true,
            shoppingList: true,
            stillGotThis: true,
        });
        expect(screen.getByRole("switch", { name: "Stock changes" })).toHaveAttribute("aria-checked", "true");
        expect(screen.getByRole("switch", { name: "Shopping list" })).toHaveAttribute("aria-checked", "true");
        expect(screen.getByRole("switch", { name: "Still got this?" })).toHaveAttribute(
            "aria-checked",
            "true",
        );
        expect(card()).toHaveTextContent("On for this phone");
    });

    test("off: the card says what notifications are for, as drawn", async () => {
        phone();
        await screen.findByRole("button", { name: "Turn on notifications" });
        expect(card()).toHaveTextContent(
            "Get a notification when stock is added or removed and you didn't see it in the app, or when something goes on the shopping list.",
        );
    });

    test("on: the Shopping list switch is on until turned off (#86), and saves this device's choice", async () => {
        const { api, json } = phone({
            permission: "granted",
            existing: true,
            preferences: { stockChanges: true },
        });
        const toggle = await screen.findByRole("switch", { name: "Shopping list" });
        expect(toggle).toHaveAttribute("aria-checked", "true"); // turned on before #86: no key yet
        expect(card()).toHaveTextContent("When someone else adds to the list");

        await act(async () => fireEvent.click(toggle));

        expect(api.setPushPreferences).toHaveBeenCalledWith(json.endpoint, { shoppingList: false });
        expect(toggle).toHaveAttribute("aria-checked", "false");
        expect(screen.getByRole("switch", { name: "Stock changes" })).toHaveAttribute("aria-checked", "true");
    });

    test("turning on but saying no to the prompt shows blocked", async () => {
        phone({ grant: "denied" });
        const button1 = await screen.findByRole("button", { name: "Turn on notifications" });
        await act(async () => fireEvent.click(button1));
        expect(card()).toHaveTextContent("Blocked in your settings");
    });

    test("on: the toggle saves this device's preference", async () => {
        const { api, json } = phone({
            permission: "granted",
            existing: true,
            preferences: { stockChanges: true },
        });
        const toggle = await screen.findByRole("switch", { name: "Stock changes" });
        expect(toggle).toHaveAttribute("aria-checked", "true");
        expect(card()).toHaveTextContent("Quiet from 21:30 to 07:30: nothing is sent overnight.");

        await act(async () => fireEvent.click(toggle));

        expect(api.setPushPreferences).toHaveBeenCalledWith(json.endpoint, { stockChanges: false });
        expect(toggle).toHaveAttribute("aria-checked", "false");
    });

    test("on: Send a test, and a polite answer when there have been too many", async () => {
        const { api } = phone({ permission: "granted", existing: true, preferences: { stockChanges: true } });
        const button2 = await screen.findByRole("button", { name: "Send a test" });
        await act(async () => fireEvent.click(button2));
        expect(api.pushTest).toHaveBeenCalled();
        expect(screen.getByRole("button", { name: "Test sent" })).toBeInTheDocument();

        api.pushTest.mockRejectedValueOnce(new ApiError(429, "too many"));
        await act(async () => fireEvent.click(screen.getByRole("button", { name: "Test sent" })));
        expect(screen.getByRole("button", { name: "Try again later" })).toBeInTheDocument();
    });

    test("on: Turn off on this phone unsubscribes and removes it", async () => {
        const { api, sub, json } = phone({
            permission: "granted",
            existing: true,
            preferences: { stockChanges: true },
        });
        const button3 = await screen.findByRole("button", { name: "Turn off on this phone" });
        await act(async () => fireEvent.click(button3));

        expect(sub.unsubscribe).toHaveBeenCalled();
        expect(api.deletePushSubscription).toHaveBeenCalledWith(json.endpoint);
        expect(screen.getByRole("button", { name: "Turn on notifications" })).toBeInTheDocument();
    });

    test("granted but no subscription here is off", async () => {
        phone({ permission: "granted" });
        expect(await screen.findByRole("button", { name: "Turn on notifications" })).toBeInTheDocument();
    });
});
