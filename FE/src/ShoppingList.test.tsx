import { HubConnectionState } from "@microsoft/signalr";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import type { ApiClient, ShoppingListItem } from "./api";
import { BottomNav } from "./BottomNav";
import { BY_AISLE_KEY, ListScreen, SUGGEST_DELAY_MS } from "./ListScreen";
import { applyItem, applyRemoved, EMPTY_LIST, groupByAisle, listWhen, replaceAll } from "./shoppingList";
import type { FeedConnection } from "./useScanFeed";
import { ShoppingListProvider } from "./useShoppingList";

// The shared shopping list (#84).

const NOW = new Date("2026-10-20T09:00:00.000Z"); // 10:00 BST

const item = (id: string, name: string, overrides: Partial<ShoppingListItem> = {}): ShoppingListItem => ({
    id,
    name,
    note: null,
    productId: null,
    aisleId: "other",
    aisleName: "Other",
    findingAisle: false,
    source: "typed",
    status: "open",
    addedByName: "Gary",
    addedAt: "2026-10-20T07:12:00.000Z",
    doneByName: null,
    doneAt: null,
    updatedAt: "2026-10-20T07:12:00.000Z",
    version: 1,
    removed: false,
    ...overrides,
});

// ---- The state rules

describe("list state", () => {
    test("the higher version wins, so a late or repeated event never undoes a newer change", () => {
        let s = applyItem(EMPTY_LIST, item("a", "Bread", { version: 3, status: "done" }));
        s = applyItem(s, item("a", "Bread", { version: 2 })); // stale
        expect(s.items.get("a")?.status).toBe("done");
        s = applyItem(s, item("a", "Bread", { version: 4 }));
        expect(s.items.get("a")?.status).toBe("open");
    });

    test("a removed item can't be brought back by an older change", () => {
        let s = applyItem(EMPTY_LIST, item("a", "Bread", { version: 1 }));
        s = applyRemoved(s, "a", 2);
        s = applyItem(s, item("a", "Bread", { version: 1 }));
        expect(s.items.has("a")).toBe(false);
        s = applyItem(s, item("b", "Milk", { version: 2, removed: true }));
        expect(s.items.has("b")).toBe(false);
    });

    test("a full load keeps an add that's still on its way", () => {
        const pending = applyItem(EMPTY_LIST, item("new", "Foil", { version: 0 }));
        const s = replaceAll(pending, [item("a", "Bread")]);
        expect([...s.items.keys()].sort()).toEqual(["a", "new"]);
    });

    test("by aisle: Just added first, then aisles in shop order", () => {
        const groups = groupByAisle([
            item("1", "Kitchen roll", { aisleId: "other" }),
            item("2", "Milk", { aisleId: "dairyEggs" }),
            item("3", "Foil", { aisleId: null, findingAisle: true }),
            item("4", "Bananas", { aisleId: "fruitVeg" }),
        ]);
        expect(groups.map((g) => [g.title, g.items.map((i) => i.name)])).toEqual([
            ["Just added", ["Foil"]],
            ["Fruit & veg", ["Bananas"]],
            ["Dairy & eggs", ["Milk"]],
            ["Other", ["Kitchen roll"]],
        ]);
    });

    test.each([
        ["2026-10-20T08:59:40.000Z", "just now"],
        ["2026-10-20T07:12:00.000Z", "08:12"],
        ["2026-10-19T12:00:00.000Z", "yesterday"],
        ["2026-10-17T12:00:00.000Z", "Sat"],
        ["2026-10-01T12:00:00.000Z", "1 Oct"],
    ])("when %s → %s", (iso, text) => expect(listWhen(iso, NOW)).toBe(text));
});

// ---- The screen

class FakeConnection implements FeedConnection {
    handlers = new Map<string, (payload: unknown) => void>();
    reconnected: (() => void) | null = null;
    state: string = HubConnectionState.Disconnected;
    on(event: string, handler: (payload: unknown) => void) {
        this.handlers.set(event, handler);
    }
    onreconnected(handler: () => void) {
        this.reconnected = handler;
    }
    async start() {
        this.state = HubConnectionState.Connected;
    }
    async stop() {
        this.state = HubConnectionState.Disconnected;
    }
    emit(event: string, payload: unknown) {
        this.handlers.get(event)!(payload);
    }
}

function setup(
    items: ShoppingListItem[] = [],
    suggestions: Array<{ name: string; inStock: number; productId: string | null }> = [],
) {
    const answers = [{ asOf: "2026-10-20T09:00:00.000Z", items }];
    const names = new Map(items.map((i) => [i.id, i.name]));
    const versions = new Map<string, number>();
    const bump = (id: string) => versions.set(id, (versions.get(id) ?? 1) + 1);
    const api = {
        shoppingList: vi.fn(async () => answers.shift() ?? { asOf: "2026-10-20T09:05:00.000Z", items: [] }),
        addToList: vi.fn(
            async (id: string, add: { name: string; productId: string | null; source: string }) => ({
                item: item(id, add.name, {
                    productId: add.productId,
                    source: add.source,
                    aisleId: null,
                    findingAisle: !add.productId,
                }),
                merged: false,
            }),
        ),
        tickListItem: vi.fn(
            async (id: string, done: boolean) => (
                bump(id),
                {
                    item: item(id, names.get(id) ?? "x", {
                        status: done ? "done" : "open",
                        version: (versions.get(id) ?? 1) + 1,
                        doneByName: done ? "Gary" : null,
                        doneAt: done ? NOW.toISOString() : null,
                    }),
                }
            ),
        ),
        removeListItem: vi.fn(async (id: string) => ({
            item: item(id, names.get(id) ?? "x", { removed: true, version: 999 }),
        })),
        clearTicked: vi.fn(async () => ({ cleared: [] })),
        listSuggestions: vi.fn(async () => ({ suggestions })),
    };
    const connection = new FakeConnection();
    render(
        <ShoppingListProvider api={api as unknown as ApiClient} connect={() => connection}>
            <ListScreen api={api as unknown as ApiClient} now={NOW} />
            <BottomNav current="list" />
        </ShoppingListProvider>,
    );
    return { api, connection, answers };
}

const flush = () => act(async () => {});
const rows = (name = "To buy") => within(screen.getByRole("list", { name })).getAllByRole("listitem");

beforeEach(() => localStorage.clear());

test("shows the list as drawn: count, rows with notes and who added them, and the tab badge", async () => {
    setup([
        item("1", "Semi-skimmed milk", { note: "2" }),
        item("2", "Bananas", { addedByName: "Test User", addedAt: "2026-10-19T12:00:00.000Z" }),
        item("3", "Eggs", { status: "done", doneByName: "Gary", doneAt: "2026-10-20T08:40:00.000Z" }),
    ]);
    await flush();

    expect(screen.getByText("2 things to buy")).toBeInTheDocument();
    expect(rows().map((r) => r.textContent)).toEqual([
        "BananasAdded by Test User · yesterday", // oldest first
        "Semi-skimmed milk· 2Added by Gary · 08:12",
    ]);
    expect(rows("Recently ticked off")[0]).toHaveTextContent("EggsGary · 09:40");
    expect(screen.getByRole("link", { name: /List/ })).toHaveTextContent("2");
});

test("adding is instant: the row appears with a spinner before the API answers, and the box clears", async () => {
    const { api } = setup();
    await flush();
    let answer!: () => void;
    api.addToList.mockImplementationOnce(
        (id, add) =>
            new Promise((resolve) => {
                answer = () =>
                    resolve({
                        item: item(id, add.name, { aisleId: null, findingAisle: true }),
                        merged: false,
                    });
            }),
    );

    const box = screen.getByRole("combobox", { name: "Add to the list" });
    fireEvent.change(box, { target: { value: "Kitchen foil" } });
    fireEvent.submit(box.closest("form")!);

    expect(rows()[0]).toHaveTextContent("Kitchen foilFinding its aisle…");
    expect(screen.getByRole("status", { name: "Finding its aisle" })).toBeInTheDocument();
    expect(box).toHaveValue("");
    const [id, body] = api.addToList.mock.calls[0];
    expect(id).toMatch(/^[0-9a-f-]{36}$/);
    expect(body).toEqual(expect.objectContaining({ name: "Kitchen foil", source: "typed", productId: null }));
    expect((body as unknown as { changeId: string }).changeId).toMatch(/^[0-9a-f-]{36}$/);
    await act(async () => answer());
});

test("the aisle arriving by a live event takes the spinner away", async () => {
    const { connection } = setup([item("f", "Kitchen foil", { aisleId: null, findingAisle: true })]);
    await flush();

    act(() =>
        connection.emit(
            "shoppingListItemChanged",
            item("f", "Kitchen foil", { version: 2, aisleId: "other" }),
        ),
    );

    expect(screen.queryByRole("status", { name: "Finding its aisle" })).not.toBeInTheDocument();
    expect(rows()[0]).toHaveTextContent("Added by Gary");
});

test("suggestions show stock across brands, None in stock in amber, and a pick adds with its product", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
        const { api } = setup(
            [],
            [
                { name: "Semi-skimmed milk", inStock: 3, productId: "p-milk" },
                { name: "Milk chocolate", inStock: 0, productId: null },
            ],
        );
        await flush();
        fireEvent.change(screen.getByRole("combobox", { name: "Add to the list" }), {
            target: { value: "milk" },
        });
        await act(async () => vi.advanceTimersByTime(SUGGEST_DELAY_MS + 1));

        const options = within(screen.getByRole("listbox", { name: "Suggestions" })).getAllByRole("option");
        expect(options.map((o) => o.textContent)).toEqual([
            "Semi-skimmed milk3 in stock",
            "Milk chocolateNone in stock",
        ]);
        expect(within(options[1]).getByText("None in stock").className).toMatch(/noneInStock/);

        await act(async () => fireEvent.click(within(options[0]).getByRole("button")));
        expect(api.addToList.mock.calls[0][1]).toEqual(
            expect.objectContaining({ name: "Semi-skimmed milk", productId: "p-milk", source: "suggestion" }),
        );
    } finally {
        vi.useRealTimers();
    }
});

test("suggestions wait for 2 characters and a pause in typing, then ask once (#109)", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
        const { api } = setup([], [{ name: "Semi-skimmed milk", inStock: 3, productId: "p-milk" }]);
        await flush();
        const box = screen.getByRole("combobox", { name: "Add to the list" });

        fireEvent.change(box, { target: { value: "m" } });
        await act(async () => vi.advanceTimersByTime(1000));
        expect(api.listSuggestions).not.toHaveBeenCalled(); // 1 character: no request

        for (const value of ["mi", "mil", "milk"]) {
            fireEvent.change(box, { target: { value } });
            await act(async () => vi.advanceTimersByTime(SUGGEST_DELAY_MS - 50)); // still typing
        }
        expect(api.listSuggestions).not.toHaveBeenCalled();

        await act(async () => vi.advanceTimersByTime(100));
        expect(api.listSuggestions).toHaveBeenCalledTimes(1);
        expect(api.listSuggestions).toHaveBeenCalledWith("milk");
    } finally {
        vi.useRealTimers();
    }
});

test("a name already open isn't added again", async () => {
    const { api } = setup([item("1", "Bread")]);
    await flush();
    const box = screen.getByRole("combobox", { name: "Add to the list" });
    fireEvent.change(box, { target: { value: " bread " } });
    fireEvent.submit(box.closest("form")!);
    expect(api.addToList).not.toHaveBeenCalled();
    expect(rows()).toHaveLength(1);
});

test("tick off, put back, remove and Clear ticked", async () => {
    const { api } = setup([item("1", "Bread"), item("2", "Milk")]);
    await flush();

    await act(async () => fireEvent.click(screen.getByRole("checkbox", { name: "Tick off Bread" })));
    expect(api.tickListItem).toHaveBeenCalledWith("1", true, expect.any(String), expect.any(String));
    expect(rows("Recently ticked off")[0]).toHaveTextContent("Bread");

    await act(async () =>
        fireEvent.click(screen.getByRole("checkbox", { name: "Put Bread back on the list" })),
    );
    expect(api.tickListItem).toHaveBeenLastCalledWith("1", false, expect.any(String), expect.any(String));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Remove Milk" })));
    expect(api.removeListItem).toHaveBeenCalledWith("2", expect.any(String));
    expect(screen.queryByText("Milk")).not.toBeInTheDocument();

    await act(async () => fireEvent.click(screen.getByRole("checkbox", { name: "Tick off Bread" })));
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Clear ticked" })));
    expect(api.clearTicked).toHaveBeenCalled();
    expect(screen.queryByText("Recently ticked off")).not.toBeInTheDocument();
});

test("events are applied by id and version: a stale one is ignored, removals and clears take rows away", async () => {
    const { connection } = setup([
        item("1", "Bread", { version: 3 }),
        item("2", "Milk"),
        item("3", "Eggs", { status: "done", doneAt: NOW.toISOString() }),
    ]);
    await flush();

    act(() => connection.emit("shoppingListItemChanged", item("1", "Bread", { version: 2, status: "done" })));
    expect(screen.getByRole("checkbox", { name: "Tick off Bread" })).toBeInTheDocument();

    act(() => connection.emit("shoppingListItemChanged", item("4", "Rice", { version: 1 })));
    act(() => connection.emit("shoppingListItemRemoved", { id: "2", version: 2 }));
    act(() => connection.emit("shoppingListCleared", { ids: ["3"] }));

    expect(rows().map((r) => r.querySelector('[class*="rowName"]')?.textContent)).toEqual(["Bread", "Rice"]);
    expect(screen.queryByText("Recently ticked off")).not.toBeInTheDocument();
});

test("after a reconnect and on coming back to the foreground it catches up with since", async () => {
    const { api, connection, answers } = setup([item("1", "Bread")]);
    await flush();
    answers.push({ asOf: "2026-10-20T09:10:00.000Z", items: [item("2", "Milk")] });

    await act(async () => connection.reconnected!());
    expect(api.shoppingList).toHaveBeenLastCalledWith("2026-10-20T09:00:00.000Z");
    expect(rows()).toHaveLength(2);

    answers.push({
        asOf: "2026-10-20T09:20:00.000Z",
        items: [item("1", "Bread", { version: 2, removed: true })],
    });
    await act(async () => document.dispatchEvent(new Event("visibilitychange")));
    expect(api.shoppingList).toHaveBeenLastCalledWith("2026-10-20T09:10:00.000Z");
    expect(rows().map((r) => r.querySelector('[class*="rowName"]')?.textContent)).toEqual(["Milk"]);
});

test("Sort by aisle groups in shop order with Just added first, and is remembered", async () => {
    setup([
        item("1", "Kitchen roll"),
        item("2", "Milk", { aisleId: "dairyEggs" }),
        item("3", "Foil", { aisleId: null, findingAisle: true }),
    ]);
    await flush();
    const toggle = screen.getByRole("button", { name: "Sort by aisle" });
    expect(toggle).toHaveAttribute("aria-pressed", "false");

    fireEvent.click(toggle);

    expect(toggle).toHaveAttribute("aria-pressed", "true");
    expect(screen.getAllByRole("list").map((l) => l.getAttribute("aria-label"))).toEqual([
        "Just added",
        "Dairy & eggs",
        "Other",
    ]);
    expect(localStorage.getItem(BY_AISLE_KEY)).toBe("true");
});

test("Sort by aisle still works when storage throws", async () => {
    const get = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
        throw new Error("blocked");
    });
    const set = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
        throw new Error("blocked");
    });
    try {
        setup([item("1", "Milk", { aisleId: "dairyEggs" })]);
        await flush();
        fireEvent.click(screen.getByRole("button", { name: "Sort by aisle" }));
        expect(screen.getByRole("list", { name: "Dairy & eggs" })).toBeInTheDocument();
    } finally {
        get.mockRestore();
        set.mockRestore();
    }
});

test("an empty list says so", async () => {
    setup();
    await flush();
    expect(screen.getByText("Nothing on the list. Nice.")).toBeInTheDocument();
    expect(screen.getByText("0 things to buy")).toBeInTheDocument();
});
