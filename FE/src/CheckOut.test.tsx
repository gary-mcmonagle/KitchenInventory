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

const gravy: StockProduct = {
    productId: "p-gravy",
    barcode: "5000354922831",
    displayName: "Beef Gravy",
    brand: "Bisto",
    size: "230g",
    count: 2,
    latestCheckedInAt: "2026-10-17T18:00:00.000Z",
    status: "known",
};
const naan: StockProduct = {
    ...gravy,
    productId: "p-naan",
    displayName: "PLAIN NAAN BREADS",
    brand: null,
    size: null,
    count: 1,
};
const stock: Stock = { generatedAt: "", totalUnits: 3, products: [gravy, naan] };

function deferred<T>() {
    let resolve!: (v: T) => void;
    let reject!: (e: unknown) => void;
    const promise = new Promise<T>((res, rej) => ((resolve = res), (reject = rej)));
    return { promise, resolve, reject };
}

function setup(overrides: Partial<Record<"checkOut" | "undoCheckOut", ReturnType<typeof vi.fn>>> = {}) {
    const api = {
        stock: vi.fn(async () => stock),
        checkOut: vi.fn(async (productId: string): Promise<CheckedOut> => ({
            unitId: `unit-of-${productId}`,
            productId,
            displayName: productId === "p-gravy" ? "Beef Gravy" : "PLAIN NAAN BREADS",
            remainingCount: productId === "p-gravy" ? 1 : 0,
        })),
        undoCheckOut: vi.fn(async (unitId: string): Promise<UndoneCheckOut> => ({
            unitId,
            productId: unitId.replace("unit-of-", ""),
            count: unitId === "unit-of-p-gravy" ? 2 : 1,
            restored: true,
        })),
        ...overrides,
    };
    render(<StockScreen api={api as unknown as ApiClient} />);
    return api;
}

const checkOutButton = (name: string) => screen.getByRole("button", { name: `Check out one ${name}` });

afterEach(() => vi.useRealTimers());

test("check out drops the count at once and shows the toast", async () => {
    const api = setup();
    await screen.findByText("Bisto · 230g · ×2");

    await act(async () => fireEvent.click(checkOutButton("Beef Gravy")));

    expect(api.checkOut).toHaveBeenCalledWith("p-gravy");
    expect(screen.getByText("Bisto · 230g")).toBeInTheDocument(); // ×2 → 1
    expect(screen.getAllByText("2 items")[0]).toBeInTheDocument(); // header total (3 → 2)
    expect(screen.getByRole("status")).toHaveTextContent("Checked out Beef Gravy");
});

test("the row disappears at 0", async () => {
    setup();
    await screen.findByText("PLAIN NAAN BREADS");

    await act(async () => fireEvent.click(checkOutButton("PLAIN NAAN BREADS")));

    expect(screen.queryByText("PLAIN NAAN BREADS")).not.toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Checked out PLAIN NAAN BREADS");
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
    expect(screen.getByText("3 items")).toBeInTheDocument();
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
});

test("the toast goes after 6 seconds", async () => {
    setup();
    await screen.findByText("PLAIN NAAN BREADS");
    vi.useFakeTimers();
    await act(async () => fireEvent.click(checkOutButton("PLAIN NAAN BREADS")));
    expect(screen.getByRole("status")).toBeInTheDocument();

    act(() => vi.advanceTimersByTime(TOAST_MS - 100));
    expect(screen.getByRole("status")).toBeInTheDocument();
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
});

test("a new check-out replaces the toast, so Undo applies to the latest", async () => {
    const api = setup();
    await screen.findByText("PLAIN NAAN BREADS");
    await act(async () => fireEvent.click(checkOutButton("Beef Gravy")));
    await act(async () => fireEvent.click(checkOutButton("PLAIN NAAN BREADS")));

    expect(screen.getAllByRole("status")).toHaveLength(1);
    expect(screen.getByRole("status")).toHaveTextContent("Checked out PLAIN NAAN BREADS");
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo" })));
    expect(api.undoCheckOut).toHaveBeenCalledWith("unit-of-p-naan");
});

test("a failed check-out reverts the count and says so", async () => {
    setup({ checkOut: vi.fn(async () => Promise.reject(new ApiError(500, "boom"))) });
    await screen.findByText("Bisto · 230g · ×2");

    await act(async () => fireEvent.click(checkOutButton("Beef Gravy")));

    expect(screen.getByText("Bisto · 230g · ×2")).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Couldn't check that out. Try again.");
    expect(screen.queryByRole("button", { name: "Undo" })).not.toBeInTheDocument();
});

test("nothing left (409): reverts, says so and reloads the list", async () => {
    const api = setup({ checkOut: vi.fn(async () => Promise.reject(new ApiError(409, "none"))) });
    await screen.findByText("Bisto · 230g · ×2");

    await act(async () => fireEvent.click(checkOutButton("Beef Gravy")));

    expect(screen.getByRole("status")).toHaveTextContent("There's no Beef Gravy left to check out.");
    expect(api.stock).toHaveBeenCalledTimes(2);
});

test("a failed undo reverts and says so", async () => {
    setup({ undoCheckOut: vi.fn(async () => Promise.reject(new ApiError(500, "boom"))) });
    await screen.findByText("PLAIN NAAN BREADS");
    await act(async () => fireEvent.click(checkOutButton("PLAIN NAAN BREADS")));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo" })));

    expect(screen.queryByText("PLAIN NAAN BREADS")).not.toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Couldn't undo that. Try again.");
});

test("withCount keeps the API's order when a row comes back", () => {
    const apple: StockProduct = { ...gravy, productId: "p-apple", displayName: "apples", count: 1 };
    const unknown: StockProduct = {
        ...gravy,
        productId: "p-x",
        displayName: "Unknown item (1)",
        status: "noMatch",
        count: 1,
    };
    const s: Stock = { generatedAt: "", totalUnits: 3, products: [gravy, naan, unknown] };

    const back = withCount(s, apple, 1);
    expect(back.products.map((p) => p.productId)).toEqual(["p-apple", "p-gravy", "p-naan", "p-x"]);
    expect(back.totalUnits).toBe(5);
    expect(withCount(s, naan, 0).products.map((p) => p.productId)).toEqual(["p-gravy", "p-x"]);
});
