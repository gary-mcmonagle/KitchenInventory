import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ApiClient, QuickIdea, RecipeRequest, RecipeSuggestion, StockProduct } from "./api";
import { MODE_KEY, RecipesScreen } from "./RecipesScreen";

// Hungry? (#88): Cook something | Ready now, the Snack chip, and the Ready now ideas.

const EVENING = new Date("2026-10-18T17:00:00.000Z"); // 18:00 BST

const product = (id: string, name: string, overrides: Partial<StockProduct> = {}): StockProduct => ({
    productId: id,
    barcode: null,
    displayName: name,
    brand: null,
    size: null,
    count: 1,
    latestCheckedInAt: "2026-10-18T08:00:00.000Z",
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

const STOCK = [
    product("hummus", "Test hummus", {
        useFirst: true,
        daysUntilSoonest: -1,
        soonestEstimatedExpiryDate: "2026-10-17",
    }),
    product("bread", "Test bread"),
];

const usesItem = (id: string, name: string, useFirst = false, days: number | null = null) => ({
    productId: id,
    displayName: name,
    useFirst,
    daysUntilSoonest: days,
    soonestEstimatedExpiryDate: null,
});

const IDEAS: QuickIdea[] = [
    {
        title: "Hummus and toast",
        how: "Toast 2 slices and dip.",
        appliance: "toaster",
        minutes: 4,
        uses: [usesItem("hummus", "Test hummus", true, -1), usesItem("bread", "Test bread")],
        alsoNeeded: null,
    },
    {
        title: "Hot chocolate",
        how: "Warm a mug of milk.",
        appliance: "microwave",
        minutes: 3,
        uses: [],
        alsoNeeded: "Hot chocolate powder",
    },
    {
        title: "Cup of tea",
        how: "Boil the kettle.",
        appliance: "kettle",
        minutes: 3,
        uses: [],
        alsoNeeded: null,
    },
    {
        title: "Bread and butter",
        how: "Butter 2 slices.",
        appliance: "none",
        minutes: 2,
        uses: [usesItem("bread", "Test bread")],
        alsoNeeded: null,
    },
];

async function renderScreen(answer?: (r: RecipeRequest) => Promise<RecipeSuggestion>) {
    const suggestRecipe = vi.fn(
        answer ??
            (async (r: RecipeRequest) => ({
                available: true,
                mode: "readyNow" as const,
                meal: r.meal,
                ideas: IDEAS,
            })),
    );
    const api = {
        stock: async () => ({ generatedAt: "", totalUnits: 2, useFirstCount: 1, products: STOCK }),
        suggestRecipe,
        savedRecipes: async () => ({ savedRecipes: [] }),
    } as unknown as ApiClient;
    render(<RecipesScreen api={api} now={EVENING} />);
    await screen.findByRole("combobox", { name: "Include ingredients" });
    await waitFor(() =>
        expect(screen.getByRole("button", { name: /Get a recipe|Show me ideas/ })).toBeEnabled(),
    );
    return suggestRecipe;
}

const modes = () => screen.getByRole("radiogroup", { name: "What kind of food" });

beforeEach(() => localStorage.clear());

test("Cook something | Ready now at the top: cook by default, with its own button wording and hints", async () => {
    await renderScreen();

    expect(
        within(modes())
            .getAllByRole("radio")
            .map((r) => r.textContent),
    ).toEqual(["Cook somethingA proper recipe", "Ready nowToaster, microwave, air fryer"]);
    expect(within(modes()).getByRole("radio", { name: /Cook something/ })).toHaveAttribute(
        "aria-checked",
        "true",
    );
    expect(screen.getByRole("button", { name: "Get a recipe" })).toBeInTheDocument();
    expect(screen.getByText("Builds the recipe around the 1 thing to use first.")).toBeInTheDocument();

    fireEvent.click(within(modes()).getByRole("radio", { name: /Ready now/ }));

    expect(screen.getByRole("button", { name: "Show me ideas" })).toBeInTheDocument();
    expect(screen.getByText("Ideas using the 1 thing to use first come first.")).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: "Additional comments" })).toHaveAttribute(
        "placeholder",
        "e.g. something sweet, no bread",
    );
});

test("the mode is remembered on this device", async () => {
    await renderScreen();
    fireEvent.click(within(modes()).getByRole("radio", { name: /Ready now/ }));
    expect(localStorage.getItem(MODE_KEY)).toBe("readyNow");
});

test("a remembered Ready now opens in Ready now", async () => {
    localStorage.setItem(MODE_KEY, "readyNow");
    await renderScreen();
    expect(within(modes()).getByRole("radio", { name: /Ready now/ })).toHaveAttribute("aria-checked", "true");
});

test("storage that throws falls back to Cook something and choosing still works", async () => {
    const get = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
        throw new Error("blocked");
    });
    const set = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
        throw new Error("blocked");
    });
    try {
        await renderScreen();
        expect(within(modes()).getByRole("radio", { name: /Cook something/ })).toHaveAttribute(
            "aria-checked",
            "true",
        );
        fireEvent.click(within(modes()).getByRole("radio", { name: /Ready now/ }));
        expect(screen.getByRole("button", { name: "Show me ideas" })).toBeInTheDocument();
    } finally {
        get.mockRestore();
        set.mockRestore();
    }
});

test("Ready now · Snack shows the ideas as drawn: minutes, appliance, how, uses with dots and links, the extra", async () => {
    const suggest = await renderScreen();
    fireEvent.click(within(modes()).getByRole("radio", { name: /Ready now/ }));
    fireEvent.click(
        within(screen.getByRole("radiogroup", { name: "Meal" })).getByRole("radio", { name: "Snack" }),
    );

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Show me ideas" })));

    expect(suggest).toHaveBeenCalledWith(
        expect.objectContaining({ mode: "readyNow", meal: "snack", avoidTitles: [] }),
    );
    expect(screen.getByText("Ready now · Snack")).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1, name: "4 quick ideas" })).toBeInTheDocument();
    expect(screen.getByText(/Things to use first come first\./)).toBeInTheDocument();

    const first = screen.getByRole("article", { name: "Hummus and toast" });
    expect(first).toHaveTextContent("4 min");
    expect(first).toHaveTextContent("Toaster");
    expect(first).toHaveTextContent("Toast 2 slices and dip.");
    expect(first).toHaveTextContent("Check the test hummus is still good first.");
    const hummus = within(first).getByRole("link", { name: "Test hummus (estimated expired)" });
    expect(hummus).toHaveAttribute("href", "/products/hummus");
    expect(hummus.querySelector('[class*="useDotExpired"]')).not.toBeNull();
    expect(within(first).getByRole("link", { name: "Test bread" })).toBeInTheDocument();

    expect(screen.getByRole("article", { name: "Hot chocolate" })).toHaveTextContent(
        "You'll also need: Hot chocolate powder",
    );
    expect(screen.getByRole("article", { name: "Cup of tea" })).toHaveTextContent("Kettle");
    expect(screen.getByRole("article", { name: "Bread and butter" })).toHaveTextContent("No cooking");
});

test("More ideas asks again avoiding every title shown; Start again goes back to the form", async () => {
    const suggest = await renderScreen();
    fireEvent.click(within(modes()).getByRole("radio", { name: /Ready now/ }));
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Show me ideas" })));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "More ideas" })));

    expect(suggest.mock.calls[1][0].avoidTitles).toEqual(IDEAS.map((i) => i.title));
    fireEvent.click(screen.getByRole("button", { name: "Start again" }));
    expect(screen.getByRole("heading", { level: 1, name: "Hungry?" })).toBeInTheDocument();
    expect(within(modes()).getByRole("radio", { name: /Ready now/ })).toHaveAttribute("aria-checked", "true");
});

test("no ideas from the model is the friendly message with Try again", async () => {
    await renderScreen(async () => ({
        available: false,
        message: "Couldn't come up with a recipe just now. Try again.",
    }));
    fireEvent.click(within(modes()).getByRole("radio", { name: /Ready now/ }));
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Show me ideas" })));

    expect(screen.getByRole("alert")).toHaveTextContent("Couldn't come up with a recipe just now.");
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
});
