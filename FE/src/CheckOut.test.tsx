import { act, fireEvent, render, screen } from "@testing-library/react";
import {
    ApiError,
    type ApiClient,
    type CheckedOut,
    type Stock,
    type StockProduct,
    type UndoneCheckOut,
} from "./api";
import { StockScreen, TOAST_MS, withCount } from "./StockScreen";

/** Status messages other than the scan feed region (#54). */
const notFeed = { name: (name: string) => name !== "Scan feed" };

const gravy: StockProduct = {
    productId: "p-gravy",
    barcode: "5000354922831",
    displayName: "Beef Gravy",
    brand: "Bisto",
    size: "230g",
    count: 2,
    latestCheckedInAt: "2026-10-17T18:00:00.000Z",
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
const naan: StockProduct = {
    ...gravy,
    productId: "p-naan",
    displayName: "PLAIN NAAN BREADS",
    brand: null,
    size: null,
    count: 1,
};
const stock: Stock = { generatedAt: "", totalUnits: 3, useFirstCount: 0, products: [gravy, naan] };

function deferred<T>() {
    let resolve!: (v: T) => void;
    let reject!: (e: unknown) => void;
    const promise = new Promise<T>((res, rej) => ((resolve = res), (reject = rej)));
    return { promise, resolve, reject };
}

function setup(overrides: Partial<Record<"checkOut" | "undoCheckOut", ReturnType<typeof vi.fn>>> = {}) {
    // A stateful fake: the screen reloads quietly after a check-out or undo, and should see the new counts.
    const counts = new Map(stock.products.map((p) => [p.productId, p.count]));
    const current = (): Stock => {
        const products = stock.products
            .filter((p) => counts.get(p.productId)! > 0)
            .map((p) => ({ ...p, count: counts.get(p.productId)! }));
        return { ...stock, products, totalUnits: products.reduce((n, p) => n + p.count, 0) };
    };
    const api = {
        stock: vi.fn(async () => current()),
        checkOut: vi.fn(async (productId: string): Promise<CheckedOut> => {
            counts.set(productId, counts.get(productId)! - 1);
            return {
                unitId: `unit-of-${productId}`,
                productId,
                displayName: productId === "p-gravy" ? "Beef Gravy" : "PLAIN NAAN BREADS",
                remainingCount: counts.get(productId)!,
            };
        }),
        undoCheckOut: vi.fn(async (unitId: string): Promise<UndoneCheckOut> => {
            const productId = unitId.replace("unit-of-", "");
            counts.set(productId, counts.get(productId)! + 1);
            return { unitId, productId, count: counts.get(productId)!, restored: true };
        }),
        ...overrides,
    };
    render(<StockScreen api={api as unknown as ApiClient} />);
    return api;
}

const checkOutButton = (name: string) => screen.getByRole("button", { name: `Remove one ${name}` });

afterEach(() => vi.useRealTimers());

test("check out drops the count at once and shows the toast", async () => {
    const api = setup();
    await screen.findByText("Bisto · 230g · ×2");

    await act(async () => fireEvent.click(checkOutButton("Beef Gravy")));

    expect(api.checkOut).toHaveBeenCalledWith("p-gravy");
    expect(screen.getByText("Bisto · 230g")).toBeInTheDocument(); // ×2 → 1
    expect(screen.getByText("2 items · 0 to use first")).toBeInTheDocument(); // header total (3 → 2)
    expect(screen.getByRole("status", notFeed)).toHaveTextContent("Checked out Beef Gravy");
});

test("the row disappears at 0", async () => {
    setup();
    await screen.findByText("PLAIN NAAN BREADS");

    await act(async () => fireEvent.click(checkOutButton("PLAIN NAAN BREADS")));

    expect(screen.queryByText("PLAIN NAAN BREADS")).not.toBeInTheDocument();
    expect(screen.getByRole("status", notFeed)).toHaveTextContent("Checked out PLAIN NAAN BREADS");
});

test("the count drops before the API answers (optimistic), and the button is disabled meanwhile", async () => {
    const pending = deferred<CheckedOut>();
    const api = setup({ checkOut: vi.fn(() => pending.promise) });
    await screen.findByText("Bisto · 230g · ×2");

    fireEvent.click(checkOutButton("Beef Gravy"));
    expect(screen.getByText("Bisto · 230g")).toBeInTheDocument();
    expect(checkOutButton("Beef Gravy")).toBeDisabled();
    fireEvent.click(checkOutButton("Beef Gravy")); // a quick second tap
    expect(api.checkOut).toHaveBeenCalledTimes(1);

    await act(async () =>
        pending.resolve({ unitId: "u1", productId: "p-gravy", displayName: "Beef Gravy", remainingCount: 1 }),
    );
    expect(checkOutButton("Beef Gravy")).toBeEnabled();
});

test("Undo puts it back", async () => {
    const api = setup();
    await screen.findByText("PLAIN NAAN BREADS");
    await act(async () => fireEvent.click(checkOutButton("PLAIN NAAN BREADS")));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo" })));

    expect(api.undoCheckOut).toHaveBeenCalledWith("unit-of-p-naan");
    expect(screen.getByText("PLAIN NAAN BREADS")).toBeInTheDocument();
    expect(screen.getByText("3 items · 0 to use first")).toBeInTheDocument();
    expect(screen.queryByRole("status", notFeed)).not.toBeInTheDocument();
});

test("the toast goes after 6 seconds", async () => {
    setup();
    await screen.findByText("PLAIN NAAN BREADS");
    vi.useFakeTimers();
    await act(async () => fireEvent.click(checkOutButton("PLAIN NAAN BREADS")));
    expect(screen.getByRole("status", notFeed)).toBeInTheDocument();

    act(() => vi.advanceTimersByTime(TOAST_MS - 100));
    expect(screen.getByRole("status", notFeed)).toBeInTheDocument();
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByRole("status", notFeed)).not.toBeInTheDocument();
});

test("a new check-out replaces the toast, so Undo applies to the latest", async () => {
    const api = setup();
    await screen.findByText("PLAIN NAAN BREADS");
    await act(async () => fireEvent.click(checkOutButton("Beef Gravy")));
    await act(async () => fireEvent.click(checkOutButton("PLAIN NAAN BREADS")));

    expect(screen.getAllByRole("status", notFeed)).toHaveLength(1);
    expect(screen.getByRole("status", notFeed)).toHaveTextContent("Checked out PLAIN NAAN BREADS");
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo" })));
    expect(api.undoCheckOut).toHaveBeenCalledWith("unit-of-p-naan");
});

test("a failed check-out reverts the count and says so", async () => {
    setup({ checkOut: vi.fn(async () => Promise.reject(new ApiError(500, "boom"))) });
    await screen.findByText("Bisto · 230g · ×2");

    await act(async () => fireEvent.click(checkOutButton("Beef Gravy")));

    expect(screen.getByText("Bisto · 230g · ×2")).toBeInTheDocument();
    expect(screen.getByRole("status", notFeed)).toHaveTextContent("Couldn't check that out. Try again.");
    expect(screen.queryByRole("button", { name: "Undo" })).not.toBeInTheDocument();
});

test("nothing left (409): reverts, says so and reloads the list", async () => {
    const api = setup({ checkOut: vi.fn(async () => Promise.reject(new ApiError(409, "none"))) });
    await screen.findByText("Bisto · 230g · ×2");

    await act(async () => fireEvent.click(checkOutButton("Beef Gravy")));

    expect(screen.getByRole("status", notFeed)).toHaveTextContent("There's no Beef Gravy left to check out.");
    expect(api.stock).toHaveBeenCalledTimes(2);
});

test("a failed undo reverts and says so", async () => {
    setup({ undoCheckOut: vi.fn(async () => Promise.reject(new ApiError(500, "boom"))) });
    await screen.findByText("PLAIN NAAN BREADS");
    await act(async () => fireEvent.click(checkOutButton("PLAIN NAAN BREADS")));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo" })));

    expect(screen.queryByText("PLAIN NAAN BREADS")).not.toBeInTheDocument();
    expect(screen.getByRole("status", notFeed)).toHaveTextContent("Couldn't undo that. Try again.");
});

test("withCount keeps the API's order when a row comes back", () => {
    const apple: StockProduct = { ...gravy, productId: "p-apple", displayName: "apples", count: 1 };
    const unknown: StockProduct = {
        ...gravy,
        productId: "p-x",
        displayName: "Unknown item (1)",
        status: "noMatch",
        section: "waitingForName",
        count: 1,
    };
    const s: Stock = { generatedAt: "", totalUnits: 3, useFirstCount: 0, products: [gravy, naan, unknown] };

    const back = withCount(s, apple, 1);
    expect(back.products.map((p) => p.productId)).toEqual(["p-apple", "p-gravy", "p-naan", "p-x"]);
    expect(back.totalUnits).toBe(5);
    expect(withCount(s, naan, 0).products.map((p) => p.productId)).toEqual(["p-gravy", "p-x"]);
});

test("withCount puts a Use first row back by soonest expiry and recounts Use first", () => {
    const due = (id: string, date: string, name = id): StockProduct => ({
        ...gravy,
        productId: id,
        displayName: name,
        count: 1,
        section: "useFirst",
        soonestEstimatedExpiryDate: date,
        daysUntilSoonest: 1,
    });
    const s: Stock = {
        generatedAt: "",
        totalUnits: 4,
        useFirstCount: 2,
        products: [due("a", "2026-10-17"), due("c", "2026-10-19"), gravy],
    };

    const back = withCount(s, due("b", "2026-10-18"), 1);

    expect(back.products.map((p) => p.productId)).toEqual(["a", "b", "c", "p-gravy"]);
    expect(back.useFirstCount).toBe(3);
    expect(withCount(s, s.products[0], 0).useFirstCount).toBe(1);
});
