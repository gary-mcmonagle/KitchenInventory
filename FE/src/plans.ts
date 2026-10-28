import { ApiError, type MealPlan, type PlanMeal, type PlanMealType } from "./api";

/** Plan meals (#119): wording and small calculations shared by the two screens. */

export const MEAL_TYPES: ReadonlyArray<{ id: PlanMealType; title: string; word: string; plural: string }> = [
    { id: "breakfast", title: "Breakfast", word: "breakfast", plural: "breakfasts" },
    { id: "lunch", title: "Lunch", word: "lunch", plural: "lunches" },
    { id: "dinner", title: "Dinner", word: "dinner", plural: "dinners" },
];

export const MAX_COMMENTS = 300;

/** "1 meal", "3 meals". */
export const mealsText = (n: number) => `${n} ${n === 1 ? "meal" : "meals"}`;

/** "Plan 6 meals", or "Plan 1 meal". */
export const planLabel = (n: number) => `Plan ${mealsText(n)}`;

/** The plan's meals of one type, in the plan's order. */
export const mealsOf = (plan: MealPlan, type: PlanMealType): PlanMeal[] =>
    plan.meals.filter((m) => m.type === type);

/** "3 dinners · 2 lunches · 5 things to buy are shared": counts by type, then how many things to buy two or more meals share. */
export function planSummary(plan: MealPlan): string {
    const parts = [...MEAL_TYPES].reverse().flatMap((t) => {
        const n = mealsOf(plan, t.id).length;
        return n > 0 ? [`${n} ${n === 1 ? t.word : t.plural}`] : [];
    });
    const shared = new Set(plan.meals.flatMap((m) => m.shares?.names.map((n) => n.toLowerCase()) ?? [])).size;
    if (shared > 0)
        parts.push(
            `${shared} ${shared === 1 ? "thing" : "things"} to buy ${shared === 1 ? "is" : "are"} shared`,
        );
    return parts.join(" · ");
}

/** "Shares peppers, onions with 2 other meals": up to two names. */
export function sharesText(meal: PlanMeal): string | null {
    if (!meal.shares) return null;
    const n = meal.shares.otherMeals;
    return `Shares ${meal.shares.names.slice(0, 2).join(", ")} with ${n} other ${n === 1 ? "meal" : "meals"}`;
}

/** The message to show for a failed call: the API's own words (a limit, busy, nothing in stock…), else a generic one. */
export function planError(error: unknown, fallback: string): string {
    if (
        error instanceof ApiError &&
        error.body &&
        typeof (error.body as { error?: unknown }).error === "string"
    ) {
        return (error.body as { error: string }).error;
    }
    return fallback;
}
