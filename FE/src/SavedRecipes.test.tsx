import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ApiClient, QuickIdea, Recipe, RecipeSuggestion, SavedRecipe, StockProduct } from "./api";
import { RecipesScreen } from "./RecipesScreen";
import { savedToRecipe, savedWhen, statusLine } from "./SavedRecipes";
import type { ShoppingList } from "./useShoppingList";

// Saved recipes and Ready now ideas (#105): project/SavedPicker.dc.html and project/SavedRecipe.dc.html.

const list = {
    open: [] as Array<{ name: string }>,
    add: vi.fn(),
};
vi.mock("./useShoppingList", async (importOriginal) => ({
    ...(await importOriginal<typeof import("./useShoppingList")>()),
    useOptionalShoppingList: () => list as unknown as ShoppingList,
}));

// 17:00 UTC = 18:00 BST on 21 Oct.
const NOW = new Date("2026-10-21T17:00:00.000Z");

const STOCK: StockProduct[] = [
    {
        productId: "penne",
        barcode: null,
        displayName: "Test penne",
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
    },
];

const RECIPE: Recipe = {
    title: "Test pasta bake",
    summary: "A quick test pasta.",
    meal: "dinner",
    servings: 2,
    totalMinutes: 25,
    ingredients: [
        { name: "Test penne", amount: "200 g", productId: "penne", useFirst: false },
        { name: "Salt", amount: "a pinch", productId: null, useFirst: false },
    ],
    steps: [{ text: "Cook the penne.", for: "all" }],
    usesUp: [],
    alsoNeeded: [{ name: "1 onion", for: "all" }],
    couldNotFitIn: [],
};

const IDEA: QuickIdea = {
    title: "Test beans on toast",
    how: "Microwave the beans; toast the bread.",
    appliance: "microwave",
    minutes: 5,
    uses: [
        {
            productId: "beans",
            displayName: "Test beans",
            useFirst: false,
            daysUntilSoonest: null,
            soonestEstimatedExpiryDate: null,
        },
    ],
    alsoNeeded: null,
};

const saved = (overrides: Partial<SavedRecipe>): SavedRecipe => ({
    id: "s1",
    kind: "recipe",
    title: "Test traybake",
    originalTitle: "Test traybake",
    meal: "dinner",
    totalMinutes: 40,
    servings: 2,
    ingredients: [
        { name: "Peppers", amount: "2", productId: "peppers", genericName: "Peppers", status: "have" },
        { name: "Halloumi", amount: "225 g", productId: null, genericName: null, status: "missing" },
        {
            name: "Paprika",
            amount: "1 tsp",
            productId: "paprika",
            genericName: "Paprika",
            status: "useFirst",
        },
    ],
    steps: [
        { text: "Roast the peppers.", for: "all" },
        { text: "Add the halloumi.", for: "all" },
    ],
    alsoNeeded: [],
    how: null,
    appliance: null,
    note: "Halve the chilli",
    savedByName: "Sam",
    savedAt: "2026-10-12T18:00:00.000Z",
    updatedAt: "2026-10-12T18:00:00.000Z",
    missing: ["Halloumi"],
    usesUp: ["Paprika"],
    ...overrides,
});

const SAVED: SavedRecipe[] = [
    saved({
        id: "s-new",
        title: "Test penne bake",
        meal: "dinner",
        totalMinutes: 25,
        ingredients: [],
        missing: [],
        usesUp: ["Milk"],
        savedAt: "2026-10-21T08:00:00.000Z",
    }),
    saved({
        id: "s-beans",
        kind: "readyNow",
        title: "Test beans on toast",
        meal: "snack",
        totalMinutes: 5,
        servings: null,
        ingredients: [],
        missing: [],
        usesUp: [],
        how: "Microwave the beans.",
        appliance: "microwave",
        savedAt: "2026-10-20T08:00:00.000Z",
    }),
    saved({ id: "s1" }),
    saved({
        id: "s-oats",
        title: "Test overnight oats",
        meal: "breakfast",
        totalMinutes: 10,
        missing: [],
        usesUp: [],
    }),
];

function fakeApi(
    savedRecipes: SavedRecipe[] = SAVED,
    answer: () => Promise<RecipeSuggestion> = async () => ({ available: true, recipe: RECIPE }),
) {
    const api = {
        stock: async () => ({ generatedAt: "", totalUnits: 1, useFirstCount: 0, products: STOCK }),
        suggestRecipe: vi.fn(answer),
        suggestRecipeAdditions: vi.fn(async () => ({ suggestions: [] })),
        savedRecipes: vi.fn(async () => ({ savedRecipes })),
        saveRecipe: vi.fn(async (id: string, body: { title: string }) =>
            saved({ id, title: body.title, savedAt: NOW.toISOString(), savedByName: "Cupboard test" }),
        ),
        updateSavedRecipe: vi.fn(async (id: string, change: { title?: string; note?: string }) => ({
            ...savedRecipes.find((r) => r.id === id)!,
            ...change,
        })),
        deleteSavedRecipe: vi.fn(async () => undefined),
    };
    return api;
}

async function show(api = fakeApi()) {
    render(<RecipesScreen api={api as unknown as ApiClient} now={NOW} />);
    await waitFor(() => expect(screen.getByText(`${SAVED.length} saved`)).toBeInTheDocument());
    return api;
}

const savedBox = () => screen.getByRole("combobox", { name: "Saved" });
const rows = () =>
    within(screen.getByRole("listbox", { name: "Saved recipes" }))
        .queryAllByRole("option")
        .map((o) => within(o).getByRole("button").textContent);

beforeEach(() => {
    localStorage.clear();
    list.open = [];
    list.add.mockClear();
});

test("savedWhen says today, yesterday, or the date", () => {
    expect(savedWhen("2026-10-21T08:00:00.000Z", NOW)).toBe("Saved today");
    expect(savedWhen("2026-10-20T08:00:00.000Z", NOW)).toBe("Saved yesterday");
    expect(savedWhen("2026-10-12T08:00:00.000Z", NOW)).toBe("Saved 12 Oct");
});

test("the status line: everything (with uses up) or what's missing", () => {
    expect(statusLine(saved({ missing: [], usesUp: [] }))).toEqual({
        text: "You've got everything",
        missing: false,
    });
    expect(statusLine(saved({ missing: [], usesUp: ["Milk", "Eggs"] })).text).toBe(
        "You've got everything · Uses up Milk, Eggs",
    );
    expect(statusLine(saved({ missing: ["Halloumi", "Tuna"] }))).toEqual({
        text: "Missing: Halloumi, Tuna",
        missing: true,
    });
});

test("the Saved box is closed by default, above the form, and opens on focus; the form dims", async () => {
    await show();

    expect(savedBox()).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
    expect(
        savedBox().compareDocumentPosition(screen.getByRole("radiogroup", { name: "What kind of food" })),
    ).toBe(Node.DOCUMENT_POSITION_FOLLOWING);

    fireEvent.focus(savedBox());

    expect(savedBox()).toHaveAttribute("aria-expanded", "true");
    expect(rows()).toEqual([
        "Test penne bakeDinner · 25 min · Saved todayYou've got everything · Uses up Milk",
        "Test beans on toastReady nowSnack · 5 min · Saved yesterdayYou've got everything",
        "Test traybakeDinner · 40 min · Saved 12 OctMissing: Halloumi",
        "Test overnight oatsBreakfast · 10 min · Saved 12 OctYou've got everything",
    ]);
    expect(
        screen.getByRole("radiogroup", { name: "What kind of food" }).closest("[class*=dimmed]"),
    ).not.toBeNull();
});

test("the chevron opens and closes it; Escape and a tap outside close it", async () => {
    await show();

    fireEvent.click(screen.getByRole("button", { name: "Show saved recipes" }));
    expect(savedBox()).toHaveAttribute("aria-expanded", "true");
    fireEvent.click(screen.getByRole("button", { name: "Hide saved recipes" }));
    expect(savedBox()).toHaveAttribute("aria-expanded", "false");

    fireEvent.focus(savedBox());
    fireEvent.keyDown(savedBox(), { key: "Escape" });
    expect(savedBox()).toHaveAttribute("aria-expanded", "false");

    fireEvent.click(screen.getByRole("button", { name: "Show saved recipes" }));
    fireEvent.pointerDown(document.body);
    expect(savedBox()).toHaveAttribute("aria-expanded", "false");
    expect(
        screen.getByRole("radiogroup", { name: "What kind of food" }).closest("[class*=dimmed]"),
    ).toBeNull();
});

test("meal, time and search filter together, with a note when nothing matches", async () => {
    await show();
    fireEvent.focus(savedBox());
    const meal = screen.getByRole("radiogroup", { name: "Filter by meal" });
    const time = screen.getByRole("radiogroup", { name: "Filter by cook time" });
    expect(
        within(meal)
            .getAllByRole("radio")
            .map((r) => r.textContent),
    ).toEqual(["All", "Breakfast", "Lunch", "Dinner", "Snack"]);
    expect(
        within(time)
            .getAllByRole("radio")
            .map((r) => r.textContent),
    ).toEqual(["Any time", "15 min or less", "30 min or less", "1 hour or less"]);

    fireEvent.click(within(meal).getByRole("radio", { name: "Dinner" }));
    expect(rows().map((r) => r!.split("Dinner")[0])).toEqual(["Test penne bake", "Test traybake"]);

    fireEvent.click(within(time).getByRole("radio", { name: "30 min or less" }));
    expect(within(time).getByRole("radio", { name: "30 min or less" })).toHaveAttribute(
        "aria-checked",
        "true",
    );
    expect(rows()).toHaveLength(1);

    fireEvent.click(within(meal).getByRole("radio", { name: "All" }));
    fireEvent.change(savedBox(), { target: { value: "OATS" } });
    expect(rows()).toEqual(["Test overnight oatsBreakfast · 10 min · Saved 12 OctYou've got everything"]);

    fireEvent.change(savedBox(), { target: { value: "curry" } });
    expect(screen.getByText("Nothing saved matches that.")).toBeInTheDocument();
});

test("with nothing saved: None saved, and a hint when opened", async () => {
    render(<RecipesScreen api={fakeApi([]) as unknown as ApiClient} now={NOW} />);
    await screen.findByText("None saved");

    fireEvent.focus(savedBox());

    expect(screen.getByText("Nothing saved yet. Tap Save on a recipe you like.")).toBeInTheDocument();
    expect(screen.queryByRole("radiogroup", { name: "Filter by meal" })).not.toBeInTheDocument();
});

test("Save on a recipe saves a snapshot, fills, and shows the toast; Undo unsaves it", async () => {
    const api = await show();
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));
    const pill = await screen.findByRole("button", { name: "Save", pressed: false });

    fireEvent.click(pill);

    await screen.findByRole("button", { name: "Saved", pressed: true });
    const [id, body] = api.saveRecipe.mock.calls[0];
    expect(id).toMatch(/^[0-9a-f-]{36}$/);
    expect(body).toEqual({
        kind: "recipe",
        title: "Test pasta bake",
        meal: "dinner",
        totalMinutes: 25,
        servings: 2,
        ingredients: [
            { name: "Test penne", amount: "200 g", productId: "penne", for: "all" },
            { name: "Salt", amount: "a pinch", productId: null, for: "all" },
        ],
        steps: [{ text: "Cook the penne.", for: "all" }],
        alsoNeeded: [{ name: "1 onion", for: "all" }],
        diet: "any",
        servingsMeat: 0,
        servingsVeg: 0,
    });
    const toast = await screen
        .findByText("Saved to Hungry?")
        .then((t) => t.closest("[role=status]") as HTMLElement);

    fireEvent.click(within(toast).getByRole("button", { name: "Undo" }));

    await screen.findByRole("button", { name: "Save", pressed: false });
    expect(api.deleteSavedRecipe).toHaveBeenCalledWith(id);
});

test("tapping Saved on a recipe unsaves it, with Undo saving it again", async () => {
    const api = await show();
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));
    fireEvent.click(await screen.findByRole("button", { name: "Save" }));
    fireEvent.click(await screen.findByRole("button", { name: "Saved" }));

    await screen.findByRole("button", { name: "Save" });
    const id = api.saveRecipe.mock.calls[0][0];
    expect(api.deleteSavedRecipe).toHaveBeenCalledWith(id);
    await screen.findByText("Removed from saved");

    fireEvent.click(within(screen.getByRole("status")).getByRole("button", { name: "Undo" }));

    await screen.findByRole("button", { name: "Saved" });
    expect(api.saveRecipe).toHaveBeenCalledTimes(2);
});

test("a failed save says so and the pill stays unsaved", async () => {
    const api = fakeApi();
    api.saveRecipe.mockRejectedValueOnce(new Error("offline"));
    await show(api);
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));
    fireEvent.click(await screen.findByRole("button", { name: "Save" }));

    expect(await screen.findByText("Couldn't save that. Try again.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toHaveAttribute("aria-pressed", "false");
});

test("each Ready now idea has Save in its button row, saving the idea with how and appliance", async () => {
    const api = fakeApi(SAVED, async () => ({
        available: true,
        mode: "readyNow",
        meal: "snack",
        ideas: [IDEA],
    }));
    await show(api);
    fireEvent.click(screen.getByRole("radio", { name: /^Ready now/ }));
    fireEvent.click(screen.getByRole("button", { name: "Show me ideas" }));
    const card = await screen.findByRole("article", { name: "Test beans on toast" });

    fireEvent.click(within(card).getByRole("button", { name: "Save Test beans on toast" }));

    await within(card).findByRole("button", { name: "Saved Test beans on toast", pressed: true });
    expect(api.saveRecipe.mock.calls[0][1]).toEqual({
        kind: "readyNow",
        title: "Test beans on toast",
        meal: "snack",
        totalMinutes: 5,
        uses: [{ name: "Test beans", productId: "beans" }],
        alsoNeeded: [],
        diet: "any",
        how: "Microwave the beans; toast the bread.",
        appliance: "microwave",
    });
    expect(await screen.findByText("Saved to Hungry?")).toBeInTheDocument();
});

async function openTraybake(api = fakeApi()) {
    await show(api);
    fireEvent.focus(savedBox());
    fireEvent.click(screen.getByRole("button", { name: /^Test traybake/ }));
    await screen.findByRole("heading", { level: 1, name: "Test traybake" });
    return api;
}

test("choosing a saved recipe closes the box and shows it as drawn, checked against today's stock", async () => {
    await openTraybake();

    expect(screen.queryByRole("combobox", { name: "Saved" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Hungry?" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Saved", pressed: true })).toBeInTheDocument();
    expect(screen.getByText("Dinner · Serves 2 · 40 min · Saved 12 Oct by Sam")).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: "Your note" })).toHaveValue("Halve the chilli");
    expect(screen.getByText("Everyone at home sees this")).toBeInTheDocument();
    expect(screen.getByText("Missing 1 thing")).toBeInTheDocument();
    expect(screen.getByText("You've got everything except Halloumi.")).toBeInTheDocument();

    const ingredients = screen.getByRole("heading", {
        name: "Ingredients · checked against today's stock",
    }).parentElement!;
    expect(
        within(ingredients)
            .getAllByRole("listitem")
            .map((li) => li.textContent),
    ).toEqual(["✓2PeppersIn stock", "!225 gHalloumiNot in stock", "✓1 tspPaprikaUse first"]);
    expect(screen.getByText("Roast the peppers.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Adjust to what's in" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove from saved" })).toBeInTheDocument();
});

test("rename with the pencil: Enter saves; an empty name goes back", async () => {
    const api = await openTraybake();

    fireEvent.click(screen.getByRole("button", { name: "Rename" }));
    const input = screen.getByRole("textbox", { name: "Name" });
    expect(input).toHaveAttribute("maxLength", "80");
    fireEvent.change(input, { target: { value: "  Sam's traybake " } });
    fireEvent.keyDown(input, { key: "Enter" });
    fireEvent.blur(input);

    await screen.findByRole("heading", { level: 1, name: "Sam's traybake" });
    expect(api.updateSavedRecipe).toHaveBeenCalledWith("s1", { title: "Sam's traybake" });

    fireEvent.click(screen.getByRole("button", { name: "Rename" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Name" }), { target: { value: "  " } });
    fireEvent.blur(screen.getByRole("textbox", { name: "Name" }));
    expect(screen.getByRole("heading", { level: 1, name: "Sam's traybake" })).toBeInTheDocument();
    expect(api.updateSavedRecipe).toHaveBeenCalledTimes(1);
});

test("the note is saved on blur, only when it changed, and limited to 300", async () => {
    const api = await openTraybake();
    const note = screen.getByRole("textbox", { name: "Your note" });
    expect(note).toHaveAttribute("maxLength", "300");

    fireEvent.blur(note);
    expect(api.updateSavedRecipe).not.toHaveBeenCalled();

    fireEvent.change(note, { target: { value: "Good with rice" } });
    fireEvent.blur(note);

    await waitFor(() => expect(api.updateSavedRecipe).toHaveBeenCalledWith("s1", { note: "Good with rice" }));
});

test("a note that can't be saved says so", async () => {
    const api = fakeApi();
    api.updateSavedRecipe.mockRejectedValueOnce(new Error("offline"));
    await openTraybake(api);
    const note = screen.getByRole("textbox", { name: "Your note" });

    fireEvent.change(note, { target: { value: "Good with rice" } });
    fireEvent.blur(note);

    expect(await screen.findByRole("alert")).toHaveTextContent("Couldn't save your note. Try again.");
});

test("Add missing to the list adds each missing item by generic name, then says it's on the list", async () => {
    const { rerender } = render(<RecipesScreen api={fakeApi() as unknown as ApiClient} now={NOW} />);
    await screen.findByText(`${SAVED.length} saved`);
    fireEvent.focus(savedBox());
    fireEvent.click(screen.getByRole("button", { name: /^Test traybake/ }));

    fireEvent.click(await screen.findByRole("button", { name: "Add missing to the list" }));

    expect(list.add).toHaveBeenCalledWith("Halloumi", null, "savedRecipe");
    expect(list.add).toHaveBeenCalledTimes(1);

    list.open = [{ name: "halloumi" }]; // the list's live update
    rerender(<RecipesScreen api={fakeApi() as unknown as ApiClient} now={NOW} />);

    expect(screen.getByText("Halloumi is on the shopping list")).toBeInTheDocument();
    expect(
        screen.getByText("Everything else is in. Adjust it if you want to make it without."),
    ).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Added ✓" })).toBeDisabled();
    expect(screen.getByText("On the list")).toBeInTheDocument();
});

test("missing items already on the list aren't added again", async () => {
    const api = fakeApi([
        saved({
            ingredients: [
                { name: "Halloumi", amount: "225 g", productId: null, genericName: null, status: "missing" },
                {
                    name: "Tuna chunks",
                    amount: "1 tin",
                    productId: "t",
                    genericName: "Tuna",
                    status: "missing",
                },
            ],
            missing: ["Halloumi", "Tuna"],
        }),
    ]);
    list.open = [{ name: "Halloumi" }];
    render(<RecipesScreen api={api as unknown as ApiClient} now={NOW} />);
    await screen.findByText("1 saved");
    fireEvent.focus(savedBox());
    fireEvent.click(screen.getByRole("button", { name: /^Test traybake/ }));

    fireEvent.click(await screen.findByRole("button", { name: "Add missing to the list" }));

    expect(list.add.mock.calls).toEqual([["Tuna", null, "savedRecipe"]]);
});

test("Adjust to what's in opens the Adjust sheet with the saved recipe", async () => {
    const api = await openTraybake();

    fireEvent.click(screen.getByRole("button", { name: "Adjust to what's in" }));

    expect(await screen.findByRole("dialog", { name: "Adjust this recipe" })).toBeInTheDocument();
    await waitFor(() => expect(api.suggestRecipeAdditions).toHaveBeenCalledWith(savedToRecipe(SAVED[2])));
});

test("Remove from saved: gone from the list, Save again shows; Undo puts the same snapshot back", async () => {
    const api = await openTraybake();

    fireEvent.click(screen.getByRole("button", { name: "Remove from saved" }));

    await screen.findByRole("button", { name: "Save again" });
    expect(api.deleteSavedRecipe).toHaveBeenCalledWith("s1");
    expect(screen.getByRole("button", { name: "Save", pressed: false })).toBeInTheDocument();
    const toast = await screen
        .findByText("Removed from saved")
        .then((t) => t.closest("[role=status]") as HTMLElement);

    await act(async () => fireEvent.click(within(toast).getByRole("button", { name: "Undo" })));

    await screen.findByRole("button", { name: "Remove from saved" });
    expect(api.saveRecipe).toHaveBeenCalledWith(
        "s1",
        expect.objectContaining({
            kind: "recipe",
            title: "Test traybake",
            originalTitle: "Test traybake",
            note: "Halve the chilli",
            ingredients: expect.arrayContaining([
                {
                    name: "Peppers",
                    amount: "2",
                    productId: "peppers",
                    genericName: "Peppers",
                    for: "all",
                },
            ]),
        }),
    );

    fireEvent.click(screen.getByRole("button", { name: "Hungry?" }));
    await screen.findByRole("combobox", { name: "Saved" });
});

test("a saved Ready now idea shows what it uses and how, without Adjust", async () => {
    await show();
    fireEvent.focus(savedBox());
    fireEvent.click(screen.getByRole("button", { name: /^Test beans on toast/ }));

    await screen.findByRole("heading", { level: 1, name: "Test beans on toast" });
    expect(screen.getByText("Snack · Ready now · 5 min · Saved yesterday by Sam")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "How" })).toBeInTheDocument();
    expect(screen.getByText("Microwave the beans.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Adjust to what's in" })).not.toBeInTheDocument();
});

test("a saved half & half recipe keeps its split, legend and plate tags, and Adjust works on them (#110)", async () => {
    const half = saved({
        diet: "half",
        servingsMeat: 1,
        servingsVeg: 1,
        ingredients: [
            {
                name: "Peppers",
                amount: "2",
                productId: "peppers",
                genericName: "Peppers",
                status: "have",
                for: "all",
            },
            {
                name: "Test chorizo",
                amount: "100 g",
                productId: null,
                genericName: null,
                status: "have",
                for: "meat",
            },
        ],
        steps: [
            { text: "Roast the peppers.", for: "all" },
            { text: "Add the chorizo.", for: "meat" },
        ],
        missing: [],
    });
    await openTraybake(fakeApi([half, ...SAVED.filter((s) => s.id !== "s1")]));

    expect(screen.getByText(/^Dinner · Serves 2 \(1 meat, 1 veg\) · 40 min/)).toBeInTheDocument();
    expect(screen.getByLabelText("Plate tags")).toBeInTheDocument();
    expect(screen.getByText("Test chorizo").closest("li")).toHaveTextContent("Meat plates");
    expect(screen.getByText("Add the chorizo.").closest("li")).toHaveTextContent("Meat plates");

    const asRecipe = savedToRecipe(half);
    expect(asRecipe.diet).toBe("half");
    expect(asRecipe.ingredients[1].for).toBe("meat");
    expect(asRecipe.steps[1]).toEqual({ text: "Add the chorizo.", for: "meat" });
});
