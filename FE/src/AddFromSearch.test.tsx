import { HubConnectionState } from "@microsoft/signalr";
import { act, fireEvent, render, screen } from "@testing-library/react";
import type { ApiClient, ShoppingListItem, Stock, StockProduct } from "./api";
import { BottomNav } from "./BottomNav";
import { forgetStockSearch, listNameFor, offersListAdd, StockScreen } from "./StockScreen";
import type { FeedConnection } from "./useScanFeed";
import { ShoppingListProvider } from "./useShoppingList";

// Add to the shopping list from an empty search (#97).

const product = (name: string): StockProduct => ({
    productId: crypto.randomUUID(),
    barcode: null,
    displayName: name,
    brand: null,
    size: null,
    count: 1,
    latestCheckedInAt: "2026-10-20T08:00:00.000Z",
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
    genericName: null,
});

const STOCK: Stock = {
    generatedAt: "2026-10-20T08:00:00.000Z",
    totalUnits: 2,
    useFirstCount: 0,
    products: [product("Leeks"), product("Baked beans")],
};

const item = (id: string, name: string, overrides: Partial<ShoppingListItem> = {}): ShoppingListItem => ({
    id,
    name,
    note: null,
    productId: null,
    aisleId: null,
    aisleName: null,
    findingAisle: true,
    source: "stockSearch",
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
        this.state = HubConnectionState.Connected;
    }
    async stop() {
        this.state = HubConnectionState.Disconnected;
    }
    emit(event: string, payload: unknown) {
        this.handlers.get(event)?.(payload);
    }
}

function setup(onList: ShoppingListItem[] = []) {
    const api = {
        stock: vi.fn(async () => STOCK),
        shoppingList: vi.fn(async () => ({ asOf: "2026-10-20T09:00:00.000Z", items: onList })),
        addToList: vi.fn(async (id: string, add: { name: string; source: string }) => ({
            item: item(id, add.name, { source: add.source }),
            merged: false,
        })),
    };
    const connection = new FakeConnection();
    render(
        <ShoppingListProvider api={api as unknown as ApiClient} connect={() => connection}>
            <StockScreen api={api as unknown as ApiClient} />
            <BottomNav current="stock" />
        </ShoppingListProvider>,
    );
    return { api, connection };
}

const search = (text: string) =>
    fireEvent.change(screen.getByRole("searchbox", { name: "Search stock" }), { target: { value: text } });

beforeEach(() => forgetStockSearch());

test("the name is trimmed, single-spaced and capitalised; only 2+ characters that aren't all digits are offered", () => {
    expect(listNameFor("  kitchen   foil ")).toBe("Kitchen foil");
    expect(listNameFor("Bin bags")).toBe("Bin bags");
    expect(offersListAdd("kitchen foil")).toBe(true);
    expect(offersListAdd(" f ")).toBe(false);
    expect(offersListAdd("5012345678900")).toBe(false);
    expect(offersListAdd("7up")).toBe(true);
});

test("an empty search offers the list; tapping adds with source stockSearch and new ids, then Added and View list", async () => {
    const { api, connection } = setup();
    await screen.findByText("Leeks");

    search("  kitchen foil ");
    const button = screen.getByRole("button", { name: 'Add "Kitchen foil" to the shopping list' });
    await act(async () => fireEvent.click(button));

    expect(api.addToList).toHaveBeenCalledTimes(1);
    const [id, add] = api.addToList.mock.calls[0] as unknown as [
        string,
        { name: string; productId: string | null; source: string; changeId: string },
    ];
    expect(id).toMatch(/^[0-9a-f-]{36}$/);
    expect(add).toMatchObject({ name: "Kitchen foil", productId: null, source: "stockSearch" });
    expect(screen.getByRole("button", { name: "Added to the list ✓" })).toBeDisabled();
    expect(screen.getByRole("link", { name: "View list" })).toHaveAttribute("href", "/list");
    expect(screen.getByRole("searchbox", { name: "Search stock" })).toHaveValue("  kitchen foil ");

    // Someone else's add arrives live: the List tab's badge counts it.
    act(() => connection.emit("shoppingListItemChanged", item(crypto.randomUUID(), "Milk", { version: 1 })));
    expect(screen.getByRole("link", { name: /List/ })).toHaveTextContent("2");
});

test("something already open on the list says so, with no button to press", async () => {
    setup([item("a", "Kitchen foil", { aisleId: "household", findingAisle: false })]);
    await screen.findByText("Leeks");

    search("kitchen FOIL");

    expect(screen.getByRole("button", { name: '✓ "Kitchen foil" is on the list' })).toBeDisabled();
});

test("no button for a barcode, a single letter, or when something matches", async () => {
    setup();
    await screen.findByText("Leeks");

    search("5012345678900");
    expect(screen.getByText(/Nothing matches that\./)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /shopping list/ })).not.toBeInTheDocument();

    search("q");
    expect(screen.queryByRole("button", { name: /shopping list/ })).not.toBeInTheDocument();

    search("leek");
    expect(screen.queryByRole("button", { name: /shopping list/ })).not.toBeInTheDocument();
});
