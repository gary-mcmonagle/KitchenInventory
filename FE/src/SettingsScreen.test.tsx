import { act, fireEvent, render, screen, within } from "@testing-library/react";
import type { ApiClient, Scanners } from "./api";
import { SettingsScreen } from "./SettingsScreen";
import { ScanFeedView } from "./ScanFeedView";
import { applyEvent, type FeedState } from "./scanFeed";
import type { FeedConnection } from "./useScanFeed";

// 08:00 UTC = 09:00 BST on Sunday 18 October 2026.
const NOW = new Date("2026-10-18T08:00:00.000Z");

const base = (overrides: Partial<Scanners> = {}): Scanners => ({
    generatedAt: "2026-10-18T07:58:00.000Z",
    hub: {
        hubId: "cupboard-pi",
        lastSeenAt: "2026-10-18T07:58:00.000Z",
        heartbeatIntervalSeconds: 120,
        offlineThresholdMinutes: 20,
        online: true,
    },
    devices: [
        {
            deviceId: "scanner-in",
            role: "in",
            present: true,
            presentSince: "2026-10-17T07:00:00Z",
            absentSince: null,
            lastScanAt: "2026-10-17T17:42:00.000Z",
        },
        {
            deviceId: "scanner-out",
            role: "out",
            present: false,
            presentSince: null,
            absentSince: "2026-10-18T07:15:00.000Z",
            lastScanAt: null,
        },
    ],
    noMatchCheckIns: [
        {
            productId: "p 1",
            barcode: "501001234567",
            status: "noMatch",
            scannedAt: "2026-10-17T17:42:00.000Z",
            unitId: "u1",
            inStockCount: 1,
            scanId: null,
        },
    ],
    unmatchedCheckOuts: [
        {
            scanId: "s1",
            barcode: "5000354922831",
            productId: "p2",
            displayName: "Heinz tomato ketchup",
            scannedAt: "2026-10-14T18:03:00.000Z",
            storeLabel: false,
        },
        {
            scanId: "s2",
            barcode: "501001239981",
            productId: null,
            displayName: null,
            scannedAt: "2026-10-12T07:40:00.000Z",
            storeLabel: false,
        },
    ],
    recentlyCheckedOut: [
        {
            unitId: "r1",
            productId: "p3",
            displayName: "Beef Gravy",
            checkedOutAt: "2026-10-18T07:30:00.000Z",
            source: "scanner",
        },
        {
            unitId: "r2",
            productId: "p4",
            displayName: "Milk",
            checkedOutAt: "2026-10-17T19:00:00.000Z",
            source: "ui",
        },
    ],
    ...overrides,
});

const noFeed = (): FeedConnection => ({
    on() {},
    onreconnected() {},
    start: () => Promise.resolve(),
    stop: () => Promise.resolve(),
    state: "Connected",
});

function setup(data: Scanners[] = [base()]) {
    const queue = [...data];
    const api = {
        scanners: vi.fn(async () => (queue.length > 1 ? queue.shift()! : queue[0])),
        scanFeed: vi.fn(async () => ({ generatedAt: "", events: [] })),
        dismissUnmatched: vi.fn(async (scanId: string) => ({ scanId, dismissed: true })),
        undoCheckOut: vi.fn(async (unitId: string) => ({ unitId, productId: "p", count: 1, restored: true })),
    };
    render(<SettingsScreen api={api as unknown as ApiClient} feedConnection={noFeed} />);
    return api;
}

beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(NOW);
});
afterEach(() => vi.useRealTimers());

const region = (name: RegExp) => screen.getByRole("region", { name });

test("scanner cards, the Pi and the header, as in the mockup", async () => {
    setup();
    expect(await screen.findByRole("heading", { level: 1, name: "Settings" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 2, name: /^Scanners/ })).toHaveTextContent(
        "Last checked 2 minutes ago",
    );

    const checkIn = screen.getByText("Check-in scanner").closest<HTMLElement>("[class*=_card_]")!;
    expect(checkIn).toHaveTextContent("Last scan 18:42 yesterday");
    expect(within(checkIn).getByText("Connected").className).toMatch(/_ok_/);

    const checkOut = screen.getByText("Check-out scanner").closest<HTMLElement>("[class*=_card_]")!;
    expect(checkOut).toHaveTextContent("Unplugged since 08:15 today");
    expect(within(checkOut).getByText("Not connected").className).toMatch(/_bad_/);

    expect(screen.getByText("Reporting in every 2 minutes · last heard 2 minutes ago")).toBeInTheDocument();
    expect(screen.getByText("Online").className).toMatch(/_ok_/);
});

test("presence not reported yet is Unknown; an offline Pi says so", async () => {
    setup([
        base({
            hub: { ...base().hub!, online: false, lastSeenAt: "2026-10-18T07:00:00.000Z" },
            devices: [
                {
                    deviceId: "scanner-in",
                    role: "in",
                    present: null,
                    presentSince: null,
                    absentSince: null,
                    lastScanAt: null,
                },
            ],
        }),
    ]);
    expect((await screen.findByText("Unknown")).className).toMatch(/_unknown_/);
    expect(screen.getByText("No scans yet")).toBeInTheDocument();
    expect(screen.getByText("Offline").className).toMatch(/_bad_/);
});

test("no hub yet", async () => {
    setup([base({ hub: null })]);
    expect(await screen.findByText("Not heard from yet")).toBeInTheDocument();
});

test("check-ins with no match: Identify opens Name it", async () => {
    setup();
    const section = await screen.findByRole("region", { name: /Check-ins with no match/ });
    expect(section).toHaveTextContent("1 to identify");
    expect(section).toHaveTextContent("5010 0123 4567Check-in, yesterday 18:42");

    fireEvent.click(within(section).getByRole("button", { name: "Identify" }));

    expect(window.location.pathname).toBe("/products/p%201/name");
});

test("check-outs with no match: names, Unknown item, and Dismiss removes the row", async () => {
    const api = setup();
    const section = await screen.findByRole("region", { name: /Check-outs with no match/ });
    expect(section).toHaveTextContent("2 to review");
    const rows = within(section).getAllByRole("listitem");
    expect(rows.map((r) => r.textContent)).toEqual([
        "Heinz tomato ketchupScanned out 14 Oct 19:03Dismiss",
        "Unknown item 5010 0123 9981Scanned out 12 Oct 08:40Dismiss",
    ]);

    await act(async () => fireEvent.click(within(rows[0]).getByRole("button", { name: "Dismiss" })));

    expect(api.dismissUnmatched).toHaveBeenCalledWith("s1");
    expect(within(region(/Check-outs with no match/)).getAllByRole("listitem")).toHaveLength(1);
});

test("recently checked out: scanner or app, and Undo puts it back with a toast", async () => {
    const api = setup();
    const section = await screen.findByRole("region", { name: /Recently checked out/ });
    expect(section).toHaveTextContent("Scanned out the wrong thing? Undo puts it back in stock.");
    expect(
        within(section)
            .getAllByRole("listitem")
            .map((r) => r.textContent),
    ).toEqual(["Beef GravyScanned out today 08:30Undo", "MilkChecked out in the app yesterday 20:00Undo"]);

    await act(async () =>
        fireEvent.click(screen.getByRole("button", { name: "Undo check-out of Beef Gravy" })),
    );

    expect(api.undoCheckOut).toHaveBeenCalledWith("r1");
    expect(within(region(/Recently checked out/)).getAllByRole("listitem")).toHaveLength(1);
    expect(screen.getByRole("status", { name: "Notification" })).toHaveTextContent(
        "Beef Gravy is back in stock",
    );
});

test("the page has Scanners, This phone and AI models as sections, with the lists as sub-headings of Scanners", async () => {
    setup();
    await screen.findByText("Check-in scanner");

    const sections = screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(sections).toEqual(["ScannersLast checked 2 minutes ago", "This phoneNotifications", "AI models"]);
    const lists = screen
        .getAllByRole("heading", { level: 3 })
        .map((h) => h.textContent?.replace(/\d.*$/, "").trim());
    expect(lists).toEqual(["Check-ins with no match", "Check-outs with no match", "Recently checked out"]);
    // The lists are inside the Scanners section, and This phone comes after them.
    const scanners = screen.getByRole("region", { name: /^Scanners/ });
    expect(
        within(scanners).getByRole("heading", { level: 3, name: /Check-ins with no match/ }),
    ).toBeInTheDocument();
    expect(within(scanners).queryByText("This phone")).not.toBeInTheDocument();
    const order = [...document.querySelectorAll("h2, h3")].map((h) => h.textContent ?? "");
    expect(order.findIndex((t) => t.startsWith("Recently"))).toBeLessThan(
        order.findIndex((t) => t.startsWith("This phone")),
    );
});

test("This phone doesn't wait for the scanner data", () => {
    const api = {
        scanners: vi.fn(() => new Promise<Scanners>(() => undefined)), // never answers
        scanFeed: vi.fn(async () => ({ generatedAt: "", events: [] })),
    };
    render(<SettingsScreen api={api as unknown as ApiClient} feedConnection={noFeed} />);

    expect(screen.getByRole("heading", { level: 2, name: /^Scanners/ })).toHaveTextContent("Checking…");
    expect(screen.getByRole("heading", { level: 2, name: /^This phone/ })).toBeInTheDocument();
});

test("empty sections show one muted line", async () => {
    setup([base({ noMatchCheckIns: [], unmatchedCheckOuts: [], recentlyCheckedOut: [] })]);
    expect(await screen.findByText("Nothing to identify.")).toBeInTheDocument();
    expect(screen.getByText("Nothing to review.")).toBeInTheDocument();
    expect(screen.getByText("Nothing checked out in the last 48 hours.")).toBeInTheDocument();
    expect(region(/Check-ins with no match/)).toHaveTextContent("0 to identify");
});

test("returning to the app reloads", async () => {
    const api = setup();
    await screen.findByText("Last checked 2 minutes ago");
    Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });
    await act(async () => document.dispatchEvent(new Event("visibilitychange")));
    expect(api.scanners).toHaveBeenCalledTimes(2);
});

test("the feed's +n more waiting line links to Settings", () => {
    let feed: FeedState = new Map();
    for (let i = 0; i < 4; i++)
        feed = applyEvent(
            feed,
            {
                type: "scanNeedsIdentification",
                scanId: `s${i}`,
                deviceId: "d",
                role: "in",
                occurredAt: NOW.toISOString(),
                productId: `p${i}`,
                barcode: "1",
                displayName: "x",
                productStatus: "noMatch",
                unitId: "u",
            },
            NOW.getTime(),
        );
    window.history.pushState(null, "", "/");
    render(<ScanFeedView feed={feed} now={NOW.getTime()} onDismiss={() => {}} />);

    expect(screen.getByText(/\+1 more waiting\./)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("link", { name: "Open Settings to see them all." }));
    expect(window.location.pathname).toBe("/settings");
});

test("store labels (#58): Pick opens 'What is this?', and a store-label check-out says to check it out from the app", async () => {
    const data = base();
    setup([
        {
            ...data,
            noMatchCheckIns: [
                {
                    productId: null,
                    barcode: "2012345678903",
                    status: "storeLabel",
                    scannedAt: "2026-10-17T17:42:00.000Z",
                    unitId: null,
                    inStockCount: 1,
                    scanId: "scan 9",
                },
            ],
            unmatchedCheckOuts: [
                {
                    ...data.unmatchedCheckOuts[0],
                    barcode: "2012345678903",
                    displayName: null,
                    productId: null,
                    storeLabel: true,
                },
            ],
        },
    ]);
    const checkIns = await screen.findByRole("region", { name: /Check-ins with no match/ });
    expect(checkIns).toHaveTextContent("Store labelCheck-in, yesterday 18:42");
    expect(region(/Check-outs with no match/)).toHaveTextContent(
        "Store labelCheck it out from the app · scanned out 14 Oct 19:03",
    );

    fireEvent.click(within(checkIns).getByRole("button", { name: "Pick" }));

    expect(window.location.pathname).toBe("/store-labels/scan%209");
});
