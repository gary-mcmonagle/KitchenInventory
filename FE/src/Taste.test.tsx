import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ApiClient, Recipe, StockProduct } from "./api";
import { fakeRatingsApi, rating } from "./ratingsFixture";
import { forgetScrollPositions } from "./router";
import { Shell } from "./Shell";

// Our taste in the app (#128): opens over Hungry?, so ‹ comes back to the recipe as it was.

const STOCK: StockProduct[] = [
    {
        productId: "p-cheese",
        barcode: null,
        displayName: "Mature cheddar",
        brand: null,
        size: null,
        count: 1,
        latestCheckedInAt: "2026-10-20T08:00:00.000Z",
        status: "known",
        section: "later",
        soonestEstimatedExpiryDate: null,
        daysUntilSoonest: null,
        shelfLifeDays: null,
        openedCount: 0,
        frozenCount: 0,
        soonestState: null,
        soonestStateOn: null,
        aisleId: "dairyEggs",
        icon: null,
        useFirst: false,
        genericName: null,
    },
];

const RECIPE: Recipe = {
    title: "Cheese toastie",
    summary: "Quick.",
    meal: "lunch",
    servings: 1,
    totalMinutes: 10,
    ingredients: [{ name: "Mature cheddar", amount: "50 g", productId: "p-cheese", useFirst: false }],
    steps: [{ text: "Toast it.", for: "all" }],
    usesUp: [],
    alsoNeeded: [],
    couldNotFitIn: [],
};

function shellApi() {
    return {
        ...fakeRatingsApi([
            rating({ id: "a", title: "Chicken fajita traybake" }),
            rating({
                id: "e",
                title: "Mushroom risotto",
                rating: "down",
                personName: "Sam",
                mine: false,
                reasons: ["notFilling"],
            }),
        ]),
        me: async () => ({ name: "Cupboard test", objectId: "o" }),
        ping: async () => ({ status: "ok", commit: "abc1234" }),
        stock: vi.fn(async () => ({ generatedAt: "", totalUnits: 1, useFirstCount: 0, products: STOCK })),
        suggestRecipe: vi.fn(async () => ({ available: true, mode: "cook", recipe: RECIPE })),
        savedRecipes: async () => ({ savedRecipes: [] }),
    };
}

beforeEach(() => {
    window.history.replaceState(null, "", "/recipes");
    forgetScrollPositions();
    window.scrollTo = vi.fn() as unknown as typeof window.scrollTo;
});

test("Our taste opens from Hungry? over the form, shows the household's ratings, and ‹ comes back to the form as it was", async () => {
    const api = shellApi();
    render(<Shell api={api as unknown as ApiClient} buildSha="def5678" />);
    fireEvent.click(await screen.findByRole("radio", { name: "Breakfast" }));
    const row = await screen.findByRole("link", { name: /Our taste/ });
    await waitFor(() => expect(row).toHaveTextContent("2 ratings"));

    fireEvent.click(row);
    await act(async () => undefined);

    expect(window.location.pathname).toBe("/taste");
    expect(await screen.findByRole("heading", { level: 1, name: "Our taste" })).toBeInTheDocument();
    expect(await screen.findByText("Chicken fajita traybake")).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /^Liked · 1/ })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /^Not for us · 1/ })).toBeInTheDocument();
    expect(screen.getByRole("navigation")).toBeInTheDocument(); // the bottom tabs, with Hungry? current

    await act(async () => {
        fireEvent.click(screen.getByRole("link", { name: "Back to Hungry?" }));
        await new Promise((r) => setTimeout(r, 30));
    });

    await waitFor(() => expect(window.location.pathname).toBe("/recipes"));
    expect(screen.getByRole("radio", { name: "Breakfast" })).toBeChecked(); // the form was kept, not rebuilt
});

test("opened directly, /taste shows Our taste and ‹ goes to Hungry?", async () => {
    window.history.replaceState(null, "", "/taste");
    render(<Shell api={shellApi() as unknown as ApiClient} buildSha="def5678" />);

    expect(await screen.findByRole("heading", { level: 1, name: "Our taste" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("link", { name: "Back to Hungry?" }));
    await act(async () => undefined);

    expect(window.location.pathname).toBe("/recipes");
});
