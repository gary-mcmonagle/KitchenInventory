import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ApiClient, QuickIdea, Recipe, SavedRecipe, StockProduct } from "./api";
import { RateIcons, RatePills } from "./RatingControls";
import { ideaSnapshot, recipeSnapshot } from "./ratings";
import { fakeRatingsApi, rating } from "./ratingsFixture";
import { RecipesScreen } from "./RecipesScreen";
import { RatingsProvider } from "./useRatings";

// Rating recipes, ideas and saved recipes 👍 / 👎 (#128): project/RateRecipe.dc.html.

const RECIPE: Recipe = {
    title: "Test pasta bake",
    summary: "A quick test pasta.",
    meal: "dinner",
    servings: 2,
    totalMinutes: 25,
    ingredients: [
        { name: "Test penne", amount: "200 g", productId: "penne", useFirst: false },
        { name: "Peppers", amount: "2", productId: null, useFirst: false },
        { name: "Salt", amount: "a pinch", productId: null, useFirst: false },
    ],
    steps: [{ text: "Cook the penne.", for: "all" }],
    usesUp: [],
    alsoNeeded: [],
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

const loaded = async () => {
    await waitFor(() => expect(screen.getByRole("button", { name: "Like" })).toBeInTheDocument());
};

function pills(api = fakeRatingsApi()) {
    render(
        <RatingsProvider api={api.asClient()}>
            <RatePills snapshot={recipeSnapshot(RECIPE)} context="suggested" />
        </RatingsProvider>,
    );
    return api;
}

const like = () => screen.getByRole("button", { name: "Like" });
const notForUs = () => screen.getByRole("button", { name: "Not for us" });

test("Like saves a thumbs up with a small snapshot and says Saved; it shows pressed", async () => {
    const api = pills();
    await loaded();
    expect(like()).toHaveAttribute("aria-pressed", "false");

    fireEvent.click(like());

    await waitFor(() => expect(like()).toHaveAttribute("aria-pressed", "true"));
    expect(api.putRating).toHaveBeenCalledTimes(1);
    const [id, body] = api.putRating.mock.calls[0];
    expect(id).toMatch(/[0-9a-f-]{36}/);
    expect(body).toMatchObject({
        rating: "up",
        context: "suggested",
        kind: "recipe",
        title: "Test pasta bake",
        meal: "dinner",
        totalMinutes: 25,
    });
    expect(body.ingredients).toEqual([{ name: "Test penne" }, { name: "Peppers" }, { name: "Salt" }]);
    expect(JSON.stringify(body)).not.toMatch(/Cook the penne|200 g/); // no steps, no amounts
    expect(await screen.findByRole("status")).toHaveTextContent("Saved");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument(); // no reasons sheet for a 👍
});

test("tapping the pressed button again clears the rating", async () => {
    const api = fakeRatingsApi([rating({ id: "mine", title: "Test pasta bake" })]);
    pills(api);
    await waitFor(() => expect(like()).toHaveAttribute("aria-pressed", "true"));

    fireEvent.click(like());

    await waitFor(() => expect(like()).toHaveAttribute("aria-pressed", "false"));
    expect(api.deleteRating).toHaveBeenCalledWith("mine");
});

test("buttons start pressed when you've rated that dish before, whatever the case and spaces of the title", async () => {
    pills(fakeRatingsApi([rating({ id: "mine", title: "  TEST pasta   bake", rating: "down" })]));

    await waitFor(() => expect(notForUs()).toHaveAttribute("aria-pressed", "true"));
    expect(like()).toHaveAttribute("aria-pressed", "false");
});

test("someone else's rating doesn't press your buttons", async () => {
    pills(fakeRatingsApi([rating({ id: "sam", title: "Test pasta bake", personName: "Sam", mine: false })]));
    await loaded();

    expect(like()).toHaveAttribute("aria-pressed", "false");
    expect(notForUs()).toHaveAttribute("aria-pressed", "false");
});

test("Not for us saves the thumbs down at once and opens What wasn't right?; Skip keeps it without reasons", async () => {
    const api = pills();
    await loaded();

    fireEvent.click(notForUs());

    const sheet = await screen.findByRole("dialog", { name: "What wasn't right?" });
    expect(api.putRating.mock.calls[0][1]).toMatchObject({ rating: "down" });
    expect(
        within(sheet)
            .getAllByRole("button", { pressed: false })
            .map((b) => b.textContent),
    ).toEqual(
        expect.arrayContaining([
            "Too spicy",
            "Too much effort",
            "Took too long",
            "Not filling",
            "Didn't like an ingredient",
            "Just not for us",
        ]),
    );

    fireEvent.click(within(sheet).getByRole("button", { name: "Skip" }));

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(notForUs()).toHaveAttribute("aria-pressed", "true");
    expect(api.updateRating).not.toHaveBeenCalled();
});

test("Done saves the chosen reasons, in the sheet's order", async () => {
    const api = pills();
    await loaded();
    fireEvent.click(notForUs());
    const sheet = await screen.findByRole("dialog");

    fireEvent.click(within(sheet).getByRole("button", { name: "Took too long" }));
    fireEvent.click(within(sheet).getByRole("button", { name: "Too spicy" }));
    fireEvent.click(within(sheet).getByRole("button", { name: "Done" }));

    await waitFor(() => expect(api.updateRating).toHaveBeenCalledTimes(1));
    expect(api.updateRating.mock.calls[0][1]).toEqual({
        reasons: ["tooSpicy", "tookTooLong"],
        dislikedIngredients: [],
    });
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(notForUs()).toHaveAttribute("aria-pressed", "true");
});

test("Didn't like an ingredient shows the recipe's ingredients to pick from", async () => {
    const api = pills();
    await loaded();
    fireEvent.click(notForUs());
    const sheet = await screen.findByRole("dialog");
    expect(within(sheet).queryByText("Which one?")).not.toBeInTheDocument();

    fireEvent.click(within(sheet).getByRole("button", { name: "Didn't like an ingredient" }));
    const which = within(sheet).getByRole("group", { name: "Which one?" });
    expect(
        within(which)
            .getAllByRole("button")
            .map((b) => b.textContent),
    ).toEqual(["Test penne", "Peppers", "Salt"]);
    fireEvent.click(within(which).getByRole("button", { name: "Peppers" }));
    fireEvent.click(within(sheet).getByRole("button", { name: "Done" }));

    await waitFor(() => expect(api.updateRating).toHaveBeenCalledTimes(1));
    expect(api.updateRating.mock.calls[0][1]).toEqual({
        reasons: ["ingredient"],
        dislikedIngredients: ["Peppers"],
    });
});

test("the ingredient reason is dropped when no ingredient is picked, and not offered when the dish has none", async () => {
    const api = pills();
    await loaded();
    fireEvent.click(notForUs());
    const sheet = await screen.findByRole("dialog");
    fireEvent.click(within(sheet).getByRole("button", { name: "Didn't like an ingredient" }));
    fireEvent.click(within(sheet).getByRole("button", { name: "Not filling" }));
    fireEvent.click(within(sheet).getByRole("button", { name: "Done" }));
    await waitFor(() => expect(api.updateRating).toHaveBeenCalledTimes(1));
    expect(api.updateRating.mock.calls[0][1]).toEqual({ reasons: ["notFilling"], dislikedIngredients: [] });
});

test("without ingredients the sheet doesn't offer the ingredient reason", async () => {
    render(
        <RatingsProvider api={fakeRatingsApi().asClient()}>
            <RatePills snapshot={{ ...recipeSnapshot(RECIPE), ingredients: [] }} context="suggested" />
        </RatingsProvider>,
    );
    await loaded();
    fireEvent.click(notForUs());

    const sheet = await screen.findByRole("dialog");

    expect(
        within(sheet).queryByRole("button", { name: "Didn't like an ingredient" }),
    ).not.toBeInTheDocument();
});

test("Escape and a tap on the scrim close the sheet like Skip, keeping the thumbs down", async () => {
    const api = pills();
    await loaded();
    fireEvent.click(notForUs());
    fireEvent.keyDown(await screen.findByRole("dialog"), { key: "Escape" });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    fireEvent.click(notForUs()); // unpress
    await waitFor(() => expect(api.deleteRating).toHaveBeenCalled());
    fireEvent.click(notForUs()); // again
    await screen.findByRole("dialog");
    fireEvent.click(document.querySelector('[aria-hidden="true"][class*="scrim"]')!);

    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(api.updateRating).not.toHaveBeenCalled();
});

test("changing from Like to Not for us replaces your rating: one rating of the dish", async () => {
    const api = fakeRatingsApi([rating({ id: "first", title: "Test pasta bake" })]);
    pills(api);
    await waitFor(() => expect(like()).toHaveAttribute("aria-pressed", "true"));

    fireEvent.click(notForUs());

    await waitFor(() => expect(notForUs()).toHaveAttribute("aria-pressed", "true"));
    expect(like()).toHaveAttribute("aria-pressed", "false");
    expect(api.store.ratings).toHaveLength(1);
    expect(api.store.ratings[0].rating).toBe("down");
});

test("a failed save says so and the button goes back to how it was", async () => {
    const api = fakeRatingsApi();
    api.putRating.mockRejectedValueOnce(new Error("down"));
    pills(api);
    await loaded();

    fireEvent.click(like());

    expect(await screen.findByText("Couldn't save that. Try again.")).toBeInTheDocument();
    expect(like()).toHaveAttribute("aria-pressed", "false");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});

test("a failed clear leaves the rating as it was", async () => {
    const api = fakeRatingsApi([rating({ id: "mine", title: "Test pasta bake" })]);
    api.deleteRating.mockRejectedValueOnce(new Error("down"));
    pills(api);
    await waitFor(() => expect(like()).toHaveAttribute("aria-pressed", "true"));

    fireEvent.click(like());

    expect(await screen.findByText("Couldn't save that. Try again.")).toBeInTheDocument();
    expect(like()).toHaveAttribute("aria-pressed", "true");
});

test("outside the signed-in app (no ratings) there are no buttons", () => {
    const { container } = render(<RatePills snapshot={recipeSnapshot(RECIPE)} context="suggested" />);
    expect(container).toBeEmptyDOMElement();
});

test("an idea has two icon buttons labelled Like and Not for us, with the reasons sheet on a 👎", async () => {
    const api = fakeRatingsApi();
    render(
        <RatingsProvider api={api.asClient()}>
            <RateIcons snapshot={ideaSnapshot(IDEA, "snack")} context="readyNow" />
        </RatingsProvider>,
    );
    await loaded();

    fireEvent.click(like());
    await waitFor(() => expect(like()).toHaveAttribute("aria-pressed", "true"));
    expect(api.putRating.mock.calls[0][1]).toMatchObject({
        rating: "up",
        kind: "readyNow",
        context: "readyNow",
        equipment: ["microwave"],
    });

    fireEvent.click(notForUs());
    expect(await screen.findByRole("dialog", { name: "What wasn't right?" })).toBeInTheDocument();
    expect(notForUs()).toHaveAttribute("aria-pressed", "true");
    expect(like()).toHaveAttribute("aria-pressed", "false");
});

// On Hungry?: where they show up.

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

const savedRecipe = (id: string, title: string): SavedRecipe => ({
    id,
    kind: "recipe",
    title,
    originalTitle: title,
    meal: "dinner",
    totalMinutes: 25,
    servings: 2,
    ingredients: [],
    steps: [],
    alsoNeeded: [],
    how: null,
    appliance: null,
    note: "",
    savedByName: "Gary",
    savedAt: "2026-11-08T08:00:00.000Z",
    updatedAt: "2026-11-08T08:00:00.000Z",
    missing: [],
    usesUp: [],
});

function hungry(
    ratings = fakeRatingsApi(),
    suggest: () => Promise<unknown> = async () => ({ available: true, recipe: RECIPE }),
) {
    const saved: SavedRecipe[] = [];
    const api = {
        ...ratings,
        stock: async () => ({ generatedAt: "", totalUnits: 1, useFirstCount: 0, products: STOCK }),
        suggestRecipe: vi.fn(suggest),
        suggestRecipeAdditions: vi.fn(async () => ({ suggestions: [] })),
        savedRecipes: vi.fn(async () => ({ savedRecipes: saved })),
        saveRecipe: vi.fn(async (id: string, body: { title: string }) => {
            const recipe = savedRecipe(id, body.title);
            saved.push(recipe);
            return recipe;
        }),
        updateSavedRecipe: vi.fn(),
        deleteSavedRecipe: vi.fn(async () => undefined),
    };
    render(
        <RatingsProvider api={api as unknown as ApiClient}>
            <RecipesScreen api={api as unknown as ApiClient} now={new Date("2026-11-08T17:00:00.000Z")} />
        </RatingsProvider>,
    );
    return api;
}

beforeEach(() => localStorage.clear());

test("a Cook something recipe has Fancy this? with Like and Not for us, and an adjusted one is rated as that version", async () => {
    hungry();
    await screen.findByRole("button", { name: "Get a recipe" });
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));

    expect(await screen.findByText("Fancy this?")).toBeInTheDocument();
    fireEvent.click(like());
    await waitFor(() => expect(like()).toHaveAttribute("aria-pressed", "true"));
});

test("Hungry? has an Our taste row with the number of ratings", async () => {
    hungry(
        fakeRatingsApi([
            rating({ id: "a", title: "One" }),
            rating({ id: "b", title: "Two", personName: "Sam", mine: false }),
        ]),
    );

    const row = await screen.findByRole("link", { name: /Our taste/ });

    await waitFor(() => expect(row).toHaveTextContent("2 ratings"));
    expect(row).toHaveAttribute("href", "/taste");
});

test("saving a recipe reads the ratings again, so the 👍 it recorded shows", async () => {
    const ratings = fakeRatingsApi();
    const api = hungry(ratings);
    await screen.findByRole("button", { name: "Get a recipe" });
    fireEvent.click(screen.getByRole("button", { name: "Get a recipe" }));
    await screen.findByText("Fancy this?");
    await waitFor(() => expect(ratings.ratings).toHaveBeenCalledTimes(1));
    ratings.store.ratings = [rating({ id: "from-save", title: "Test pasta bake", context: "saved" })]; // what the server recorded on save

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(api.saveRecipe).toHaveBeenCalled());
    await waitFor(() => expect(like()).toHaveAttribute("aria-pressed", "true"));
    expect(ratings.ratings).toHaveBeenCalledTimes(2);
});
