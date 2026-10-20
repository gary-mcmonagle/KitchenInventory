import { HubConnectionState } from "@microsoft/signalr";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { ApiError, SignInRequiredError, type ApiClient, type ShoppingListItem } from "./api";
import { ListScreen, offlineSuggestions } from "./ListScreen";
import { memoryPersistence, type ListSnapshot } from "./listStore";
import type { FeedConnection } from "./useScanFeed";
import { ShoppingListProvider } from "./useShoppingList";

// The shopping list works offline (#85, spike #83): the copy on the phone, the outbox, and the sync states.

const NOW = new Date("2026-10-20T09:00:00.000Z");

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

class FakeConnection implements FeedConnection {
    handlers = new Map<string, (payload: unknown) => void>();
    state: string = HubConnectionState.Disconnected;
    on(event: string, handler: (payload: unknown) => void) {
        this.handlers.set(event, handler);
    }
    onreconnected() {}
    async start() {
        if (!navigator.onLine) throw new TypeError("Failed to fetch");
        this.state = HubConnectionState.Connected;
    }
    async stop() {
        this.state = HubConnectionState.Disconnected;
    }
    emit(event: string, payload: unknown) {
        this.handlers.get(event)!(payload);
    }
}

let online = true;
beforeEach(() => {
    online = true;
    vi.spyOn(navigator, "onLine", "get").mockImplementation(() => online);
    localStorage.clear();
});
afterEach(() => vi.restoreAllMocks());

const offlineError = () => new TypeError("Failed to fetch");

function setup(server: ShoppingListItem[], saved: ListSnapshot | null = null) {
    const calls: string[] = [];
    const whenOnline = <T,>(work: () => T) =>
        vi.fn(async (...args: unknown[]) => {
            if (!online) throw offlineError();
            calls.push(JSON.stringify(args.slice(0, 2)));
            return work.call(null, ...(args as []));
        });
    const api = {
        shoppingList: whenOnline(() => ({ asOf: "2026-10-20T09:00:00.000Z", items: server })),
        addToList: vi.fn(async (id: string, add: { name: string; changeId: string }) => {
            if (!online) throw offlineError();
            calls.push(`add ${add.name}`);
            return { item: item(id, add.name, { addedByName: "Test User" }), merged: false };
        }),
        tickListItem: vi.fn(
            async (...[id, done]: [id: string, done: boolean, changeId?: string, at?: string]) => {
                if (!online) throw offlineError();
                calls.push(`${done ? "tick" : "untick"} ${id}`);
                return {
                    item: item(id, "x", {
                        status: done ? "done" : "open",
                        version: 5,
                        doneAt: NOW.toISOString(),
                    }),
                };
            },
        ),
        removeListItem: vi.fn(async (id: string) => {
            if (!online) throw offlineError();
            calls.push(`remove ${id}`);
            return { item: item(id, "x", { removed: true, version: 9 }) };
        }),
        clearTicked: vi.fn(async () => ({ cleared: [] })),
        listSuggestions: vi.fn(async () => ({ suggestions: [] })),
        signInAgain: vi.fn(),
    };
    const persistence = memoryPersistence(saved);
    const connection = new FakeConnection();
    render(
        <ShoppingListProvider
            api={api as unknown as ApiClient}
            connect={() => connection}
            persistence={persistence}
        >
            <ListScreen api={api as unknown as ApiClient} now={NOW} />
        </ShoppingListProvider>,
    );
    return { api, calls, persistence, connection };
}

const flush = () => act(async () => {});
const names = (list = "To buy") =>
    within(screen.getByRole("list", { name: list }))
        .getAllByRole("listitem")
        .map((r) => r.querySelector('[class*="rowName"]')?.textContent);
const goOnline = () =>
    act(async () => {
        online = true;
        window.dispatchEvent(new Event("online"));
    });
const addTyped = (name: string) => {
    const box = screen.getByRole("combobox", { name: "Add to the list" });
    fireEvent.change(box, { target: { value: name } });
    fireEvent.submit(box.closest("form")!);
};

test("opens offline from the copy on this phone, without the network", async () => {
    online = false;
    const { api } = setup([], {
        items: [item("1", "Bread"), item("2", "Milk")],
        asOf: "x",
        outbox: [],
        names: [],
    });
    await flush();

    expect(names()).toEqual(["Bread", "Milk"]);
    expect(screen.getByRole("status")).toHaveTextContent("Offline");
    expect(screen.queryByText("Couldn't load the list")).not.toBeInTheDocument();
    expect(api.shoppingList).not.toHaveBeenCalled();
});

test("offline changes apply at once, wait in the outbox (saved on the phone), then sync in order", async () => {
    online = false;
    const { calls, persistence } = setup([], {
        items: [item("1", "Bread"), item("2", "Milk")],
        asOf: "x",
        outbox: [],
        names: [],
    });
    await flush();

    await act(async () => fireEvent.click(screen.getByRole("checkbox", { name: "Tick off Bread" })));
    await act(async () => addTyped("Kitchen foil"));
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Remove Milk" })));

    expect(names()).toEqual(["Kitchen foil"]);
    expect(
        within(screen.getByRole("list", { name: "Recently ticked off" })).getByText("Bread"),
    ).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Offline · 3 changes waiting to sync");
    expect(screen.getByText("Not synced")).toBeInTheDocument();
    expect(screen.queryByRole("status", { name: "Finding its aisle" })).not.toBeInTheDocument();
    expect(persistence.current?.outbox.map((e) => e.op)).toEqual(["tick", "add", "remove"]);
    expect(calls).toEqual([]);

    await goOnline();

    expect(calls.filter((c) => !c.startsWith("["))).toEqual(["tick 1", "add Kitchen foil", "remove 2"]);
    expect(persistence.current?.outbox).toEqual([]);
    expect(screen.queryByText("Not synced")).not.toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Synced");
});

test("a replay after a failed send reuses the same change id, so the server can't apply it twice", async () => {
    const { api, persistence } = setup([item("1", "Bread")]);
    await flush();
    api.tickListItem.mockRejectedValueOnce(offlineError());

    await act(async () => fireEvent.click(screen.getByRole("checkbox", { name: "Tick off Bread" })));
    expect(persistence.current?.outbox).toHaveLength(1);
    expect(screen.getByRole("status")).toHaveTextContent("Offline · 1 change waiting to sync");

    await goOnline();

    const [first, second] = api.tickListItem.mock.calls;
    expect(second[2]).toBe(first[2]); // same changeId
    expect(second[3]).toBe(first[3]); // and the time it was really made, for ordering on the server
    expect(persistence.current?.outbox).toEqual([]);
});

test("changes saved on the phone are sent when the app opens again", async () => {
    const { api, persistence } = setup([item("1", "Bread")], {
        items: [item("1", "Bread", { status: "done" })],
        asOf: "x",
        outbox: [{ op: "tick", changeId: "c1", itemId: "1", at: "2026-10-20T08:00:00.000Z" }],
        names: [],
    });
    await flush();

    expect(api.tickListItem).toHaveBeenCalledWith("1", true, "c1", "2026-10-20T08:00:00.000Z");
    expect(persistence.current?.outbox).toEqual([]);
});

test("while a change waits, the server's older view of that item doesn't overwrite it", async () => {
    online = false;
    const { connection } = setup([], { items: [item("1", "Bread")], asOf: "x", outbox: [], names: [] });
    await flush();
    await act(async () => fireEvent.click(screen.getByRole("checkbox", { name: "Tick off Bread" })));

    act(() => connection.emit("shoppingListItemChanged", item("1", "Bread", { version: 2 })));

    expect(screen.getByRole("checkbox", { name: "Put Bread back on the list" })).toBeInTheDocument();
});

test("sign-in expired: the outbox is kept and the screen asks to sign in, never while offline", async () => {
    const { api, persistence } = setup([item("1", "Bread")]);
    await flush();
    api.tickListItem.mockRejectedValue(new SignInRequiredError("expired"));

    await act(async () => fireEvent.click(screen.getByRole("checkbox", { name: "Tick off Bread" })));

    expect(screen.getByRole("status")).toHaveTextContent("Sign in to sync your changes");
    expect(persistence.current?.outbox).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(api.signInAgain).toHaveBeenCalled();
});

test("a change the server refuses (4xx) is dropped, and the rest still go", async () => {
    online = false;
    const { api, persistence } = setup([], {
        items: [item("1", "Bread"), item("2", "Milk")],
        asOf: "x",
        outbox: [],
        names: [],
    });
    await flush();
    await act(async () => fireEvent.click(screen.getByRole("checkbox", { name: "Tick off Bread" })));
    await act(async () => fireEvent.click(screen.getByRole("checkbox", { name: "Tick off Milk" })));
    api.tickListItem.mockRejectedValueOnce(new ApiError(404, "gone"));
    vi.spyOn(console, "warn").mockImplementation(() => {});

    await goOnline();

    expect(api.tickListItem.mock.calls.map((c) => c[0])).toEqual(["1", "2"]);
    expect(persistence.current?.outbox).toEqual([]);
});

test("suggestions offline come from names seen on the list, with no stock counts", async () => {
    online = false;
    const { api } = setup([], {
        items: [item("1", "Bread")],
        asOf: "x",
        outbox: [],
        names: ["Bread", "Bananas", "Bran flakes"],
    });
    await flush();

    fireEvent.change(screen.getByRole("combobox", { name: "Add to the list" }), { target: { value: "ba" } });
    await flush();

    const options = screen.getAllByRole("option").map((o) => o.textContent);
    expect(options).toEqual(["Bananas"]);
    expect(api.listSuggestions).not.toHaveBeenCalled();
    expect(offlineSuggestions(["Bread", "Bran flakes"], "br").map((s) => s.name)).toEqual([
        "Bread",
        "Bran flakes",
    ]);
});
