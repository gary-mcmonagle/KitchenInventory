import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ApiClient, QuickIdea, Recipe, RecipeRequest, RecipeSuggestion, StockProduct } from "./api";
import { RecipesScreen } from "./RecipesScreen";

// Meat or veg? (#110): the switch, the request, half & half's plate tags, Veggie in the meta line, and idea warnings.

const EVENING = new Date("2026-10-21T17:00:00.000Z");

const STOCK: StockProduct[] = [
    {
        productId: "mince",
        barcode: null,
        displayName: "Test mince",
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
        aisleId: "meatFish",
        icon: null,
        useFirst: false,
    },
];

const HALF: Recipe = {
    title: "Test two-pan chilli",
    summary: "",
    meal: "dinner",
    servings: 2,
    totalMinutes: 40,
    ingredients: [
        { name: "Test tomatoes", amount: "1 tin", productId: null, useFirst: false, for: "all" },
        { name: "Test mince", amount: "250 g", productId: "mince", useFirst: false, for: "meat" },
        { name: "Test beans", amount: "1 tin", productId: null, useFirst: false, for: "veg" },
    ],
    steps: [
        { text: "Make the sauce and split it.", for: "all" },
        { text: "Brown the mince.", for: "meat" },
        { text: "Stir in the beans.", for: "veg" },
    ],
    usesUp: [],
    alsoNeeded: [{ name: "1 tsp Worcestershire sauce", for: "meat" }],
    couldNotFitIn: [],
    equipment: ["hob"],
    warnings: [],
    diet: "half",
    servingsMeat: 1,
    servingsVeg: 1,
};

const VEG: Recipe = {
    ...HALF,
    title: "Test bean chilli",
    totalMinutes: 25,
    ingredients: [{ name: "Test beans", amount: "1 tin", productId: null, useFirst: false, for: "all" }],
    steps: [{ text: "Simmer the beans.", for: "all" }],
    alsoNeeded: [],
    diet: "veg",
    servingsMeat: 0,
    servingsVeg: 0,
};

const IDEA: QuickIdea = {
    title: "Test cheese toastie",
    how: "Toast the bread with cheese.",
    appliance: "toaster",
    minutes: 5,
    uses: [],
    alsoNeeded: null,
    diet: "veg",
    warnings: ["Check Test cheese is vegetarian."],
};

function setup(answer: (r: RecipeRequest) => RecipeSuggestion = () => ({ available: true, recipe: HALF })) {
    const suggestRecipe = vi.fn(async (r: RecipeRequest) => answer(r));
    const api = {
        stock: async () => ({ generatedAt: "", totalUnits: 1, useFirstCount: 0, products: STOCK }),
        suggestRecipe,
        savedRecipes: async () => ({ savedRecipes: [] }),
    };
    render(<RecipesScreen api={api as unknown as ApiClient} now={EVENING} />);
    return suggestRecipe;
}

const loaded = () =>
    waitFor(() => expect(screen.getByRole("button", { name: "Get a recipe" })).toBeEnabled());
const diets = () => within(screen.getByRole("radiogroup", { name: "Meat or veg?" }));

beforeEach(() => localStorage.clear());

test("the switch offers Meat & veg, Veggie and Half & half, on Meat & veg, with each one's help line", async () => {
    setup();
    await loaded();

    expect(
        diets()
            .getAllByRole("radio")
            .map((r) => r.textContent),
    ).toEqual(["Meat & veg", "Veggie", "Half & half"]);
    expect(diets().getByRole("radio", { name: "Meat & veg" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByText("Anything goes.")).toBeInTheDocument();

    fireEvent.click(diets().getByRole("radio", { name: "Veggie" }));
    expect(screen.getByText("No meat or fish, and nothing cooked with them.")).toBeInTheDocument();

    fireEvent.click(diets().getByRole("radio", { name: "Half & half" }));
    expect(
        screen.getByText("One meal: half the plates veggie, cooked alongside the meat."),
    ).toBeInTheDocument();
});

test("the request carries the diet; Another one keeps it; Start again puts it back to Meat & veg", async () => {
    const suggest = setup();
    await loaded();
    fireEvent.click(diets().getByRole("radio", { name: "Half & half" }));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Get a recipe" })));
    await act(async () => fireEvent.click(await screen.findByRole("button", { name: "Another one" })));

    expect(suggest.mock.calls[0][0].diet).toBe("half");
    expect(suggest.mock.calls[1][0].diet).toBe("half");

    fireEvent.click(screen.getByRole("button", { name: "Start again" }));
    expect(diets().getByRole("radio", { name: "Meat & veg" })).toHaveAttribute("aria-checked", "true");
});

test("Ready now has no Half & half, and switching to it from Half & half falls back to Meat & veg", async () => {
    setup();
    await loaded();
    fireEvent.click(diets().getByRole("radio", { name: "Half & half" }));

    fireEvent.click(screen.getByRole("radio", { name: /^Ready now/ }));

    expect(diets().queryByRole("radio", { name: "Half & half" })).not.toBeInTheDocument();
    expect(diets().getByRole("radio", { name: "Meat & veg" })).toHaveAttribute("aria-checked", "true");
});

test("a half & half recipe shows the split, the legend, and tags on ingredients, extras and steps", async () => {
    setup();
    await loaded();

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Get a recipe" })));

    expect(await screen.findByText("Dinner · Serves 2 (1 meat, 1 veg) · 40 min · Hob")).toBeInTheDocument();
    const legend = screen.getByLabelText("Plate tags");
    expect(legend).toHaveTextContent("Meat plates");
    expect(legend).toHaveTextContent("Veggie plates");
    expect(legend).toHaveTextContent("Untagged: everyone");

    const ingredients = within(screen.getByRole("heading", { name: "Ingredients" }).parentElement!);
    expect(ingredients.getByText("Test mince").closest("li")).toHaveTextContent("Meat plates");
    expect(ingredients.getByText("Test beans").closest("li")).toHaveTextContent("Veggie plates");
    expect(ingredients.getByText("Test tomatoes").closest("li")).not.toHaveTextContent("plates");
    expect(ingredients.getByText(/Worcestershire/).closest("span")).toHaveTextContent("Meat plates");

    const steps = within(screen.getByRole("heading", { name: "Method" }).parentElement!).getAllByRole(
        "listitem",
    );
    expect(steps.map((s) => s.textContent)).toEqual([
        "1Make the sauce and split it.",
        "2Meat platesBrown the mince.",
        "3Veggie platesStir in the beans.",
    ]);
});

test("a veggie recipe says Veggie in the meta line, with no legend or tags", async () => {
    setup(() => ({ available: true, recipe: VEG }));
    await loaded();
    fireEvent.click(diets().getByRole("radio", { name: "Veggie" }));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Get a recipe" })));

    expect(await screen.findByText("Dinner · Serves 2 · Veggie · 25 min · Hob")).toBeInTheDocument();
    expect(screen.queryByLabelText("Plate tags")).not.toBeInTheDocument();
    expect(screen.queryByText("Veggie plates")).not.toBeInTheDocument();
});

test("a Ready now idea's warnings show as Check before cooking on its card", async () => {
    setup((r) => ({ available: true, mode: "readyNow", meal: r.meal, ideas: [IDEA] }));
    await loaded();
    fireEvent.click(screen.getByRole("radio", { name: /^Ready now/ }));
    fireEvent.click(diets().getByRole("radio", { name: "Veggie" }));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Show me ideas" })));

    const note = within(await screen.findByRole("article", { name: IDEA.title })).getByRole("note");
    expect(note).toHaveTextContent("Check before cooking");
    expect(note).toHaveTextContent("Check Test cheese is vegetarian.");
});

test("Veggie Ready now with nothing vegetarian says so", async () => {
    setup(() => ({ available: false, message: "Couldn't find veggie ideas in your stock." }));
    await loaded();
    fireEvent.click(screen.getByRole("radio", { name: /^Ready now/ }));
    fireEvent.click(diets().getByRole("radio", { name: "Veggie" }));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Show me ideas" })));

    expect(await screen.findByText("Couldn't find veggie ideas in your stock.")).toBeInTheDocument();
});
