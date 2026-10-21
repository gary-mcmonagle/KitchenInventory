import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type {
    AdjustedRecipe,
    ApiClient,
    Recipe,
    RecipeAddition,
    RecipeAdjustment,
    StockProduct,
} from "./api";
import { adjustmentLine, SUGGESTIONS_TIMEOUT_MS } from "./RecipeAdjust";
import { RecipesScreen } from "./RecipesScreen";

// Adjust a recipe (#104, project/RecipeAdjust.dc.html).

const EVENING = new Date("2026-10-21T17:00:00.000Z");

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
    product("chicken", "Test chicken"),
    product("chorizo", "Test chorizo ring", { genericName: "Chorizo" }),
    product("spinach", "Test spinach", { useFirst: true, section: "useFirst" }),
    product("penne", "Test penne"),
];

const RECIPE: Recipe = {
    title: "Creamy chicken penne",
    summary: "A quick one-pan pasta.",
    meal: "dinner",
    servings: 2,
    totalMinutes: 25,
    ingredients: [
        { name: "Test chicken", amount: "2", productId: "chicken", useFirst: false },
        { name: "Test penne", amount: "200 g", productId: "penne", useFirst: false },
        { name: "Onion", amount: "1", productId: null, useFirst: false },
    ],
    steps: ["Cook the penne.", "Fry the chicken until cooked through."],
    usesUp: [],
    alsoNeeded: ["1 garlic clove"],
    couldNotFitIn: [],
};

const SPICY: Recipe = {
    ...RECIPE,
    title: "Spicy chicken and chorizo penne",
    ingredients: [
        { name: "Test chicken", amount: "2", productId: "chicken", useFirst: false },
        { name: "Chorizo", amount: "70 g", productId: "chorizo", useFirst: false },
        { name: "Test penne", amount: "200 g", productId: "penne", useFirst: false },
    ],
};

const SUGGESTIONS: RecipeAddition[] = [
    { name: "Chorizo", productIds: ["chorizo"], useFirst: false, confidence: 0.4 },
    { name: "Spinach", productIds: ["spinach"], useFirst: true, confidence: 0.3 },
];

function setup(
    options: {
        additions?: () => Promise<{ suggestions: RecipeAddition[] }>;
        adjust?: (a: RecipeAdjustment) => Promise<AdjustedRecipe>;
    } = {},
) {
    const api = {
        stock: async () => ({ generatedAt: "", totalUnits: STOCK.length, useFirstCount: 1, products: STOCK }),
        suggestRecipe: vi.fn(async () => ({ available: true, recipe: RECIPE })),
        savedRecipes: async () => ({ savedRecipes: [] }),
        suggestRecipeAdditions: vi.fn(options.additions ?? (async () => ({ suggestions: SUGGESTIONS }))),
        adjustRecipe: vi.fn(
            options.adjust ??
                (async () => ({
                    available: true,
                    recipe: SPICY,
                    adjustmentSummary: { added: ["Chorizo"], removed: ["Onion"], tweak: "spicier" },
                })),
        ),
    };
    render(<RecipesScreen api={api as unknown as ApiClient} now={EVENING} />);
    return api;
}

async function toRecipe() {
    await waitFor(() => expect(screen.getByRole("button", { name: "Get a recipe" })).toBeEnabled());
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Get a recipe" })));
    await screen.findByRole("heading", { name: "Creamy chicken penne" });
}

const sheet = () => screen.getByRole("dialog", { name: "Adjust this recipe" });
const update = () => within(sheet()).getByRole("button", { name: "Update recipe" });

test("Adjust opens the sheet; suggestions load as it opens and show as chips, use-first marked", async () => {
    let answer!: (v: { suggestions: RecipeAddition[] }) => void;
    const api = setup({ additions: () => new Promise((r) => (answer = r)) });
    await toRecipe();

    fireEvent.click(screen.getByRole("button", { name: "Adjust" }));

    expect(within(sheet()).getByText("Finding good additions…")).toBeInTheDocument();
    await waitFor(() => expect(api.suggestRecipeAdditions).toHaveBeenCalledWith(RECIPE));
    expect(update()).toHaveAttribute("aria-disabled", "true");
    await act(async () => answer({ suggestions: SUGGESTIONS }));
    expect(within(sheet()).getByRole("button", { name: "Add Chorizo" })).toBeInTheDocument();
    expect(within(sheet()).getByRole("button", { name: "Add Spinach (use first)" })).toBeInTheDocument();
    expect(
        within(sheet())
            .getAllByRole("checkbox")
            .map((c) => c.closest("label")!.textContent),
    ).toEqual(["Test chicken", "Test penne", "Onion"]);

    fireEvent.click(within(sheet()).getByRole("button", { name: "Add Chorizo" }));
    expect(within(sheet()).getByRole("button", { name: "Remove Chorizo" })).toBeInTheDocument();
    expect(within(sheet()).queryByRole("button", { name: "Add Chorizo" })).not.toBeInTheDocument();
    expect(update()).toHaveAttribute("aria-disabled", "false");
});

test.each([
    ["none", async () => ({ suggestions: [] })],
    [
        "an error",
        async () => {
            throw new Error("down");
        },
    ],
])("suggestions hide quietly on %s, and everything else still works", async (_, additions) => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    setup({ additions });
    await toRecipe();
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Adjust" })));

    expect(within(sheet()).queryByText("Suggested from your cupboard")).not.toBeInTheDocument();
    fireEvent.change(within(sheet()).getByRole("textbox", { name: "Tweak" }), {
        target: { value: "spicier" },
    });
    expect(update()).toHaveAttribute("aria-disabled", "false");
});

test("suggestions that take over 8 seconds are dropped", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
        setup({ additions: () => new Promise(() => {}) });
        await toRecipe();
        fireEvent.click(screen.getByRole("button", { name: "Adjust" }));
        expect(within(sheet()).getByText("Finding good additions…")).toBeInTheDocument();

        await act(async () => vi.advanceTimersByTime(SUGGESTIONS_TIMEOUT_MS + 1));

        expect(within(sheet()).queryByText("Finding good additions…")).not.toBeInTheDocument();
    } finally {
        vi.useRealTimers();
    }
});

test("add from the search and typed, remove, tweak: sends them, dims while updating, then shows the update", async () => {
    let finish!: (v: AdjustedRecipe) => void;
    const api = setup({ adjust: () => new Promise((r) => (finish = r)) });
    await toRecipe();
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Adjust" })));

    fireEvent.change(within(sheet()).getByRole("searchbox", { name: "Add something from your cupboard" }), {
        target: { value: "chor" },
    });
    fireEvent.click(within(sheet()).getByRole("button", { name: "Chorizo" })); // its generic name
    fireEvent.click(within(sheet()).getByRole("button", { name: "Or type something you'd buy" }));
    fireEvent.change(within(sheet()).getByRole("textbox", { name: "Something you'd buy" }), {
        target: { value: "chilli flakes" },
    });
    fireEvent.click(within(sheet()).getByRole("button", { name: "Add" }));
    fireEvent.click(within(sheet()).getByRole("checkbox", { name: "Onion" }));
    fireEvent.change(within(sheet()).getByRole("textbox", { name: "Tweak" }), {
        target: { value: " spicier " },
    });
    expect(within(sheet()).getByRole("textbox", { name: "Tweak" })).toHaveAttribute("maxLength", "300");

    fireEvent.click(update());

    expect(api.adjustRecipe).toHaveBeenCalledWith({
        recipe: RECIPE,
        add: { productIds: ["chorizo"], typed: ["chilli flakes"] },
        remove: ["Onion"],
        tweak: "spicier",
    });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Updating your recipe…");
    expect(screen.getByRole("heading", { name: "Creamy chicken penne" })).toBeInTheDocument(); // still there, dimmed

    await act(async () =>
        finish({
            available: true,
            recipe: SPICY,
            adjustmentSummary: { added: ["Chorizo", "chilli flakes"], removed: ["Onion"], tweak: "spicier" },
        }),
    );

    expect(screen.getByRole("heading", { name: "Spicy chicken and chorizo penne" })).toBeInTheDocument();
    expect(
        screen.getByText('Adjusted: + Chorizo · + chilli flakes · − Onion · "spicier"'),
    ).toBeInTheDocument();
    const chorizoRow = screen.getByRole("link", { name: "Chorizo" }).closest("li")!;
    expect(within(chorizoRow).getByText("Added")).toBeInTheDocument();
});

test("Undo adjustment steps back one version each time; adjusting again works on the latest", async () => {
    const api = setup();
    await toRecipe();

    for (let i = 0; i < 2; i++) {
        await act(async () => fireEvent.click(screen.getByRole("button", { name: "Adjust" })));
        fireEvent.change(within(sheet()).getByRole("textbox", { name: "Tweak" }), {
            target: { value: "spicier" },
        });
        await act(async () => fireEvent.click(update()));
    }

    expect((api.adjustRecipe.mock.calls[1] as unknown as [RecipeAdjustment])[0].recipe).toEqual(SPICY); // the second works on the latest
    fireEvent.click(screen.getByRole("button", { name: "Undo adjustment" }));
    expect(screen.getByRole("heading", { name: "Spicy chicken and chorizo penne" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Undo adjustment" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Undo adjustment" }));
    expect(screen.getByRole("heading", { name: "Creamy chicken penne" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Undo adjustment" })).not.toBeInTheDocument();
});

test("an error keeps the recipe as it was, with the message and Try again", async () => {
    const api = setup({
        adjust: async () => ({
            available: false,
            message: "Couldn't update the recipe just now. Try again.",
        }),
    });
    await toRecipe();
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Adjust" })));
    fireEvent.change(within(sheet()).getByRole("textbox", { name: "Tweak" }), {
        target: { value: "spicier" },
    });
    await act(async () => fireEvent.click(update()));

    expect(screen.getByRole("heading", { name: "Creamy chicken penne" })).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent("Couldn't update the recipe just now. Try again.");

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Try again" })));
    expect(api.adjustRecipe).toHaveBeenCalledTimes(2);
    expect((api.adjustRecipe.mock.calls[1] as unknown as [RecipeAdjustment])[0].tweak).toBe("spicier");
});

test("Another one after adjusting is a fresh idea: no summary, nothing to undo", async () => {
    const api = setup();
    await toRecipe();
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Adjust" })));
    fireEvent.change(within(sheet()).getByRole("textbox", { name: "Tweak" }), {
        target: { value: "spicier" },
    });
    await act(async () => fireEvent.click(update()));
    expect(screen.getByRole("button", { name: "Undo adjustment" })).toBeInTheDocument();

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Another one" })));

    expect(api.suggestRecipe).toHaveBeenCalledTimes(2);
    expect(screen.queryByRole("button", { name: "Undo adjustment" })).not.toBeInTheDocument();
    expect(screen.queryByText(/^Adjusted:/)).not.toBeInTheDocument();
});

test("what the model couldn't do is said under the ingredients", async () => {
    setup({
        adjust: async () => ({
            available: true,
            recipe: { ...SPICY, couldNotAdd: ["chilli flakes"], couldNotRemove: ["Onion"] },
            adjustmentSummary: { added: ["chilli flakes"], removed: ["Onion"], tweak: null },
        }),
    });
    await toRecipe();
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Adjust" })));
    fireEvent.click(within(sheet()).getByRole("checkbox", { name: "Onion" }));
    await act(async () => fireEvent.click(update()));

    expect(screen.getByText("Couldn't add: chilli flakes")).toBeInTheDocument();
    expect(screen.getByText("Couldn't remove: Onion")).toBeInTheDocument();
});

test("Cancel closes the sheet without changing anything", async () => {
    const api = setup();
    await toRecipe();
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Adjust" })));
    fireEvent.click(within(sheet()).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(api.adjustRecipe).not.toHaveBeenCalled();
});

test("the summary line", () => {
    expect(adjustmentLine({ added: ["Chorizo"], removed: ["Onion"], tweak: "spicier" })).toBe(
        'Adjusted: + Chorizo · − Onion · "spicier"',
    );
    expect(adjustmentLine({ added: [], removed: [], tweak: "for 4" })).toBe('Adjusted: "for 4"');
});
