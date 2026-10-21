import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ApiClient, QuickIdea, Recipe, RecipeRequest, RecipeSuggestion, StockProduct } from "./api";
import { RecipesScreen } from "./RecipesScreen";

// Specify means of cooking (#111): the equipment chips, the meta line, "Check before cooking", and the air fryer.

const EVENING = new Date("2026-10-21T17:00:00.000Z");

const STOCK: StockProduct[] = [
    {
        productId: "penne",
        barcode: null,
        displayName: "Test penne",
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
        aisleId: "other",
        icon: null,
        useFirst: false,
    },
];

const RECIPE: Recipe = {
    title: "Test penne",
    summary: "",
    meal: "dinner",
    servings: 2,
    totalMinutes: 20,
    ingredients: [{ name: "Test penne", amount: "200 g", productId: "penne", useFirst: false }],
    steps: ["Boil the penne."],
    usesUp: [],
    alsoNeeded: [],
    couldNotFitIn: [],
    equipment: ["hob", "microwave"],
    warnings: [],
};

const AIR: QuickIdea = {
    title: "Test pitta crisps",
    how: "Air-fry the pitta.",
    appliance: "airFryer",
    minutes: 8,
    uses: [],
    alsoNeeded: null,
};

const ALL = [
    "hob",
    "ovenGrill",
    "microwave",
    "toaster",
    "airFryer",
    "slowCooker",
    "riceCooker",
    "soupMaker",
    "blender",
];

function setup(answer: (r: RecipeRequest) => RecipeSuggestion = () => ({ available: true, recipe: RECIPE })) {
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

const chips = () => within(screen.getByRole("group", { name: "What can you use?" })).getAllByRole("button");
const chip = (name: string) =>
    within(screen.getByRole("group", { name: "What can you use?" })).getByRole("button", { name });

beforeEach(() => localStorage.clear());

test("Cook something shows the nine chips as drawn, all ticked, with the count and the note", async () => {
    setup();
    await loaded();

    expect(chips().map((c) => c.textContent)).toEqual([
        "Hob",
        "Oven & grill",
        "Microwave",
        "Toaster",
        "Air fryer",
        "Slow cooker",
        "Rice cooker",
        "Soup maker",
        "Blender",
    ]);
    expect(chips().every((c) => c.getAttribute("aria-pressed") === "true")).toBe(true);
    expect(screen.getByText("9 of 9")).toBeInTheDocument();
    expect(
        screen.getByText("The kettle is always fine. Untick anything you can't use today."),
    ).toBeInTheDocument();

    fireEvent.click(chip("Oven & grill"));
    expect(chip("Oven & grill")).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByText("8 of 9")).toBeInTheDocument();
});

test("Ready now has no chips, and says it can use the air fryer", async () => {
    setup();
    await loaded();

    fireEvent.click(screen.getByRole("radio", { name: /^Ready now/ }));

    expect(screen.queryByRole("group", { name: "What can you use?" })).not.toBeInTheDocument();
    expect(screen.getByRole("radio", { name: /^Ready now/ })).toHaveTextContent(
        "Toaster, microwave, air fryer",
    );
});

test("with nothing ticked, Get a recipe is off and says to tick at least one", async () => {
    setup();
    await loaded();

    for (const c of chips()) fireEvent.click(c);

    expect(screen.getByText("0 of 9")).toBeInTheDocument();
    expect(screen.getByText("Tick at least one")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Get a recipe" })).toBeDisabled();
});

test("the request carries the ticked kit; Another one keeps it; Start again ticks everything again", async () => {
    const suggest = setup();
    await loaded();
    fireEvent.click(chip("Oven & grill"));
    fireEvent.click(chip("Blender"));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Get a recipe" })));
    await act(async () => fireEvent.click(await screen.findByRole("button", { name: "Another one" })));

    const without = ALL.filter((k) => k !== "ovenGrill" && k !== "blender");
    expect(suggest.mock.calls[0][0].equipment).toEqual(without);
    expect(suggest.mock.calls[1][0].equipment).toEqual(without);

    fireEvent.click(screen.getByRole("button", { name: "Start again" }));
    expect(chips().every((c) => c.getAttribute("aria-pressed") === "true")).toBe(true);
});

test("the recipe's meta line ends with its kit, and a warning shows as Check before cooking", async () => {
    setup(() => ({
        available: true,
        recipe: {
            ...RECIPE,
            warnings: ["This recipe mentions the oven or grill, which you said you can't use."],
        },
    }));
    await loaded();

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Get a recipe" })));

    expect(await screen.findByText("Dinner · Serves 2 · 20 min · Hob · Microwave")).toBeInTheDocument();
    const note = screen.getByRole("note");
    expect(note).toHaveTextContent("Check before cooking");
    expect(note).toHaveTextContent("This recipe mentions the oven or grill, which you said you can't use.");
});

test("Ready now ideas show the Air fryer tag, and the header names it", async () => {
    setup((r) => ({ available: true, mode: "readyNow", meal: r.meal, ideas: [AIR] }));
    await loaded();
    fireEvent.click(screen.getByRole("radio", { name: /^Ready now/ }));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Show me ideas" })));

    expect(
        within(await screen.findByRole("article", { name: AIR.title })).getByText("Air fryer"),
    ).toBeInTheDocument();
    expect(screen.getByText(/Toaster, microwave, kettle, air fryer or no cooking/)).toBeInTheDocument();
});
