import type { ApiClient, RatedDish, Rating, RatingBody } from "./api";
import { dishKey, normalizeTitle } from "./ratings";

/** Test data for ratings (#128): a small in-memory stand-in for the four ratings endpoints. Made-up dishes and names. */

export const rating = (overrides: Partial<Rating> & Pick<Rating, "id" | "title">): Rating => ({
    rating: "up",
    reasons: [],
    dislikedIngredients: [],
    context: "suggested",
    ratedAt: "2026-11-08T08:00:00.000Z",
    updatedAt: "2026-11-08T08:00:00.000Z",
    personName: "Gary",
    mine: true,
    kind: "recipe",
    key: normalizeTitle(overrides.title),
    meal: "dinner",
    diet: "any",
    totalMinutes: 40,
    ingredients: [],
    equipment: [],
    savedRecipeId: null,
    planId: null,
    mealId: null,
    ...overrides,
});

/** The server's grouping: one dish per kind and title, the dish with the newest rating first, ratings newest first. */
export function dishesOf(ratings: Rating[]): RatedDish[] {
    const by = new Map<string, Rating[]>();
    for (const r of ratings)
        by.set(dishKey(r.kind, r.title), [...(by.get(dishKey(r.kind, r.title)) ?? []), r]);
    return [...by.values()]
        .map((rs) => {
            const sorted = [...rs].sort((a, b) => b.ratedAt.localeCompare(a.ratedAt));
            const first = sorted[0];
            return {
                kind: first.kind,
                key: first.key,
                title: first.title,
                meal: first.meal,
                diet: first.diet,
                totalMinutes: first.totalMinutes,
                latestAt: first.ratedAt,
                ratings: sorted,
            };
        })
        .sort((a, b) => b.latestAt.localeCompare(a.latestAt));
}

/** The ratings endpoints, kept in memory. `me` is the signed-in person's name. */
export function fakeRatingsApi(initial: Rating[] = [], me = "Gary") {
    const store = { ratings: [...initial] };
    const api = {
        ratings: vi.fn(async () => ({ total: store.ratings.length, dishes: dishesOf(store.ratings) })),
        putRating: vi.fn(async (id: string, body: RatingBody) => {
            const saved: Rating = rating({
                id,
                title: body.title,
                rating: body.rating,
                reasons: body.reasons ?? [],
                dislikedIngredients: body.dislikedIngredients ?? [],
                context: body.context,
                kind: body.kind,
                meal: body.meal,
                diet: body.diet ?? "any",
                totalMinutes: body.totalMinutes,
                ingredients: body.ingredients.map((i) => ({
                    name: i.name,
                    genericName: i.genericName ?? null,
                })),
                equipment: body.equipment ?? [],
                personName: me,
                mine: true,
                ratedAt: new Date().toISOString(),
            });
            store.ratings = [
                saved,
                ...store.ratings.filter(
                    (r) =>
                        !(
                            r.mine &&
                            (r.id === id || dishKey(r.kind, r.title) === dishKey(saved.kind, saved.title))
                        ),
                ),
            ];
            return saved;
        }),
        updateRating: vi.fn(
            async (id: string, change: { reasons: Rating["reasons"]; dislikedIngredients: string[] }) => {
                store.ratings = store.ratings.map((r) => (r.id === id ? { ...r, ...change } : r));
                return store.ratings.find((r) => r.id === id)!;
            },
        ),
        deleteRating: vi.fn(async (id: string) => {
            store.ratings = store.ratings.filter((r) => r.id !== id);
        }),
    };
    return Object.assign(api, { store, asClient: () => api as unknown as ApiClient });
}
