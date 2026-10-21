import { act, fireEvent, render, screen, within } from "@testing-library/react";
import type { AdjustedIdea, ApiClient, IdeaAdjustment, QuickIdea, RecipeRequest, StockProduct } from "./api";
import { ideaAsRecipe, RecipesScreen } from "./RecipesScreen";

// Ready now: Adjust and Save on each idea (#108), project/ReadyNow.dc.html and project/ReadyNowStates.dc.html.

const EVENING = new Date("2026-10-21T17:00:00.000Z");

const product = (id: string, name: string): StockProduct => ({
    productId: id,
    barcode: null,
    displayName: name,
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
});

const STOCK = [
    product("beans", "Test beans"),
    product("bread", "Test bread"),
    product("cheddar", "Test cheddar"),
];

const use = (id: string, name: string) => ({
    productId: id,
    displayName: name,
    useFirst: false,
    daysUntilSoonest: null,
    soonestEstimatedExpiryDate: null,
});

const BEANS: QuickIdea = {
    title: "Test beans on toast",
    how: "Microwave the beans; toast the bread.",
    appliance: "microwave",
    minutes: 5,
    uses: [use("beans", "Test beans"), use("bread", "Test bread")],
    alsoNeeded: null,
};

const TEA: QuickIdea = {
    title: "Test cup of tea",
    how: "Boil the kettle.",
    appliance: "kettle",
    minutes: 3,
    uses: [],
    alsoNeeded: "Tea bags",
};

const CHEESY: QuickIdea = {
    title: "Test cheesy beans toastie",
    how: "Toast the bread with the cheddar and beans.",
    appliance: "toaster",
    minutes: 6,
    uses: [use("beans", "Test beans"), use("bread", "Test bread"), use("cheddar", "Test cheddar")],
    alsoNeeded: null,
};

function setup(adjust: (a: IdeaAdjustment) => Promise<AdjustedIdea>) {
    const suggestRecipe = vi.fn(async (r: RecipeRequest) => ({
        available: true,
        mode: "readyNow" as const,
        meal: r.meal,
        ideas: [BEANS, TEA],
    }));
    const api = {
        stock: async () => ({ generatedAt: "", totalUnits: 3, useFirstCount: 0, products: STOCK }),
        suggestRecipe,
        savedRecipes: async () => ({ savedRecipes: [] }),
        saveRecipe: vi.fn(),
        suggestRecipeAdditions: vi.fn(async () => ({ suggestions: [] })),
        adjustIdea: vi.fn(adjust),
    };
    render(<RecipesScreen api={api as unknown as ApiClient} now={EVENING} />);
    return api;
}

async function showIdeas(api: ReturnType<typeof setup>) {
    fireEvent.click(await screen.findByRole("radio", { name: /^Ready now/ }));
    const button = await screen.findByRole("button", { name: "Show me ideas" });
    await act(async () => fireEvent.click(button));
    return api;
}

const card = (title: string) => screen.getByRole("article", { name: title });
const sheet = () => screen.getByRole("dialog", { name: "Adjust this idea" });

async function adjust(title: string, tweak: string) {
    await act(async () =>
        fireEvent.click(within(card(title)).getByRole("button", { name: `Adjust ${title}` })),
    );
    fireEvent.change(within(sheet()).getByRole("textbox", { name: "Tweak" }), { target: { value: tweak } });
    await act(async () => fireEvent.click(within(sheet()).getByRole("button", { name: "Update idea" })));
}

beforeEach(() => localStorage.clear());

test("every card ends with Adjust and Save, equal width, as drawn", async () => {
    await showIdeas(setup(async () => ({ available: false })));

    for (const title of [BEANS.title, TEA.title]) {
        const buttons = within(card(title)).getAllByRole("button");
        expect(buttons.slice(-2).map((b) => b.textContent)).toEqual(["Adjust", "Save"]);
    }
});

test("Adjust opens the sheet for that idea: titled for an idea, Remove lists its uses, room for one thing to buy", async () => {
    const api = await showIdeas(setup(async () => ({ available: false })));

    await act(async () =>
        fireEvent.click(within(card(BEANS.title)).getByRole("button", { name: `Adjust ${BEANS.title}` })),
    );

    expect(sheet()).toBeInTheDocument();
    expect(
        within(sheet())
            .getAllByRole("checkbox")
            .map((c) => c.getAttribute("aria-label") ?? c.closest("label")?.textContent),
    ).toEqual(["Test beans", "Test bread"]);
    expect(api.suggestRecipeAdditions).toHaveBeenCalledWith(ideaAsRecipe(BEANS, "dinner"));

    fireEvent.click(within(sheet()).getByRole("button", { name: "Or type something you'd buy" }));
    fireEvent.change(within(sheet()).getByRole("textbox", { name: "Something you'd buy" }), {
        target: { value: "hot sauce" },
    });
    fireEvent.click(within(sheet()).getByRole("button", { name: "Add" }));
    expect(
        within(sheet()).queryByRole("button", { name: "Or type something you'd buy" }),
    ).not.toBeInTheDocument();
});

test("Update dims only that card, then shows the adjusted idea with the summary, Added tag and Undo", async () => {
    let answer!: (a: AdjustedIdea) => void;
    const api = await showIdeas(setup(() => new Promise((resolve) => (answer = resolve))));

    await act(async () =>
        fireEvent.click(within(card(BEANS.title)).getByRole("button", { name: `Adjust ${BEANS.title}` })),
    );
    fireEvent.change(within(sheet()).getByRole("searchbox", { name: "Add something from your cupboard" }), {
        target: { value: "ched" },
    });
    fireEvent.click(within(sheet()).getByRole("button", { name: "Test cheddar" }));
    fireEvent.change(within(sheet()).getByRole("textbox", { name: "Tweak" }), {
        target: { value: "make it a toastie" },
    });
    await act(async () => fireEvent.click(within(sheet()).getByRole("button", { name: "Update idea" })));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(card(BEANS.title)).toHaveAttribute("aria-busy", "true");
    expect(within(card(BEANS.title)).getByRole("status")).toHaveTextContent("Updating this idea…");
    expect(card(TEA.title)).toHaveAttribute("aria-busy", "false");
    expect(within(card(TEA.title)).getByRole("button", { name: `Adjust ${TEA.title}` })).toBeEnabled();
    expect(api.adjustIdea).toHaveBeenCalledWith({
        idea: {
            title: BEANS.title,
            how: BEANS.how,
            appliance: "microwave",
            minutes: 5,
            uses: [
                { productId: "beans", displayName: "Test beans" },
                { productId: "bread", displayName: "Test bread" },
            ],
            alsoNeeded: null,
        },
        meal: "dinner",
        add: { productIds: ["cheddar"], typed: [] },
        remove: [],
        tweak: "make it a toastie",
    });

    await act(async () =>
        answer({
            available: true,
            idea: CHEESY,
            adjustmentSummary: { added: ["Cheddar"], removed: [], tweak: "make it a toastie" },
        }),
    );

    const adjusted = card(CHEESY.title);
    expect(adjusted.className).toMatch(/ideaAdjusted/);
    expect(adjusted).toHaveTextContent('Adjusted: + Cheddar · "make it a toastie"');
    expect(adjusted).toHaveTextContent("Toaster");
    expect(within(adjusted).getByRole("link", { name: /Test cheddar/ })).toHaveTextContent("Added");
    expect(within(adjusted).getByRole("link", { name: /Test beans/ })).not.toHaveTextContent("Added");

    fireEvent.click(within(adjusted).getByRole("button", { name: "Undo" }));
    expect(card(BEANS.title).className).not.toMatch(/ideaAdjusted/);
    expect(within(card(BEANS.title)).queryByRole("button", { name: "Undo" })).not.toBeInTheDocument();
});

test("adjusting again works on the latest version, and Undo steps back once each time", async () => {
    const versions = [
        { ...CHEESY, title: "Version 1" },
        { ...CHEESY, title: "Version 2" },
    ];
    const api = await showIdeas(
        setup(async () => ({
            available: true,
            idea: versions.shift(),
            adjustmentSummary: { added: [], removed: [], tweak: "x" },
        })),
    );

    await adjust(BEANS.title, "spicier");
    await adjust("Version 1", "for 3");

    expect(api.adjustIdea.mock.calls[1][0].idea.title).toBe("Version 1");
    fireEvent.click(within(card("Version 2")).getByRole("button", { name: "Undo" }));
    fireEvent.click(within(card("Version 1")).getByRole("button", { name: "Undo" }));
    expect(card(BEANS.title)).toBeInTheDocument();
});

test("an error stays on that card, with Try again sending the same changes", async () => {
    const api = await showIdeas(
        setup(async () => ({ available: false, message: "Couldn't update this idea just now. Try again." })),
    );

    await adjust(BEANS.title, "spicier");

    const alert = within(card(BEANS.title)).getByRole("alert");
    expect(alert).toHaveTextContent("Couldn't update this idea just now. Try again.");
    expect(within(card(TEA.title)).queryByRole("alert")).not.toBeInTheDocument();
    await act(async () => fireEvent.click(within(alert).getByRole("button", { name: "Try again" })));
    expect(api.adjustIdea).toHaveBeenCalledTimes(2);
    expect(api.adjustIdea.mock.calls[1][0]).toEqual(api.adjustIdea.mock.calls[0][0]);
});

test("More ideas avoids adjusted titles too", async () => {
    const api = await showIdeas(
        setup(async () => ({
            available: true,
            idea: CHEESY,
            adjustmentSummary: { added: [], removed: [], tweak: "x" },
        })),
    );
    await adjust(BEANS.title, "toastie");

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "More ideas" })));

    expect(api.suggestRecipe.mock.calls[1][0].avoidTitles).toEqual([BEANS.title, TEA.title, CHEESY.title]);
});

test("what the model couldn't do is said on the card", async () => {
    await showIdeas(
        setup(async () => ({
            available: true,
            idea: { ...CHEESY, couldNotAdd: ["Hot sauce"], couldNotRemove: ["Test bread"] },
            adjustmentSummary: { added: ["Hot sauce"], removed: ["Test bread"], tweak: null },
        })),
    );
    await adjust(BEANS.title, "x");

    expect(card(CHEESY.title)).toHaveTextContent("Couldn't add: Hot sauce");
    expect(card(CHEESY.title)).toHaveTextContent("Couldn't remove: Test bread");
});
