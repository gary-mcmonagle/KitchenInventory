import { act, fireEvent, render, screen, within } from "@testing-library/react";
import type { ApiClient, ExpiredUnit, Stock, StockAsk, StockProduct } from "./api";
import { CheckSheet, checkFromUrl } from "./CheckSheet";
import { daysOver, expiredUnitMeta, stillGotThisWhy } from "./format";
import { StockScreen } from "./StockScreen";

// "Still got this?" on items at their estimate (#94): the row prompt, Still here, Gone, the aisle pill and the sheet.

const YEAR = new Date().getFullYear();
const day = (monthDay: string) => `${YEAR}-${monthDay}`;

const ask = (overrides: Partial<StockAsk> = {}): StockAsk => ({
    unitId: "unit-1",
    estimatedExpiryDate: day("10-20"),
    boughtOn: day("10-14"),
    openedOn: null,
    daysOver: 0,
    askingCount: 1,
    ...overrides,
});

const product = (overrides: Partial<StockProduct> = {}): StockProduct => ({
    productId: "hummus",
    barcode: "5000354922831",
    displayName: "Hummus",
    brand: null,
    size: null,
    count: 1,
    latestCheckedInAt: `${day("10-14")}T09:00:00.000Z`,
    status: "known",
    section: "useFirst",
    soonestEstimatedExpiryDate: day("10-20"),
    daysUntilSoonest: 0,
    shelfLifeDays: 6,
    openedCount: 0,
    frozenCount: 0,
    soonestState: null,
    soonestStateOn: null,
    aisleId: "chilledDeli",
    icon: null,
    useFirst: true,
    ask: ask(),
    checkedToday: false,
    ...overrides,
});

const stockOf = (...products: StockProduct[]): Stock => ({
    generatedAt: `${day("10-20")}T17:00:00.000Z`,
    totalUnits: products.reduce((n, p) => n + p.count, 0),
    useFirstCount: products.filter((p) => p.section === "useFirst").length,
    products,
});

/** A fake API whose stock is `state.stock`, so a test can change what the next load returns. */
function fakeApi(initial: Stock) {
    const state = { stock: initial };
    const api = {
        state,
        stock: vi.fn(async () => state.stock),
        scanners: vi.fn(async () => {
            throw new Error("not needed");
        }),
        stillHere: vi.fn(async (unitId: string) => ({ unitId, productId: "hummus", lastConfirmedAt: "now" })),
        undoStillHere: vi.fn(async (unitId: string) => ({
            unitId,
            productId: "hummus",
            lastConfirmedAt: null,
        })),
        unitGone: vi.fn(async (unitId: string) => ({
            unitId,
            productId: "hummus",
            displayName: "Hummus",
            remainingCount: 0,
        })),
        undoCheckOut: vi.fn(async (unitId: string) => ({
            unitId,
            productId: "hummus",
            count: 1,
            restored: true,
        })),
        expiredUnits: vi.fn(async () => ({ units: [] as ExpiredUnit[] })),
    };
    return api;
}

const show = (api: ReturnType<typeof fakeApi>) => render(<StockScreen api={api as unknown as ApiClient} />);

beforeEach(() => window.history.replaceState(null, "", "/"));

test("a unit on its estimated day asks on its row, as drawn, in place of the buttons", async () => {
    show(fakeApi(stockOf(product())));

    const row = (await screen.findByText("Still got this?")).closest("li")!;
    expect(row).toHaveTextContent(`Estimated to last until today. Bought 14 Oct.`);
    expect(within(row).getByRole("button", { name: "Hummus: still here" })).toHaveTextContent("Still here");
    expect(within(row).getByRole("button", { name: "Hummus: gone" })).toHaveTextContent("Gone");
    expect(within(row).queryByRole("button", { name: "Remove one Hummus" })).not.toBeInTheDocument();
});

test("the aisle's red pill counts what to check, in place of the use-first pill", async () => {
    show(fakeApi(stockOf(product(), product({ productId: "ham", displayName: "Ham", ask: null }))));

    const header = await screen.findByRole("button", { name: /Chilled & deli/ });
    expect(header).toHaveTextContent("1 to check");
    expect(header).not.toHaveTextContent("to use first");
});

test("Still here: the prompt goes, Checked shows with its note, and Undo asks again", async () => {
    const api = fakeApi(stockOf(product()));
    api.stillHere.mockImplementation(async (unitId: string) => {
        api.state.stock = stockOf(product({ ask: null, checkedToday: true }));
        return { unitId, productId: "hummus", lastConfirmedAt: "now" };
    });
    api.undoStillHere.mockImplementation(async (unitId: string) => {
        api.state.stock = stockOf(product());
        return { unitId, productId: "hummus", lastConfirmedAt: null };
    });
    show(api);

    const button = await screen.findByRole("button", { name: "Hummus: still here" });
    await act(async () => fireEvent.click(button));

    expect(api.stillHere).toHaveBeenCalledWith("unit-1");
    expect(screen.queryByText("Still got this?")).not.toBeInTheDocument();
    expect(screen.getByText("Checked")).toBeInTheDocument();
    expect(screen.getByText("You said it's still here · we'll ask again in 2 days")).toBeInTheDocument();
    const toast = screen.getByText("OK, we'll ask again in 2 days").closest("[role=status]") as HTMLElement;

    await act(async () => fireEvent.click(within(toast).getByRole("button", { name: "Undo" })));
    expect(api.undoStillHere).toHaveBeenCalledWith("unit-1");
    expect(await screen.findByText("Still got this?")).toBeInTheDocument();
});

test("Gone takes that unit out with a toast, and Undo puts it back", async () => {
    const api = fakeApi(stockOf(product()));
    api.unitGone.mockImplementation(async (unitId: string) => {
        api.state.stock = stockOf();
        return { unitId, productId: "hummus", displayName: "Hummus", remainingCount: 0 };
    });
    show(api);

    const button = await screen.findByRole("button", { name: "Hummus: gone" });
    await act(async () => fireEvent.click(button));

    expect(api.unitGone).toHaveBeenCalledWith("unit-1");
    const toast = screen.getByText("Marked Hummus as gone").closest("[role=status]") as HTMLElement;
    await act(async () => fireEvent.click(within(toast).getByRole("button", { name: "Undo" })));
    expect(api.undoCheckOut).toHaveBeenCalledWith("unit-1");
});

test("the why line: days ago, opened, and how many are asking", () => {
    const now = new Date(`${day("10-20")}T12:00:00Z`);
    expect(
        stillGotThisWhy(
            ask({
                estimatedExpiryDate: day("10-16"),
                boughtOn: day("10-10"),
                openedOn: day("10-12"),
                daysOver: 4,
                askingCount: 2,
            }),
            3,
            now,
        ),
    ).toBe(
        "Estimated to last until 16 Oct, 4 days ago (opened 12 Oct). Bought 10 Oct. 2 of 3 are at their estimate.",
    );
    expect(stillGotThisWhy(ask({ estimatedExpiryDate: day("10-19"), daysOver: 1 }), 1, now)).toBe(
        "Estimated to last until 19 Oct, 1 day ago. Bought 14 Oct.",
    );
    expect(
        expiredUnitMeta(
            {
                storage: "fridge",
                boughtOn: day("10-10"),
                openedOn: day("10-18"),
                estimatedExpiryDate: day("10-21"),
            },
            now,
        ),
    ).toBe("Fridge · opened 18 Oct · estimated until 21 Oct");
    expect(
        expiredUnitMeta(
            { storage: null, boughtOn: day("10-10"), openedOn: null, estimatedExpiryDate: day("10-16") },
            now,
        ),
    ).toBe("bought 10 Oct · estimated until 16 Oct");
    expect([daysOver(1), daysOver(9)]).toEqual(["1 day over", "9 days over"]);
});

// ---- The sheet

const expired = (unitId: string, name: string, over: number): ExpiredUnit => ({
    unitId,
    productId: unitId,
    name,
    storage: "fridge",
    boughtOn: day("10-10"),
    openedOn: null,
    estimatedExpiryDate: day("10-16"),
    daysOver: over,
});

test("the push's link (/?check=expired) opens the sheet once over the stock list", async () => {
    window.history.replaceState(null, "", "/?check=expired");
    expect(checkFromUrl()).toBe(true);
    const api = fakeApi(stockOf(product({ ask: null })));
    api.expiredUnits.mockResolvedValue({ units: [expired("u1", "Hummus", 9)] });
    show(api);

    expect(await screen.findByRole("dialog", { name: "Still got these?" })).toBeInTheDocument();
    expect(window.location.search).toBe("");
    expect(await screen.findByText("1 expired item is still in stock.")).toBeInTheDocument();
});

test("the sheet: answer each row, see what happened, Undo, and Not now becomes Done", async () => {
    const api = fakeApi(stockOf());
    api.expiredUnits.mockResolvedValue({
        units: [expired("u1", "Hummus", 9), expired("u2", "Cooked ham", 4)],
    });
    const onClose = vi.fn();
    render(<CheckSheet api={api as unknown as ApiClient} onClose={onClose} />);

    expect(await screen.findByText("2 expired items are still in stock.")).toBeInTheDocument();
    expect(screen.getByText("9 days over")).toBeInTheDocument();
    expect(screen.getAllByText("Fridge · bought 10 Oct · estimated until 16 Oct")).toHaveLength(2);
    expect(screen.getByRole("button", { name: "Not now" })).toBeInTheDocument();

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Hummus: still here" })));
    expect(api.stillHere).toHaveBeenCalledWith("u1");
    expect(screen.getByText("Still here: we'll ask again in 2 days")).toBeInTheDocument();

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Cooked ham: gone" })));
    expect(api.unitGone).toHaveBeenCalledWith("u2");
    expect(screen.getByText("Gone: taken out of stock")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Done" })).toBeInTheDocument();

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo Cooked ham" })));
    expect(api.undoCheckOut).toHaveBeenCalledWith("u2");
    expect(screen.getByRole("button", { name: "Cooked ham: gone" })).toBeInTheDocument();
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo Hummus" })));
    expect(api.undoStillHere).toHaveBeenCalledWith("u1");

    fireEvent.click(screen.getByRole("button", { name: "Not now" }));
    expect(onClose).toHaveBeenCalled();
});

test("with nothing left to check, the sheet says so and offers Done", async () => {
    const api = fakeApi(stockOf());
    render(<CheckSheet api={api as unknown as ApiClient} onClose={() => undefined} />);

    expect(await screen.findByText("Nothing to check right now")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Done" })).toBeInTheDocument();
});
