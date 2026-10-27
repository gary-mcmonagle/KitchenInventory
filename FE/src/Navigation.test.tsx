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
        savedRecipes: async () => ({ savedRecipes: [] }),
        shoppingListViewed: async () => undefined,
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
    ["/settings", "Stock"], // reached from the stock list
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

test("the stock list header has the Settings button and + Add, and both navigate", async () => {
    await at("/");
    fireEvent.click(screen.getByRole("link", { name: "Add" }));
    expect(window.location.pathname).toBe("/add");
    window.history.pushState(null, "", "/");
    await act(async () => window.dispatchEvent(new PopStateEvent("popstate")));
    fireEvent.click(screen.getByRole("link", { name: "Settings" }));
    expect(window.location.pathname).toBe("/settings");
});

test("Settings has a back link to Stock", async () => {
    await at("/settings");
    fireEvent.click(await screen.findByRole("link", { name: "Back to stock" }));
    expect(window.location.pathname).toBe("/");
});

test("the old /scanners address goes to /settings, keeping the back link and the Stock tab", async () => {
    window.history.pushState({ from: "/" }, "", "/scanners"); // as an old in-app link or the stock list would leave it
    render(<Shell api={shellApi()} buildSha="def5678" />);
    await act(async () => undefined);

    expect(window.location.pathname).toBe("/settings");
    expect(window.history.state).toEqual({ from: "/" });
    expect(await screen.findByRole("heading", { level: 1, name: "Settings" })).toBeInTheDocument();
    expect(tabs().filter(([, c]) => c === "page")).toEqual([["Stock", "page"]]);
    fireEvent.click(screen.getByRole("link", { name: "Back to stock" }));
    expect(window.location.pathname).toBe("/");
});

test.each([
    ["clear", clear, "none", "Settings"],
    [
        "something to identify",
        { ...clear, noMatchCheckIns: [toIdentify, toIdentify] },
        "attention",
        "Settings: 2 need attention",
    ],
    [
        "the hub offline",
        { ...clear, hub: { ...clear.hub!, online: false } },
        "problem",
        "Settings: the Raspberry Pi is offline",
    ],
    [
        "a scanner unplugged",
        { ...clear, devices: [{ ...clear.devices[0], present: false }, clear.devices[1]] },
        "problem",
        "Settings: a scanner is unplugged",
    ],
] as const)("the Settings button's dot: %s", async (_, scanners, level, label) => {
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
    expect(status).toEqual({ level: "problem", count: 2, hubOffline: true, unplugged: 0 });
    expect(scannerLabel(status)).toBe("Settings: the Raspberry Pi is offline"); // the problem is named, not the count
    expect(scannerLabel(null)).toBe("Settings");
});

test("the Settings button's label names the problem: how many scanners, or the Pi", () => {
    const status = (hubOffline: boolean, unplugged: number, toSort = 0) =>
        ({
            level: "problem",
            count: (hubOffline ? 1 : 0) + unplugged + toSort,
            hubOffline,
            unplugged,
        }) as const;

    expect(scannerLabel(status(false, 1))).toBe("Settings: a scanner is unplugged");
    expect(scannerLabel(status(false, 2))).toBe("Settings: 2 scanners are unplugged");
    expect(scannerLabel(status(true, 0))).toBe("Settings: the Raspberry Pi is offline");
    expect(scannerLabel(status(true, 1))).toBe("Settings: 2 need attention"); // more than one kind of problem
    expect(scannerLabel({ level: "attention", count: 1, hubOffline: false, unplugged: 0 })).toBe(
        "Settings: 1 needs attention",
    );
});

// Tapping the tab you're on starts that page afresh, as arriving from another tab does (Gary, 2026-10-22).
describe("tapping the tab you're on", () => {
    const tab = (name: string) =>
        within(screen.getByRole("navigation", { name: "Main" })).getByRole("link", {
            name: new RegExp(`^${name}`),
        });

    test("Hungry? goes back to an empty form from wherever you were in it", async () => {
        await at("/recipes");
        fireEvent.change(screen.getByRole("textbox", { name: /Additional comments/i }), {
            target: { value: "spicy" },
        });
        expect(screen.getByRole("textbox", { name: /Additional comments/i })).toHaveValue("spicy");

        await act(async () => fireEvent.click(tab("Hungry\\?")));

        expect(window.location.pathname).toBe("/recipes");
        expect(screen.getByRole("textbox", { name: /Additional comments/i })).toHaveValue("");
    });

    test("Stock clears the search and goes back to the top", async () => {
        const scrollTo = vi.spyOn(window, "scrollTo").mockImplementation(() => {});
        const api = shellApi();
        const milk = {
            productId: "milk",
            barcode: null,
            displayName: "Milk",
            brand: null,
            size: null,
            count: 1,
            latestCheckedInAt: "2026-10-21T08:00:00.000Z",
            status: "known",
            section: "later",
            soonestEstimatedExpiryDate: null,
            daysUntilSoonest: null,
            shelfLifeDays: null,
            openedCount: 0,
            frozenCount: 0,
            soonestState: null,
            soonestStateOn: null,
            aisleId: "other",
            icon: null,
            useFirst: false,
        };
        (api as unknown as { stock: () => Promise<unknown> }).stock = async () => ({
            generatedAt: "",
            totalUnits: 1,
            useFirstCount: 0,
            products: [milk],
        });
        await at("/", api);
        fireEvent.change(await screen.findByRole("searchbox", { name: "Search stock" }), {
            target: { value: "milk" },
        });

        await act(async () => fireEvent.click(tab("Stock")));

        expect(screen.getByRole("searchbox", { name: "Search stock" })).toHaveValue("");
        expect(scrollTo).toHaveBeenLastCalledWith(0, 0);
        scrollTo.mockRestore();
    });

    test("another tab still navigates as before", async () => {
        await at("/recipes");
        await act(async () => fireEvent.click(tab("List")));
        expect(window.location.pathname).toBe("/list");
    });
});
