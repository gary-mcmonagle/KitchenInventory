import { act, fireEvent, render, screen, within } from "@testing-library/react";
import type { ApiClient, ClearOutResult, Stock, StockProduct } from "./api";
import { ClearOut, CLEAR_OUT_TOAST_MS } from "./ClearOut";
import { StockScreen } from "./StockScreen";

const product = (overrides: Partial<StockProduct>): StockProduct => ({
    productId: crypto.randomUUID(),
    barcode: "5000354922831",
    displayName: "Beef Gravy",
    brand: "Bisto",
    size: "230g",
    count: 1,
    latestCheckedInAt: "2026-11-20T18:00:00.000Z",
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
    ...overrides,
});

const stockOf = (...products: StockProduct[]): Stock => ({
    generatedAt: "2026-11-22T18:00:00.000Z",
    totalUnits: products.reduce((n, p) => n + p.count, 0),
    useFirstCount: 0,
    products,
});

// Chilled: milk expired yesterday (1 unit), yoghurt in 1 day (3 units), cheddar later. Tins: tomatoes (4 units).
const milk = product({
    productId: "milk",
    displayName: "Semi-skimmed milk 2L",
    brand: null,
    size: null,
    aisleId: "dairyEggs",
    daysUntilSoonest: -1,
    soonestEstimatedExpiryDate: "2026-11-21",
});
const yoghurt = product({
    productId: "yog",
    displayName: "Greek yoghurt",
    brand: null,
    size: null,
    count: 3,
    aisleId: "dairyEggs",
    daysUntilSoonest: 1,
    soonestEstimatedExpiryDate: "2026-11-23",
});
const cheddar = product({
    productId: "ched",
    displayName: "Mature cheddar",
    brand: null,
    size: null,
    aisleId: "dairyEggs",
    daysUntilSoonest: 9,
    soonestEstimatedExpiryDate: "2026-12-01",
});
const tomatoes = product({
    productId: "tom",
    displayName: "Chopped tomatoes",
    brand: null,
    size: null,
    count: 4,
    aisleId: "tinsJars",
    daysUntilSoonest: 240,
    soonestEstimatedExpiryDate: "2027-07-20",
});
const STOCK = stockOf(milk, yoghurt, cheddar, tomatoes);

/** Each product's units, in check-out order (soonest first): ids like "yog-1", "yog-2". */
function fakeApi() {
    const product = vi.fn(async (id: string) => {
        const p = STOCK.products.find((x) => x.productId === id)!;
        return { units: Array.from({ length: p.count }, (_, i) => ({ unitId: `${id}-${i + 1}` })) };
    });
    const checkOutUnits = vi.fn(async (ids: string[]): Promise<ClearOutResult> => ({
        checkedOut: ids.length,
        unchanged: 0,
        failed: 0,
        units: ids.map((unitId) => ({ unitId, outcome: "checkedOut", productId: unitId.split("-")[0] })),
    }));
    const undoCheckOut = vi.fn(async (unitId: string) => ({
        unitId,
        productId: "x",
        count: 1,
        restored: true,
    }));
    return {
        api: { product, checkOutUnits, undoCheckOut } as unknown as ApiClient,
        product,
        checkOutUnits,
        undoCheckOut,
    };
}

function setup(stock: Stock = STOCK) {
    const fake = fakeApi();
    const onClose = vi.fn();
    const onChanged = vi.fn();
    const view = render(<ClearOut api={fake.api} stock={stock} onClose={onClose} onChanged={onChanged} />);
    return { ...fake, onClose, onChanged, ...view };
}

const pick = (name: string) => screen.getByRole("button", { name: new RegExp(`^${name}`) });
const rowOf = (name: string) => pick(name).closest("li")!;
const go = () => screen.getByRole("button", { name: /^(Check out \d+ items?|Select items to check out)$/ });
const chip = (name: RegExp) => screen.getByRole("button", { name });

afterEach(() => vi.useRealTimers());

test("tapping a row selects all its units, and tapping again puts it back", () => {
    setup();
    expect(go()).toBeDisabled();
    expect(go()).toHaveTextContent("Select items to check out");

    fireEvent.click(pick("Greek yoghurt"));
    expect(within(rowOf("Greek yoghurt")).getByText("3 of 3")).toBeInTheDocument();
    expect(go()).toHaveTextContent("Check out 3 items");

    fireEvent.click(pick("Greek yoghurt"));
    expect(go()).toBeDisabled();
});

test("the stepper picks how many, between 1 and all", () => {
    setup();
    fireEvent.click(pick("Greek yoghurt"));
    const fewer = screen.getByRole("button", { name: "One fewer Greek yoghurt" });
    const more = screen.getByRole("button", { name: "One more Greek yoghurt" });

    expect(more).toBeDisabled();
    fireEvent.click(fewer);
    fireEvent.click(fewer);
    expect(screen.getByText("1 of 3")).toBeInTheDocument();
    expect(fewer).toBeDisabled();
    expect(go()).toHaveTextContent("Check out 1 item");
    fireEvent.click(more);
    expect(screen.getByText("2 of 3")).toBeInTheDocument();
});

test("the badge shows only when expired, today, tomorrow or within 2 days", () => {
    setup();
    expect(within(rowOf("Semi-skimmed milk")).getByText("Expired yesterday")).toBeInTheDocument();
    expect(within(rowOf("Greek yoghurt")).getByText("Tomorrow")).toBeInTheDocument();
    expect(rowOf("Mature cheddar").textContent).not.toMatch(/Expired|Today|Tomorrow|In 2 days/);
    expect(rowOf("Chopped tomatoes").textContent).not.toMatch(/Expired|Today|Tomorrow|In 2 days/);
});

test("the chips count what they show, and Select all shown / Clear selection act on the shown items", () => {
    setup();
    expect(chip(/^All 4/)).toBeInTheDocument();
    expect(chip(/^Expired 1/)).toBeInTheDocument();
    expect(chip(/^Use soon 1/)).toBeInTheDocument();
    expect(chip(/^Dairy & eggs 3/)).toBeInTheDocument();
    expect(chip(/^Tins & jars 1/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Frozen/ })).not.toBeInTheDocument(); // an empty section has no chip

    fireEvent.click(chip(/^Expired/));
    expect(screen.getByText("1 product shown")).toBeInTheDocument();
    expect(screen.queryByText("Greek yoghurt")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Select all shown" }));
    expect(go()).toHaveTextContent("Check out 1 item");
    expect(screen.getByRole("button", { name: "Clear selection" })).toBeInTheDocument();

    // the selection survives a change of filter, and Select all shown then adds the other filter's items
    fireEvent.click(chip(/^Tins & jars/));
    fireEvent.click(screen.getByRole("button", { name: "Select all shown" }));
    expect(go()).toHaveTextContent("Check out 5 items");
    fireEvent.click(screen.getByRole("button", { name: "Clear selection" })); // only what's shown
    expect(go()).toHaveTextContent("Check out 1 item");
});

test("the sheet lists what is leaving, Cancel changes nothing, and nothing is sent until Check out", async () => {
    const { checkOutUnits, product } = setup();
    fireEvent.click(pick("Greek yoghurt"));
    fireEvent.click(screen.getByRole("button", { name: "One fewer Greek yoghurt" }));
    fireEvent.click(pick("Semi-skimmed milk"));
    fireEvent.click(go());

    const sheet = screen.getByRole("dialog");
    expect(within(sheet).getByRole("heading", { name: "Check out 3 items" })).toBeInTheDocument();
    expect(
        within(sheet)
            .getAllByRole("listitem")
            .map((li) => li.textContent),
    ).toEqual(["Semi-skimmed milk 2L×1", "Greek yoghurt×2"]);

    fireEvent.click(within(sheet).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(go()).toHaveTextContent("Check out 3 items"); // the selection is still there
    expect(product).not.toHaveBeenCalled();
    expect(checkOutUnits).not.toHaveBeenCalled();
});

test("confirming checks out the soonest-expiring units of each product in one request, then Undo puts exactly those back", async () => {
    const { checkOutUnits, undoCheckOut, onChanged } = setup();
    fireEvent.click(pick("Greek yoghurt"));
    fireEvent.click(screen.getByRole("button", { name: "One fewer Greek yoghurt" }));
    fireEvent.click(pick("Semi-skimmed milk"));
    fireEvent.click(go());
    await act(async () => {
        fireEvent.click(
            within(screen.getByRole("dialog")).getByRole("button", { name: "Check out 3 items" }),
        );
    });

    expect(checkOutUnits).toHaveBeenCalledTimes(1);
    expect(checkOutUnits).toHaveBeenCalledWith(["milk-1", "yog-1", "yog-2"]); // 2 of the 3 yoghurts, first in check-out order
    expect(onChanged).toHaveBeenCalled();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("3 items checked out");
    expect(go()).toBeDisabled(); // the selection is cleared

    await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Undo" }));
    });
    expect(undoCheckOut.mock.calls.map((c) => c[0]).sort()).toEqual(["milk-1", "yog-1", "yog-2"]);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
});

test("Undo only puts back the units this request checked out, and a failure is counted", async () => {
    const fake = setup();
    fake.checkOutUnits.mockResolvedValueOnce({
        checkedOut: 1,
        unchanged: 1,
        failed: 1,
        units: [
            { unitId: "milk-1", outcome: "checkedOut", productId: "milk" },
            { unitId: "yog-1", outcome: "unchanged", productId: null },
            { unitId: "yog-2", outcome: "failed", productId: null },
        ],
    });
    fireEvent.click(chip(/^Dairy & eggs/));
    fireEvent.click(screen.getByRole("button", { name: "Select all shown" }));
    fireEvent.click(pick("Mature cheddar")); // leave it out
    fireEvent.click(go());
    await act(async () => {
        fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: /^Check out/ }));
    });

    expect(screen.getByRole("status")).toHaveTextContent("1 item checked out, 1 couldn't be");
    await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Undo" }));
    });
    expect(fake.undoCheckOut.mock.calls.map((c) => c[0])).toEqual(["milk-1"]);
});

test("the Undo toast goes after about 9 seconds", async () => {
    vi.useFakeTimers();
    setup();
    fireEvent.click(pick("Semi-skimmed milk"));
    fireEvent.click(go());
    await act(async () => {
        fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: /^Check out/ }));
    });
    expect(screen.getByRole("status")).toBeInTheDocument();
    act(() => {
        vi.advanceTimersByTime(CLEAR_OUT_TOAST_MS + 50);
    });
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    expect(CLEAR_OUT_TOAST_MS).toBe(9000);
});

test("live: a product that goes meanwhile drops out of the selection, and a count never exceeds what's there", () => {
    const { rerender, api, onClose, onChanged } = setup();
    fireEvent.click(pick("Greek yoghurt"));
    fireEvent.click(pick("Semi-skimmed milk"));
    expect(go()).toHaveTextContent("Check out 4 items");

    // another phone checks out the milk and one yoghurt
    rerender(
        <ClearOut
            api={api}
            stock={stockOf({ ...yoghurt, count: 2 }, cheddar, tomatoes)}
            onClose={onClose}
            onChanged={onChanged}
        />,
    );
    expect(screen.queryByText("Semi-skimmed milk 2L")).not.toBeInTheDocument();
    expect(go()).toHaveTextContent("Check out 2 items");
    expect(screen.getByText("2 of 2")).toBeInTheDocument();
});

test("empty states: nothing in a filter, and a clear cupboard; a filter that runs empty falls back to All", () => {
    const { rerender, api, onClose, onChanged } = setup();
    fireEvent.click(chip(/^Expired/));
    rerender(
        <ClearOut api={api} stock={stockOf(yoghurt, tomatoes)} onClose={onClose} onChanged={onChanged} />,
    );
    expect(chip(/^All 2/)).toHaveAttribute("aria-pressed", "true"); // Expired no longer has anything

    rerender(<ClearOut api={api} stock={stockOf()} onClose={onClose} onChanged={onChanged} />);
    expect(screen.getByText("The cupboard is clear.")).toBeInTheDocument();

    rerender(<ClearOut api={api} stock={stockOf(yoghurt)} onClose={onClose} onChanged={onChanged} />);
    expect(screen.queryByText("No items match this filter.")).not.toBeInTheDocument();
});

test("no items match a filter that has none: the message says so", () => {
    setup(stockOf(tomatoes, cheddar));
    fireEvent.click(chip(/^Tins & jars/));
    fireEvent.click(pick("Chopped tomatoes"));
    expect(screen.queryByText("No items match this filter.")).not.toBeInTheDocument();
});

test("on the stock list, Clear out opens the mode and Done leaves it with nothing changed", async () => {
    const stock = vi.fn(async () => STOCK);
    const checkOut = vi.fn();
    const checkOutUnits = vi.fn();
    render(<StockScreen api={{ stock, checkOut, checkOutUnits } as unknown as ApiClient} />);

    fireEvent.click(await screen.findByRole("button", { name: "Clear out" }));
    expect(screen.getByRole("heading", { name: "Clear out" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("heading", { name: "Clear out" })).not.toBeInTheDocument();
    expect(checkOut).not.toHaveBeenCalled();
    expect(checkOutUnits).not.toHaveBeenCalled();
});
