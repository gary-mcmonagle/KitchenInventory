import { act, fireEvent, render, screen, within } from "@testing-library/react";
import type { ApiClient, Scanners } from "./api";
import { BottomNav } from "./BottomNav";
import { scannerLabel, scannerStatus } from "./scannerStatus";
import { Shell } from "./Shell";

// The three-tab navigation (#87): Stock · List · Hungry? (Recipes until #88), with Add and Scanners in the stock list header.

const clear: Scanners = {
    generatedAt: "2026-10-19T20:00:00.000Z",
    hub: {
        hubId: "cupboard-pi",
        lastSeenAt: "2026-10-19T19:58:00.000Z",
        heartbeatIntervalSeconds: 120,
        offlineThresholdMinutes: 20,
        online: true,
    },
    devices: [
        {
            deviceId: "scanner-in",
            role: "in",
            present: true,
            presentSince: null,
            absentSince: null,
            lastScanAt: null,
        },
        {
            deviceId: "scanner-out",
            role: "out",
            present: true,
            presentSince: null,
            absentSince: null,
            lastScanAt: null,
        },
    ],
    noMatchCheckIns: [],
    unmatchedCheckOuts: [],
    recentlyCheckedOut: [],
};

const toIdentify = {
    productId: "p",
    barcode: "5012345678900",
    status: "noMatch" as const,
    scannedAt: "",
    unitId: null,
    inStockCount: 1,
    scanId: null,
};

function shellApi(scanners: Scanners = clear) {
    return {
        me: async () => ({ name: "Cupboard test", objectId: "o" }),
        ping: async () => ({ status: "ok", commit: "abc1234" }),
        stock: async () => ({ generatedAt: "", totalUnits: 0, useFirstCount: 0, products: [] }),
        scanners: vi.fn(async () => scanners),
        looseProducts: async () => ({ products: [] }),
        product: async () => ({ productId: "p1", displayName: "Milk", status: "known", units: [] }),
    } as unknown as ApiClient & { scanners: ReturnType<typeof vi.fn> };
}

const tabs = () =>
    within(screen.getByRole("navigation", { name: "Main" }))
        .getAllByRole("link")
        .map((l) => [l.textContent, l.getAttribute("aria-current")]);

async function at(path: string, api = shellApi()) {
    window.history.pushState(null, "", path);
    render(<Shell api={api} buildSha="def5678" />);
    await act(async () => undefined);
    return api;
}

afterEach(() => window.history.pushState(null, "", "/"));

test("three tabs, Stock · List · Hungry?, with Stock open on the stock list", async () => {
    await at("/");
    expect(tabs()).toEqual([
        ["Stock", "page"],
        ["List", null],
        ["Hungry?", null],
    ]);
    expect(screen.queryByRole("link", { name: "Add an item" })).not.toBeInTheDocument(); // the old Add tab
});

test.each([
    ["/list", "List"],
    ["/recipes", "Hungry?"],
    ["/scanners", "Stock"], // reached from the stock list
])("on %s the %s tab is open", async (path, open) => {
    await at(path);
    const current = tabs().filter(([, c]) => c === "page");
    expect(current).toEqual([[open, "page"]]);
});

test("the tabs navigate", () => {
    render(<BottomNav current="stock" />);
    const nav = screen.getByRole("navigation", { name: "Main" });
    fireEvent.click(within(nav).getByRole("link", { name: "List" }));
    expect(window.location.pathname).toBe("/list");
    fireEvent.click(within(nav).getByRole("link", { name: "Hungry?" }));
    expect(window.location.pathname).toBe("/recipes");
    fireEvent.click(within(nav).getByRole("link", { name: "Stock" }));
    expect(window.location.pathname).toBe("/");
});

test("the List count badge shows only when there's something on the list", () => {
    const { rerender } = render(<BottomNav current="list" listCount={0} />);
    expect(screen.getByRole("link", { name: "List" })).toBeInTheDocument();
    rerender(<BottomNav current="list" listCount={5} />);
    expect(screen.getByRole("link", { name: "List, 5 items" })).toHaveTextContent("5List");
});

test("the List tab opens the shopping list", async () => {
    await at("/list");
    expect(screen.getByRole("heading", { level: 1, name: "Shopping list" })).toBeInTheDocument();
    expect(screen.getByText("Shared with everyone at home")).toBeInTheDocument();
});

test("the stock list header has the Scanners button and + Add, and both navigate", async () => {
    await at("/");
    fireEvent.click(screen.getByRole("link", { name: "Add" }));
    expect(window.location.pathname).toBe("/add");
    window.history.pushState(null, "", "/");
    await act(async () => window.dispatchEvent(new PopStateEvent("popstate")));
    fireEvent.click(screen.getByRole("link", { name: "Scanners and notifications" }));
    expect(window.location.pathname).toBe("/scanners");
});

test("Scanners has a back link to Stock", async () => {
    await at("/scanners");
    fireEvent.click(await screen.findByRole("link", { name: "Back to stock" }));
    expect(window.location.pathname).toBe("/");
});

test.each([
    ["clear", clear, "none", "Scanners and notifications"],
    [
        "something to identify",
        { ...clear, noMatchCheckIns: [toIdentify, toIdentify] },
        "attention",
        "Scanners and notifications: 2 need attention",
    ],
    [
        "the hub offline",
        { ...clear, hub: { ...clear.hub!, online: false } },
        "problem",
        "Scanners and notifications: 1 needs attention",
    ],
    [
        "a scanner unplugged",
        { ...clear, devices: [{ ...clear.devices[0], present: false }, clear.devices[1]] },
        "problem",
        "Scanners and notifications: 1 needs attention",
    ],
] as const)("the Scanners button's dot: %s", async (_, scanners, level, label) => {
    await at("/", shellApi(scanners as Scanners));
    const button = await screen.findByRole("link", { name: label });
    const dot = button.querySelector("[data-level]");
    expect(dot?.getAttribute("data-level") ?? "none").toBe(level);
});

test("a problem outranks things to sort out, and the count covers both", () => {
    const status = scannerStatus({
        ...clear,
        hub: { ...clear.hub!, online: false },
        unmatchedCheckOuts: [
            {
                scanId: "s",
                barcode: "1",
                productId: null,
                displayName: null,
                scannedAt: "",
                storeLabel: false,
            },
        ],
    });
    expect(status).toEqual({ level: "problem", count: 2 });
    expect(scannerLabel(status)).toBe("Scanners and notifications: 2 need attention");
    expect(scannerLabel(null)).toBe("Scanners and notifications");
});
