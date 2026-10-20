import { act, fireEvent, render, screen } from "@testing-library/react";
import type { ApiClient, Product, Recipe, StockProduct } from "./api";
import { forgetScrollPositions } from "./router";
import { Shell } from "./Shell";

// Going back to the stock list lands where you were (#103, spike #99 option A).

const row = (id: string, name: string, overrides: Partial<StockProduct> = {}): StockProduct => ({
    productId: id,
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
    aisleId: "dairyEggs",
    icon: null,
    useFirst: false,
    genericName: null,
    ...overrides,
});

const PRODUCTS = [
    row("p-milk", "Semi-skimmed milk", {
        section: "useFirst",
        useFirst: true,
        daysUntilSoonest: 1,
        soonestEstimatedExpiryDate: "2026-10-21",
    }),
    row("p-cheese", "Mature cheddar"),
    row("p-beans", "Baked beans", { aisleId: "tinsJars" }),
];

const product = (id: string, name: string): Product =>
    ({
        productId: id,
        displayName: name,
        status: "known",
        units: [],
        genericName: null,
        aisleId: "dairyEggs",
        icon: null,
        brand: null,
        size: null,
    }) as unknown as Product;

const RECIPE: Recipe = {
    title: "Cheese toastie",
    summary: "Quick.",
    meal: "lunch",
    servings: 1,
    totalMinutes: 10,
    ingredients: [{ name: "Mature cheddar", amount: "50 g", productId: "p-cheese", useFirst: false }],
    steps: ["Toast it."],
    usesUp: [],
    alsoNeeded: [],
    couldNotFitIn: [],
};

function shellApi() {
    return {
        me: async () => ({ name: "Cupboard test", objectId: "o" }),
        ping: async () => ({ status: "ok", commit: "abc1234" }),
        stock: vi.fn(async () => ({ generatedAt: "", totalUnits: 3, useFirstCount: 1, products: PRODUCTS })),
        scanners: async () => {
            throw new Error("not needed");
        },
        product: vi.fn(async (id: string) =>
            product(id, PRODUCTS.find((p) => p.productId === id)?.displayName ?? "Item"),
        ),
        suggestRecipe: vi.fn(async () => ({ available: true, mode: "cook", recipe: RECIPE })),
        runOutStatus: vi.fn(async () => ({
            genericNameId: "x",
            name: "x",
            addWhenOut: false,
            inStock: 1,
            canSet: false,
        })),
    };
}

let scrollY = 0;
const scrollTo = vi.fn((_x: number, y: number) => {
    scrollY = y;
});

beforeEach(() => {
    window.history.replaceState(null, "", "/");
    forgetScrollPositions();
    scrollY = 0;
    scrollTo.mockClear();
    Object.defineProperty(window, "scrollY", { configurable: true, get: () => scrollY });
    window.scrollTo = scrollTo as unknown as typeof window.scrollTo;
});

async function start(path = "/") {
    window.history.replaceState(null, "", path);
    const api = shellApi();
    render(<Shell api={api as unknown as ApiClient} buildSha="def5678" />);
    await act(async () => undefined);
    return api;
}

const goBack = async () => {
    await act(async () => {
        window.history.back();
        await new Promise((r) => setTimeout(r, 30));
    });
};

test("back from item detail lands at the same scroll, with no refetch and no loading placeholders", async () => {
    const api = await start();
    await screen.findByText("Mature cheddar");
    scrollY = 2500; // scrolled far down

    fireEvent.click(screen.getByRole("link", { name: "Mature cheddar" }));
    await act(async () => undefined);
    expect(window.location.pathname).toBe("/products/p-cheese");
    expect(scrollTo).toHaveBeenLastCalledWith(0, 0); // item detail opens at the top

    await goBack();

    expect(window.location.pathname).toBe("/");
    expect(scrollTo).toHaveBeenLastCalledWith(0, 2500);
    expect(api.stock).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("status", { name: "Loading your stock" })).not.toBeInTheDocument();
    expect(screen.getByText("Mature cheddar")).toBeVisible();
});

test("‹ Stock steps back through history (it doesn't grow), and lands where you were", async () => {
    await start();
    await screen.findByText("Mature cheddar");
    scrollY = 1200;

    fireEvent.click(screen.getByRole("link", { name: "Baked beans" }));
    await act(async () => undefined);
    const back = await screen.findByRole("button", { name: "Back to stock" });
    const historyBack = vi.spyOn(window.history, "back");
    const pushState = vi.spyOn(window.history, "pushState");
    fireEvent.click(back);
    await act(async () => new Promise((r) => setTimeout(r, 30)));

    expect(historyBack).toHaveBeenCalled();
    expect(pushState).not.toHaveBeenCalled(); // no new list entry: history doesn't grow
    expect(window.location.pathname).toBe("/");
    pushState.mockRestore();
    expect(scrollTo).toHaveBeenLastCalledWith(0, 1200);
    historyBack.mockRestore();
});

test("Use first and the search are as you left them", async () => {
    await start();
    await screen.findByText("Mature cheddar");
    fireEvent.click(screen.getByRole("button", { name: "Use first · 1" }));
    fireEvent.change(screen.getByRole("searchbox", { name: "Search stock" }), { target: { value: "milk" } });

    fireEvent.click(screen.getByRole("link", { name: "Semi-skimmed milk" }));
    await act(async () => undefined);
    await goBack();

    expect(screen.getByRole("button", { name: "Use first · 1" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("searchbox", { name: "Search stock" })).toHaveValue("milk");
    expect(screen.queryByText("Mature cheddar")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Clear search" }));
});

test("opened directly, ‹ Stock goes to the top of the list", async () => {
    const api = await start("/products/p-milk");
    const back = await screen.findByRole("button", { name: "Back to stock" });
    const historyBack = vi.spyOn(window.history, "back");

    fireEvent.click(back);
    await act(async () => undefined);

    expect(historyBack).not.toHaveBeenCalled(); // nothing behind it to go back to
    expect(window.location.pathname).toBe("/");
    expect(scrollTo).toHaveBeenLastCalledWith(0, 0);
    expect(await screen.findByText("Mature cheddar")).toBeInTheDocument();
    expect(api.stock).toHaveBeenCalled();
    historyBack.mockRestore();
});

test("a recipe survives a trip to item detail, and ‹ goes back to it", async () => {
    const api = await start("/recipes");
    await act(async () => fireEvent.click(await screen.findByRole("button", { name: "Get a recipe" })));
    expect(screen.getByRole("heading", { level: 1, name: "Cheese toastie" })).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole("link", { name: /Mature cheddar/ })[0]);
    await act(async () => undefined);
    expect(window.location.pathname).toBe("/products/p-cheese");
    const back = await screen.findByRole("button", { name: "Back to Hungry?" });
    expect(screen.getByText("Hungry?", { selector: "span" })).toBeInTheDocument();

    fireEvent.click(back);
    await act(async () => new Promise((r) => setTimeout(r, 30)));

    expect(window.location.pathname).toBe("/recipes");
    expect(screen.getByRole("heading", { level: 1, name: "Cheese toastie" })).toBeVisible();
    expect(api.suggestRecipe).toHaveBeenCalledTimes(1); // not asked again
});
