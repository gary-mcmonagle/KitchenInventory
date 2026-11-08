import type {
    QuickIdea,
    RatedDish,
    Rating,
    RatingBody,
    RatingContext,
    RatingKind,
    RatingReason,
    Recipe,
    RecipeMeal,
    SavedRecipe,
} from "./api";

/** Ratings (#128): the helpers behind the 👍 / 👎 buttons and Our taste. Pure, so they're easy to test. */

export const MAX_INGREDIENTS = 15;

/** The dish's identity within its kind: trimmed, spaces collapsed, lower case. The same rule as the API's `key`. */
export const normalizeTitle = (title: string): string => title.trim().split(/\s+/).join(" ").toLowerCase();

/** One key for a kind and a title, to find a person's rating of what's on screen. */
export const dishKey = (kind: RatingKind, title: string): string => `${kind}|${normalizeTitle(title)}`;

/** The optional reasons for a 👎 (the "What wasn't right?" sheet), in the order the sheet shows them. */
export const REASONS: Array<{ id: RatingReason; label: string }> = [
    { id: "tooSpicy", label: "Too spicy" },
    { id: "tooMuchEffort", label: "Too much effort" },
    { id: "tookTooLong", label: "Took too long" },
    { id: "notFilling", label: "Not filling" },
    { id: "ingredient", label: "Didn't like an ingredient" },
    { id: "justNotForUs", label: "Just not for us" },
];

/** What the API keeps of a dish: no steps and no amounts. */
export type RatingSnapshot = Omit<RatingBody, "rating" | "reasons" | "dislikedIngredients" | "context">;

const MEALS: Record<RecipeMeal, string> = {
    breakfast: "Breakfast",
    lunch: "Lunch",
    dinner: "Dinner",
    snack: "Snack",
};
export const mealName = (meal: RecipeMeal): string => MEALS[meal];

const mains = (names: string[]): Array<{ name: string }> =>
    names
        .map((n) => n.trim())
        .filter((n) => n.length > 0)
        .slice(0, MAX_INGREDIENTS)
        .map((name) => ({ name }));

/** A recipe from Cook something, as shown (an adjusted one is rated as that version). */
export function recipeSnapshot(recipe: Recipe): RatingSnapshot {
    return {
        kind: "recipe",
        title: recipe.title,
        meal: recipe.meal,
        totalMinutes: recipe.totalMinutes,
        diet: recipe.diet ?? "any",
        ingredients: mains(recipe.ingredients.map((i) => i.name)),
        equipment: recipe.equipment ?? [],
    };
}

/** A Ready now idea, as shown. */
export function ideaSnapshot(idea: QuickIdea, meal: RecipeMeal): RatingSnapshot {
    return {
        kind: "readyNow",
        title: idea.title,
        meal,
        totalMinutes: idea.minutes,
        diet: idea.diet ?? "any",
        ingredients: mains(idea.uses.map((u) => u.displayName)),
        equipment: idea.appliance === "none" ? [] : [idea.appliance],
    };
}

/** A saved recipe or idea. Its **original** title is the dish, so renaming it doesn't make a new dish. */
export function savedSnapshot(saved: SavedRecipe): RatingSnapshot {
    return {
        kind: saved.kind,
        title: saved.originalTitle,
        meal: saved.meal,
        totalMinutes: saved.totalMinutes,
        diet: saved.diet ?? "any",
        ingredients: saved.ingredients
            .slice(0, MAX_INGREDIENTS)
            .filter((i) => i.name.trim().length > 0)
            .map((i) => ({ name: i.name.trim(), genericName: i.genericName })),
        equipment:
            saved.kind === "readyNow" && saved.appliance && saved.appliance !== "none"
                ? [saved.appliance]
                : [],
        savedRecipeId: saved.id,
    };
}

/** The body that puts a rating back as it was (Undo of a removal, or a change from 👍 to 👎). */
export function bodyOf(rating: Rating): RatingBody {
    return {
        rating: rating.rating,
        reasons: rating.reasons,
        dislikedIngredients: rating.dislikedIngredients,
        context: rating.context,
        kind: rating.kind,
        title: rating.title,
        meal: rating.meal,
        totalMinutes: rating.totalMinutes,
        diet: rating.diet,
        ingredients: rating.ingredients,
        equipment: rating.equipment,
        savedRecipeId: rating.savedRecipeId,
        planId: rating.planId,
        mealId: rating.mealId,
    };
}

/** The snapshot of what a rating was of (to rate the same dish again, e.g. from 👍 to 👎). */
export function snapshotOf(rating: Rating): RatingSnapshot {
    return {
        kind: rating.kind,
        title: rating.title,
        meal: rating.meal,
        totalMinutes: rating.totalMinutes,
        diet: rating.diet,
        ingredients: rating.ingredients,
        equipment: rating.equipment,
        savedRecipeId: rating.savedRecipeId,
        planId: rating.planId,
        mealId: rating.mealId,
    };
}

/** The ratings with one person's rating of a dish replaced, as the server does: a person has one rating per dish. */
export function withRating(dishes: RatedDish[], rating: Rating): RatedDish[] {
    const key = dishKey(rating.kind, rating.title);
    const others = dishes
        .map((d) =>
            dishKey(d.kind, d.title) === key || d.ratings.some((r) => r.id === rating.id)
                ? {
                      ...d,
                      ratings: d.ratings.filter(
                          (r) => !(r.mine && (r.id === rating.id || dishKey(r.kind, r.title) === key)),
                      ),
                  }
                : d,
        )
        .filter((d) => d.ratings.length > 0);
    const dish = others.find((d) => dishKey(d.kind, d.title) === key);
    const joined = dish
        ? others.map((d) =>
              d === dish ? { ...d, ratings: [rating, ...d.ratings], latestAt: rating.ratedAt } : d,
          )
        : [
              {
                  kind: rating.kind,
                  key: rating.key,
                  title: rating.title,
                  meal: rating.meal,
                  diet: rating.diet,
                  totalMinutes: rating.totalMinutes,
                  latestAt: rating.ratedAt,
                  ratings: [rating],
              },
              ...others,
          ];
    return [...joined].sort((a, b) => b.latestAt.localeCompare(a.latestAt));
}

/** The ratings without one (when it's taken back). */
export function withoutRating(dishes: RatedDish[], id: string): RatedDish[] {
    return dishes
        .map((d) => ({ ...d, ratings: d.ratings.filter((r) => r.id !== id) }))
        .filter((d) => d.ratings.length > 0);
}

/** How many ratings there are in all. */
export const totalRatings = (dishes: RatedDish[]): number => dishes.reduce((n, d) => n + d.ratings.length, 0);

export type TasteTab = "liked" | "not";

/** A dish is in **Liked** if anyone liked it and in **Not for us** if anyone didn't: a dish people disagree about is in both. */
export const inTab = (dish: RatedDish, tab: TasteTab): boolean =>
    dish.ratings.some((r) => r.rating === (tab === "liked" ? "up" : "down"));

const label = (reason: RatingReason): string => REASONS.find((r) => r.id === reason)!.label;

/** "Didn't like: mushrooms", "Too spicy · Took too long": what the 👎s on a dish said. */
export function reasonsText(dish: RatedDish): string {
    const parts: string[] = [];
    for (const rating of dish.ratings.filter((r) => r.rating === "down")) {
        for (const reason of rating.reasons) {
            const text =
                reason === "ingredient" && rating.dislikedIngredients.length > 0
                    ? `Didn't like: ${rating.dislikedIngredients.join(", ")}`
                    : label(reason);
            if (!parts.includes(text)) parts.push(text);
        }
    }
    return parts.join(" · ");
}

/** "Gary 👍 · Sam 👎": who rated it, newest first. */
export const whoText = (dish: RatedDish): string =>
    dish.ratings.map((r) => `${r.personName} ${r.rating === "up" ? "👍" : "👎"}`).join(" · ");

/** "today", "yesterday", "3 days ago", "last week", "12 Oct". */
export function whenText(iso: string, now: Date = new Date()): string {
    const then = new Date(iso);
    const day = (d: Date) => Date.UTC(d.getFullYear(), d.getMonth(), d.getDate());
    const days = Math.round((day(now) - day(then)) / 86_400_000);
    if (days <= 0) return "today";
    if (days === 1) return "yesterday";
    if (days < 7) return `${days} days ago`;
    if (days < 14) return "last week";
    return then.toLocaleDateString("en-GB", { day: "numeric", month: "short" });
}

/** The words for where a rating came from, when it's worth saying. */
export const cookedAfter = (dish: RatedDish): boolean =>
    dish.ratings.some((r) => r.context === "afterCooking");

export const CONTEXT_FOR: Record<RatingKind, RatingContext> = {
    recipe: "suggested",
    readyNow: "readyNow",
    plannedMeal: "plannedMeal",
};
