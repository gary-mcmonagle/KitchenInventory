import type { QuickIdea, Recipe, SavedRecipe } from "./api";
import {
    bodyOf,
    cookedAfter,
    dishKey,
    ideaSnapshot,
    inTab,
    normalizeTitle,
    reasonsText,
    recipeSnapshot,
    savedSnapshot,
    snapshotOf,
    whenText,
    whoText,
    withoutRating,
    withRating,
} from "./ratings";
import { dishesOf, rating } from "./ratingsFixture";

// Ratings (#128): the helpers behind the buttons and Our taste.

const RECIPE: Recipe = {
    title: "Test pasta bake",
    summary: "A quick test pasta.",
    meal: "dinner",
    servings: 2,
    totalMinutes: 25,
    ingredients: [
        { name: "Test penne", amount: "200 g", productId: "penne", useFirst: false },
        { name: "  ", amount: "", productId: null, useFirst: false },
        { name: "Salt", amount: "a pinch", productId: null, useFirst: false },
    ],
    steps: [{ text: "Cook the penne.", for: "all" }],
    usesUp: [],
    alsoNeeded: [],
    couldNotFitIn: [],
    equipment: ["hob"],
};

test("a dish is its kind and its title with case and spaces ignored (the API's rule)", () => {
    expect(normalizeTitle("  Halloumi   TRAYBAKE ")).toBe("halloumi traybake");
    expect(dishKey("recipe", "Halloumi  traybake")).toBe(dishKey("recipe", " halloumi TRAYBAKE"));
    expect(dishKey("recipe", "Penne")).not.toBe(dishKey("readyNow", "Penne"));
});

test("a recipe's snapshot has the main ingredients and kit, and no steps or amounts", () => {
    const snapshot = recipeSnapshot(RECIPE);

    expect(snapshot).toMatchObject({
        kind: "recipe",
        title: "Test pasta bake",
        meal: "dinner",
        totalMinutes: 25,
        diet: "any",
        equipment: ["hob"],
    });
    expect(snapshot.ingredients).toEqual([{ name: "Test penne" }, { name: "Salt" }]);
    expect(JSON.stringify(snapshot)).not.toMatch(/Cook the penne|200 g|steps|amount/);
});

test("at most 15 ingredients are kept", () => {
    const many = {
        ...RECIPE,
        ingredients: Array.from({ length: 30 }, (_, i) => ({
            name: `Thing ${i}`,
            amount: "1",
            productId: null,
            useFirst: false,
        })),
    };
    expect(recipeSnapshot(many).ingredients).toHaveLength(15);
});

const IDEA: QuickIdea = {
    title: "Test beans on toast",
    how: "Microwave the beans.",
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

test("an idea's snapshot is a Ready now rating with its appliance as the kit", () => {
    expect(ideaSnapshot(IDEA, "snack")).toEqual({
        kind: "readyNow",
        title: "Test beans on toast",
        meal: "snack",
        totalMinutes: 5,
        diet: "any",
        ingredients: [{ name: "Test beans" }],
        equipment: ["microwave"],
    });
    expect(ideaSnapshot({ ...IDEA, appliance: "none" }, "snack").equipment).toEqual([]);
});

test("a saved recipe is rated under its original title, so a rename doesn't make a new dish", () => {
    const saved = {
        id: "s1",
        kind: "recipe",
        title: "Sam's traybake",
        originalTitle: "Halloumi traybake",
        meal: "dinner",
        totalMinutes: 40,
        servings: 2,
        ingredients: [
            { name: "Halloumi", amount: "225 g", productId: null, genericName: "Halloumi", status: "have" },
        ],
        steps: [],
        alsoNeeded: [],
        how: null,
        appliance: null,
        note: "",
        savedByName: "Sam",
        savedAt: "",
        updatedAt: "",
        missing: [],
        usesUp: [],
    } satisfies SavedRecipe;

    const snapshot = savedSnapshot(saved);

    expect(snapshot).toMatchObject({ kind: "recipe", title: "Halloumi traybake", savedRecipeId: "s1" });
    expect(snapshot.ingredients).toEqual([{ name: "Halloumi", genericName: "Halloumi" }]);
});

test("a rating's body puts it back as it was, and its snapshot is what it was of", () => {
    const r = rating({
        id: "a",
        title: "Penne",
        rating: "down",
        reasons: ["ingredient"],
        dislikedIngredients: ["Peppers"],
        context: "afterCooking",
        equipment: ["hob"],
    });

    expect(bodyOf(r)).toMatchObject({
        rating: "down",
        reasons: ["ingredient"],
        dislikedIngredients: ["Peppers"],
        context: "afterCooking",
        kind: "recipe",
        title: "Penne",
    });
    expect(snapshotOf(r)).not.toHaveProperty("rating");
    expect(snapshotOf(r)).toMatchObject({ kind: "recipe", title: "Penne", equipment: ["hob"] });
});

test("rating a dish again replaces your earlier rating of it, and others' stay", () => {
    const before = dishesOf([
        rating({ id: "g1", title: "Penne bake", ratedAt: "2026-11-01T08:00:00.000Z" }),
        rating({
            id: "s1",
            title: "Penne bake",
            personName: "Sam",
            mine: false,
            rating: "down",
            ratedAt: "2026-11-02T08:00:00.000Z",
        }),
    ]);

    const after = withRating(
        before,
        rating({ id: "g2", title: " penne  BAKE", rating: "down", ratedAt: "2026-11-08T08:00:00.000Z" }),
    );

    expect(after).toHaveLength(1);
    expect(after[0].ratings.map((r) => r.id)).toEqual(["g2", "s1"]);
});

test("a new dish goes first, and taking a rating back drops an empty dish", () => {
    const before = dishesOf([rating({ id: "g1", title: "Old dish", ratedAt: "2026-11-01T08:00:00.000Z" })]);

    const after = withRating(
        before,
        rating({ id: "g2", title: "New dish", ratedAt: "2026-11-08T08:00:00.000Z" }),
    );

    expect(after.map((d) => d.title)).toEqual(["New dish", "Old dish"]);
    expect(withoutRating(after, "g2").map((d) => d.title)).toEqual(["Old dish"]);
});

test("a dish people disagree about is in both tabs; the words say who and why", () => {
    const [dish] = dishesOf([
        rating({
            id: "g",
            title: "Thai curry",
            rating: "up",
            context: "afterCooking",
            ratedAt: "2026-11-02T08:00:00.000Z",
        }),
        rating({
            id: "s",
            title: "Thai curry",
            personName: "Sam",
            mine: false,
            rating: "down",
            reasons: ["tooSpicy", "ingredient"],
            dislikedIngredients: ["Peppers", "Chilli"],
            ratedAt: "2026-11-03T08:00:00.000Z",
        }),
    ]);

    expect([inTab(dish, "liked"), inTab(dish, "not")]).toEqual([true, true]);
    expect(whoText(dish)).toBe("Sam 👎 · Gary 👍");
    expect(reasonsText(dish)).toBe("Too spicy · Didn't like: Peppers, Chilli");
    expect(cookedAfter(dish)).toBe(true);
});

test("an ingredient reason without names reads as the plain reason, and repeats are said once", () => {
    const [dish] = dishesOf([
        rating({ id: "a", title: "Risotto", rating: "down", reasons: ["tookTooLong", "ingredient"] }),
        rating({
            id: "b",
            title: "Risotto",
            personName: "Sam",
            mine: false,
            rating: "down",
            reasons: ["tookTooLong"],
        }),
    ]);

    expect(reasonsText(dish)).toBe("Took too long · Didn't like an ingredient");
});

test("when it was rated: today, yesterday, days ago, last week, or the date", () => {
    const now = new Date("2026-11-08T12:00:00.000Z");
    expect(whenText("2026-11-08T07:00:00.000Z", now)).toBe("today");
    expect(whenText("2026-11-07T20:00:00.000Z", now)).toBe("yesterday");
    expect(whenText("2026-11-05T08:00:00.000Z", now)).toBe("3 days ago");
    expect(whenText("2026-11-01T08:00:00.000Z", now)).toBe("last week");
    expect(whenText("2026-10-12T08:00:00.000Z", now)).toBe("12 Oct");
});
