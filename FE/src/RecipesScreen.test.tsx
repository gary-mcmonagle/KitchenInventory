import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import {
    ApiError,
    type ApiClient,
    type Recipe,
    type RecipeRequest,
    type RecipeSuggestion,
    type StockProduct,
} from "./api";
import { defaultMeal, RecipesScreen } from "./RecipesScreen";
import { Shell } from "./Shell";

// 17:00 UTC = 18:00 BST: dinner time.
const EVENING = new Date("2026-10-18T17:00:00.000Z");

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

const due = (id: string, name: string, days: number, date: string) =>
    product(id, name, {
        useFirst: true,
        section: "useFirst",
        daysUntilSoonest: days,
        soonestEstimatedExpiryDate: date,
    });

const STOCK = [
    due("hummus", "Test hummus", -1, "2026-10-17"),
    due("milk", "Test milk", 2, "2026-10-20"),
    product("penne", "Test penne"),
    product("cheddar", "Test cheddar"),
    product("unknown", "Unknown item (5012345678900)", { status: "noMatch", section: "waitingForName" }),
];

const RECIPE: Recipe = {
    title: "Test pasta bake",
    summary: "A quick test pasta.",
    meal: "dinner",
    servings: 2,
    totalMinutes: 25,
    ingredients: [
        { name: "Test milk", amount: "300 ml", productId: "milk", useFirst: true },
        { name: "Test penne", amount: "200 g", productId: "penne", useFirst: false },
        { name: "Oil, salt and pepper", amount: "1 tbsp", productId: null, useFirst: false },
    ],
    steps: [
        { text: "Cook the penne.", for: "all" },
        { text: "Warm the milk.", for: "all" },
    ],
    usesUp: [
        {
            productId: "milk",
            displayName: "Test milk",
            daysUntilSoonest: 2,
            soonestEstimatedExpiryDate: "2026-10-20",
        },
    ],
    alsoNeeded: [
        { name: "1 onion", for: "all" },
        { name: "1 garlic clove", for: "all" },
    ],
    couldNotFitIn: [],
};

function fakeApi(
    stock: StockProduct[] = STOCK,
    answer: (r: RecipeRequest) => Promise<RecipeSuggestion> = async () => ({
        available: true,
        recipe: RECIPE,
    }),
) {
    const suggestRecipe = vi.fn(answer);
    const api = {
        stock: async () => ({ generatedAt: "", totalUnits: stock.length, useFirstCount: 0, products: stock }),
        suggestRecipe,
        savedRecipes: async () => ({ savedRecipes: [] }),
    } as unknown as ApiClient;
    return { api, suggestRecipe };
}

async function renderForm(
    stock: StockProduct[] = STOCK,
    answer?: (r: RecipeRequest) => Promise<RecipeSuggestion>,
) {
    const fake = fakeApi(stock, answer);
    render(<RecipesScreen api={fake.api} now={EVENING} />);
    await screen.findByRole("combobox", { name: "Include ingredients" });
    // Wait for the stock to arrive (the button is enabled once it has).
    await waitFor(() => expect(screen.getByRole("button", { name: "Get a recipe" })).toBeEnabled());
    return fake;
}

test.each([
    ["2026-10-18T05:00:00.000Z", "breakfast"],
    ["2026-10-18T09:59:00.000Z", "breakfast"],
    ["2026-10-18T10:00:00.000Z", "lunch"],
    ["2026-10-18T13:29:00.000Z", "lunch"], // 14:29 BST
    ["2026-10-18T13:30:00.000Z", "snack"], // 14:30 BST (#88)
    ["2026-10-18T15:59:00.000Z", "snack"], // 16:59 BST
    ["2026-10-18T16:00:00.000Z", "dinner"], // 17:00 BST
    ["2026-12-01T14:30:00.000Z", "snack"], // 14:30 GMT
    ["2026-10-18T22:30:00.000Z", "dinner"],
])("the default meal follows the UK time of day (%s → %s)", (iso, meal) => {
    expect(defaultMeal(new Date(iso))).toBe(meal);
});

test("the form matches the mockup: heading, meal chips as a radio group, the default meal", async () => {
    await renderForm();

    expect(screen.getByRole("heading", { level: 1, name: "Hungry?" })).toBeInTheDocument();
    expect(screen.getByText("Ideas built from what's in your cupboard.")).toBeInTheDocument();
    const group = screen.getByRole("radiogroup", { name: "Meal" });
    const radios = within(group).getAllByRole("radio");
    expect(radios.map((r) => r.textContent)).toEqual(["Breakfast", "Lunch", "Dinner", "Snack"]);
    expect(within(group).getByRole("radio", { name: "Dinner" })).toHaveAttribute("aria-checked", "true");

    fireEvent.click(within(group).getByRole("radio", { name: "Lunch" }));
    expect(within(group).getByRole("radio", { name: "Lunch" })).toHaveAttribute("aria-checked", "true");
    expect(within(group).getByRole("radio", { name: "Dinner" })).toHaveAttribute("aria-checked", "false");
});

test("prefer use first is ticked when anything is due, with a pill per use-first item", async () => {
    await renderForm();

    expect(screen.getByRole("checkbox", { name: "Prefer ingredients that need using" })).toBeChecked();
    expect(screen.getByText("Builds the recipe around the 2 things to use first.")).toBeInTheDocument();
    expect(screen.getByText("Test hummus · Est. expired").className).toMatch(/_expired_/);
    expect(screen.getByText("Test milk · Est. 2 days").className).toMatch(/_soon_/);
});

test("with nothing due, prefer use first is unticked and disabled, with no pills", async () => {
    await renderForm([product("penne", "Test penne")]);

    const box = screen.getByRole("checkbox", { name: "Prefer ingredients that need using" });
    expect(box).not.toBeChecked();
    expect(box).toBeDisabled();
    expect(screen.getByText("Nothing needs using up right now")).toBeInTheDocument();
    expect(screen.queryByText(/· Est\./)).not.toBeInTheDocument();
});

test("include ingredients: opens on focus, searches, picks with checkboxes and chips, removes", async () => {
    await renderForm();
    const search = screen.getByRole("combobox", { name: "Include ingredients" });
    expect(search).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();

    fireEvent.focus(search);
    const list = screen.getByRole("listbox");
    expect(search).toHaveAttribute("aria-expanded", "true");
    // Known products only, by name; use-first ones tagged.
    expect(
        within(list)
            .getAllByRole("option")
            .map((o) => o.textContent),
    ).toEqual(["Test cheddar", "Test hummusUse first", "Test milkUse first", "Test penne"]);

    fireEvent.change(search, { target: { value: "PEN" } });
    expect(
        within(screen.getByRole("listbox"))
            .getAllByRole("option")
            .map((o) => o.textContent),
    ).toEqual(["Test penne"]);
    fireEvent.click(within(screen.getByRole("listbox")).getByRole("button", { name: "Test penne" }));
    expect(screen.getByRole("option", { name: "Test penne" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("button", { name: "Remove Test penne" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Remove Test penne" }));
    expect(screen.queryByRole("button", { name: "Remove Test penne" })).not.toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Test penne" })).toHaveAttribute("aria-selected", "false");
});

test("the ingredient list closes on Escape and on a tap outside", async () => {
    await renderForm();
    const search = screen.getByRole("combobox", { name: "Include ingredients" });

    fireEvent.focus(search);
    fireEvent.keyDown(search, { key: "Escape" });
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();

    fireEvent.focus(search);
    expect(screen.getByRole("listbox")).toBeInTheDocument();
    fireEvent.pointerDown(screen.getByRole("heading", { level: 1 }));
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
});

test("comments: limited to 300 characters, with a count past 250", async () => {
    await renderForm();
    const comments = screen.getByRole("textbox", { name: "Additional comments" });
    expect(comments).toHaveAttribute("maxlength", "300");
    expect(comments).toHaveAttribute("placeholder", "e.g. something spicy, no oven, ready in 20 minutes");

    fireEvent.change(comments, { target: { value: "a".repeat(250) } });
    expect(screen.queryByText(/\/ 300/)).not.toBeInTheDocument();
    fireEvent.change(comments, { target: { value: "a".repeat(251) } });
    expect(screen.getByText("251 / 300")).toBeInTheDocument();
});

test("Get a recipe sends the choices, shows the loading state, then the recipe", async () => {
    let finish: (s: RecipeSuggestion) => void = () => {};
    const { suggestRecipe } = await renderForm(STOCK, () => new Promise((resolve) => (finish = resolve)));

    fireEvent.focus(screen.getByRole("combobox", { name: "Include ingredients" }));
    fireEvent.click(screen.getByRole("button", { name: "Test cheddar" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Additional comments" }), {
        target: { value: "  no oven  " },
    });
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));

    expect(suggestRecipe).toHaveBeenCalledWith({
        mode: "cook",
        meal: "dinner",
        preferUseFirst: true,
        includeProductIds: ["cheddar"],
        comments: "no oven",
        avoidTitles: [],
        diet: "any",
        equipment: [
            "hob",
            "ovenGrill",
            "microwave",
            "toaster",
            "airFryer",
            "slowCooker",
            "riceCooker",
            "soupMaker",
            "blender",
        ],
    });
    expect(screen.getByRole("status")).toHaveTextContent("Thinking up something with what you've got…");

    await act(async () => finish({ available: true, recipe: RECIPE }));

    expect(screen.getByRole("heading", { level: 1, name: "Test pasta bake" })).toBeInTheDocument();
});

test("the recipe view: meta, uses up, ingredients with links and dots, also needed, method", async () => {
    await renderForm();
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));

    expect(await screen.findByText("Dinner · Serves 2 · 25 min")).toBeInTheDocument();
    expect(screen.getByText("A quick test pasta.")).toBeInTheDocument();
    const usesUp = screen.getByRole("region", { name: "Uses up" });
    expect(within(usesUp).getByText("Test milk · Est. 2 days").className).toMatch(/_soon_/);

    const milk = screen.getByRole("link", { name: /Test milk/ });
    expect(milk).toHaveAttribute("href", "/products/milk");
    expect(milk.parentElement!.querySelector("[class*=_dotOn_]")).not.toBeNull();
    const penne = screen.getByRole("link", { name: "Test penne" });
    expect(penne.parentElement!.querySelector("[class*=_dotOn_]")).toBeNull();
    expect(screen.getByText("Oil, salt and pepper").tagName).toBe("SPAN");
    expect(screen.getByText("You'll also need")).toBeInTheDocument();
    expect(screen.getByText((_, el) => el?.textContent === "1 onion · 1 garlic clove")).toBeInTheDocument();
    expect(screen.queryByText(/Couldn't fit in/)).not.toBeInTheDocument();

    const steps = screen.getByRole("heading", { name: "Method" }).parentElement!.querySelectorAll("li");
    expect([...steps].map((s) => s.textContent)).toEqual(["1Cook the penne.", "2Warm the milk."]);

    fireEvent.click(milk);
    expect(window.location.pathname).toBe("/products/milk");
});

test("the uses-up card is hidden when empty, and a couldn't-fit-in note shows", async () => {
    await renderForm(STOCK, async () => ({
        available: true,
        recipe: { ...RECIPE, usesUp: [], couldNotFitIn: ["Test cheddar"] },
    }));
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));

    expect(await screen.findByText("Couldn't fit in: Test cheddar")).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Uses up" })).not.toBeInTheDocument();
});

test("Another one sends the titles shown so far; Start again keeps every choice", async () => {
    let n = 0;
    const { suggestRecipe } = await renderForm(STOCK, async () => ({
        available: true,
        recipe: { ...RECIPE, title: `Recipe ${++n}` },
    }));
    fireEvent.click(screen.getByRole("radio", { name: "Lunch" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Additional comments" }), {
        target: { value: "spicy" },
    });
    fireEvent.focus(screen.getByRole("combobox", { name: "Include ingredients" }));
    fireEvent.click(screen.getByRole("button", { name: "Test penne" }));
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));
    await screen.findByRole("heading", { name: "Recipe 1" });

    fireEvent.click(screen.getByRole("button", { name: "Another one" }));
    await screen.findByRole("heading", { name: "Recipe 2" });
    fireEvent.click(screen.getByRole("button", { name: "Another one" }));
    await screen.findByRole("heading", { name: "Recipe 3" });

    expect(suggestRecipe.mock.calls.map(([r]) => r.avoidTitles)).toEqual([
        [],
        ["Recipe 1"],
        ["Recipe 1", "Recipe 2"],
    ]);
    expect(suggestRecipe.mock.calls.every(([r]) => r.comments === "spicy" && r.meal === "lunch")).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: "Start again" }));
    expect(screen.getByRole("radio", { name: "Lunch" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("textbox", { name: "Additional comments" })).toHaveValue("spicy");
    expect(screen.getByRole("button", { name: "Remove Test penne" })).toBeInTheDocument();
});

test("an unavailable model shows the friendly message with Try again, and the form keeps its choices", async () => {
    let calls = 0;
    const { suggestRecipe } = await renderForm(STOCK, async () =>
        ++calls === 1
            ? { available: false, message: "Couldn't come up with a recipe just now. Try again." }
            : { available: true, recipe: RECIPE },
    );
    fireEvent.change(screen.getByRole("textbox", { name: "Additional comments" }), {
        target: { value: "quick" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Couldn't come up with a recipe just now. Try again.");
    expect(screen.getByRole("textbox", { name: "Additional comments" })).toHaveValue("quick");

    fireEvent.click(within(alert).getByRole("button", { name: "Try again" }));
    expect(await screen.findByRole("heading", { level: 1, name: "Test pasta bake" })).toBeInTheDocument();
    expect(suggestRecipe.mock.calls[1][0]).toEqual(suggestRecipe.mock.calls[0][0]);
});

test("the hourly limit's message is shown as it comes", async () => {
    await renderForm(STOCK, async () => {
        throw new ApiError(429, "x", {
            error: "That's a lot of recipes for one hour. Try again a bit later.",
        });
    });
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("That's a lot of recipes for one hour.");
});

test("with nothing known in stock, there's a note and no button to press", async () => {
    const { api } = fakeApi([product("unknown", "Unknown item", { status: "noMatch" })]);
    render(<RecipesScreen api={api} now={EVENING} />);

    expect(await screen.findByText("There's nothing in stock to cook with yet.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Get a recipe" })).toBeDisabled();
});

test("the shell shows the Hungry? screen at /recipes, with the nav", async () => {
    window.history.pushState(null, "", "/recipes");
    const { api } = fakeApi();
    const shellApi = {
        ...api,
        me: async () => ({ name: "Cupboard test", objectId: "o" }),
        ping: async () => ({ status: "ok", commit: "abc1234" }),
    } as unknown as ApiClient;
    render(<Shell api={shellApi} buildSha="def5678" />);

    expect(await screen.findByRole("heading", { level: 1, name: "Hungry?" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Hungry?" })).toHaveAttribute("aria-current", "page");
    window.history.pushState(null, "", "/");
});
