import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { ApiError, type ApiClient, type MealPlan, type PlanMeal, type StockProduct } from "./api";
import { PlanChooseScreen } from "./PlanChooseScreen";
import { PlanScreen } from "./PlanScreen";
import { mealsText, planError, planLabel, planSummary, sharesText } from "./plans";

beforeEach(() => window.history.replaceState(null, "", "/plan"));

const asApi = (api: object) => api as unknown as ApiClient;

const product = (name: string, days: number | null, useFirst: boolean): StockProduct =>
    ({
        productId: name,
        displayName: name,
        status: "known",
        useFirst,
        daysUntilSoonest: days,
        soonestEstimatedExpiryDate: days === null ? null : "2026-10-29",
    }) as unknown as StockProduct;

const meal = (
    id: string,
    type: PlanMeal["type"],
    title: string,
    extra: Partial<PlanMeal> = {},
): PlanMeal => ({
    id,
    type,
    title,
    summary: `${title}, simply.`,
    servings: 2,
    uses: [],
    usesUp: [],
    toBuy: [],
    keep: false,
    warnings: [],
    hasRecipe: false,
    usesCount: 2,
    shares: null,
    useFirst: [],
    ...extra,
});

const plan = (over: Partial<MealPlan> = {}): MealPlan => ({
    id: "plan-1",
    createdBy: "u1",
    createdByName: "Gary",
    createdAt: "2026-10-28T18:00:00.000Z",
    updatedAt: "2026-10-28T18:00:00.000Z",
    status: "ready",
    error: null,
    choices: {
        days: 3,
        mealTypes: ["lunch", "dinner"],
        diet: "any",
        equipment: null,
        preferUseFirst: false,
        comments: null,
    },
    addedToListAt: null,
    meals: [
        meal("lunch-1", "lunch", "Chicken wraps", {
            shares: { names: ["peppers", "spinach", "feta"], otherMeals: 2 },
        }),
        meal("lunch-2", "lunch", "Tomato soup", { usesCount: 1 }),
        meal("dinner-1", "dinner", "Fajita traybake", {
            shares: { names: ["peppers"], otherMeals: 1 },
            useFirst: ["chicken"],
        }),
    ],
    ...over,
});

describe("Plan meals: choose", () => {
    const choose = (
        stock: StockProduct[] = [
            product("Milk", 2, true),
            product("Chicken breast", 1, true),
            product("Rice", 300, false),
        ],
        extra: object = {},
    ) => {
        const api = {
            stock: vi.fn(async () => ({ generatedAt: "", totalUnits: 3, useFirstCount: 2, products: stock })),
            createMealPlan: vi.fn(async () => plan({ status: "generating", meals: [] })),
            ...extra,
        };
        render(<PlanChooseScreen api={asApi(api)} />);
        return api;
    };

    test("the defaults: 3 days, dinner, Meat & veg, all kit, prefer use first off", async () => {
        choose();

        expect(screen.getByRole("heading", { level: 1, name: "Plan meals" })).toBeInTheDocument();
        expect(screen.getByText("Plan meals · Step 1 of 3")).toBeInTheDocument();
        expect(screen.getByText("3 days")).toBeInTheDocument();
        expect(screen.getByRole("checkbox", { name: /Dinner/ })).toBeChecked();
        expect(screen.getByRole("checkbox", { name: /Dinner/ })).toHaveTextContent("");
        expect(screen.getByRole("radio", { name: "Meat & veg" })).toBeChecked();
        expect(screen.getByText("9 of 9")).toBeInTheDocument();
        expect(
            await screen.findByRole("checkbox", { name: "Prefer ingredients that need using" }),
        ).not.toBeChecked();
        expect(screen.getByRole("button", { name: "Plan 3 meals" })).toBeEnabled();
        expect(screen.getByText("Meals aren't tied to days. Make them in any order.")).toBeInTheDocument();
    });

    test("the stepper goes from 1 to 7 and the counts follow", () => {
        choose();
        const fewer = screen.getByRole("button", { name: "One day fewer" });
        const more = screen.getByRole("button", { name: "One more day" });

        for (let i = 0; i < 5; i++) fireEvent.click(fewer);
        expect(screen.getByText("1 day")).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Plan 1 meal" })).toBeInTheDocument();
        expect(screen.getByText("1 meal")).toBeInTheDocument();
        expect(fewer).toHaveAttribute("aria-disabled", "true");
        for (let i = 0; i < 9; i++) fireEvent.click(more);
        expect(screen.getByText("7 days")).toBeInTheDocument();
        expect(more).toHaveAttribute("aria-disabled", "true");
        fireEvent.click(screen.getByRole("checkbox", { name: /Lunch/ }));
        expect(screen.getByRole("button", { name: "Plan 14 meals" })).toBeInTheDocument();
    });

    test("at least one meal must be ticked", () => {
        choose();
        fireEvent.click(screen.getByRole("checkbox", { name: /Dinner/ }));

        const button = screen.getByRole("button", { name: "Pick at least one meal" });
        expect(button).toBeDisabled();
        fireEvent.click(screen.getByRole("checkbox", { name: /Breakfast/ }));
        expect(screen.getByRole("button", { name: "Plan 3 meals" })).toBeEnabled();
    });

    test("meat or veg has its own help, and the kit chips toggle with a count; none ticked can't plan", () => {
        choose();
        fireEvent.click(screen.getByRole("radio", { name: "Veggie" }));
        expect(screen.getByText("No meat or fish, and nothing cooked with them.")).toBeInTheDocument();
        fireEvent.click(screen.getByRole("radio", { name: "Half & half" }));
        expect(screen.getByRole("radio", { name: "Half & half" })).toBeChecked();

        fireEvent.click(screen.getByRole("button", { name: "Oven & grill" }));
        expect(screen.getByText("8 of 9")).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Oven & grill" })).toHaveAttribute("aria-pressed", "false");
        for (const kit of [
            "Hob",
            "Microwave",
            "Toaster",
            "Air fryer",
            "Slow cooker",
            "Rice cooker",
            "Soup maker",
            "Blender",
        ])
            fireEvent.click(screen.getByRole("button", { name: kit }));
        expect(screen.getByText("0 of 9")).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Plan 3 meals" })).toBeDisabled();
        expect(screen.getByText("Tick at least one thing you can use.")).toBeInTheDocument();
    });

    test("prefer use first is off by default, shows the use-first things as amber chips when ticked, and is off when nothing needs using", async () => {
        choose();
        const box = await screen.findByRole("checkbox", { name: "Prefer ingredients that need using" });
        expect(box).toBeEnabled();
        expect(screen.getByText("Chooses freely from what you have.")).toBeInTheDocument();
        expect(screen.queryByText(/Chicken breast ·/)).toBeNull();

        fireEvent.click(box);

        expect(screen.getByText("Plans meals around the 2 things to use first.")).toBeInTheDocument();
        expect(screen.getByText(/^Milk · /)).toBeInTheDocument();
        expect(screen.getByText(/^Chicken breast · /)).toBeInTheDocument();
        expect(screen.queryByText(/^Rice · /)).toBeNull();
    });

    test("with nothing to use first the checkbox is off and says so", async () => {
        choose([product("Rice", 300, false)]);

        await waitFor(() =>
            expect(
                screen.getByRole("checkbox", { name: "Prefer ingredients that need using" }),
            ).toBeDisabled(),
        );
        expect(screen.getByText("Nothing needs using up right now.")).toBeInTheDocument();
    });

    test("Plan sends the choices, with prefer use first off when it isn't ticked, and opens the plan", async () => {
        const api = choose();
        await screen.findByRole("checkbox", { name: "Prefer ingredients that need using" });
        fireEvent.click(screen.getByRole("button", { name: "One day fewer" }));
        fireEvent.click(screen.getByRole("checkbox", { name: /Lunch/ }));
        fireEvent.click(screen.getByRole("radio", { name: "Veggie" }));
        fireEvent.change(screen.getByLabelText("Anything else?"), { target: { value: "  quick lunches  " } });
        fireEvent.click(screen.getByRole("button", { name: "Plan 4 meals" }));

        await waitFor(() => expect(api.createMealPlan).toHaveBeenCalledTimes(1));
        expect(api.createMealPlan).toHaveBeenCalledWith({
            days: 2,
            meals: ["lunch", "dinner"],
            diet: "veg",
            equipment: null,
            preferUseFirst: false,
            comments: "quick lunches",
        });
        await waitFor(() => expect(window.location.pathname).toBe("/plan/plan-1"));
    });

    test("ticked prefer use first and an unticked kit are sent as chosen", async () => {
        const api = choose();
        fireEvent.click(await screen.findByRole("checkbox", { name: "Prefer ingredients that need using" }));
        fireEvent.click(screen.getByRole("button", { name: "Slow cooker" }));
        fireEvent.click(screen.getByRole("button", { name: "Plan 3 meals" }));

        await waitFor(() => expect(api.createMealPlan).toHaveBeenCalled());
        const sent = (
            api.createMealPlan.mock.calls as unknown as Array<
                [{ preferUseFirst: boolean; equipment: string[]; comments: string | null }]
            >
        )[0][0];
        expect(sent.preferUseFirst).toBe(true);
        expect(sent.equipment).toEqual([
            "hob",
            "ovenGrill",
            "microwave",
            "toaster",
            "airFryer",
            "riceCooker",
            "soupMaker",
            "blender",
        ]);
        expect(sent.comments).toBeNull();
    });

    test("a refusal (the limit, nothing in stock) is shown in the API's words and the form stays", async () => {
        const api = choose(undefined, {
            createMealPlan: vi.fn(async () => {
                throw new ApiError(429, "x", {
                    error: "That's a lot of planning for one hour. Try again a bit later.",
                });
            }),
        });
        fireEvent.click(screen.getByRole("button", { name: "Plan 3 meals" }));

        expect(await screen.findByRole("alert")).toHaveTextContent(
            "That's a lot of planning for one hour. Try again a bit later.",
        );
        expect(screen.getByRole("button", { name: "Plan 3 meals" })).toBeEnabled();
        expect(api.createMealPlan).toHaveBeenCalledTimes(1);
        expect(window.location.pathname).toBe("/plan");
    });

    test("comments stop at 300 characters and the count shows near the end", () => {
        choose();
        const box = screen.getByLabelText("Anything else?");

        expect(box).toHaveAttribute("maxlength", "300");
        fireEvent.change(box, { target: { value: "x".repeat(250) } });
        expect(screen.getByText("250 / 300")).toBeInTheDocument();
    });
});

describe("Plan meals: the plan", () => {
    const open = (initial: MealPlan = plan(), overrides: Record<string, unknown> = {}, pollMs = 5) => {
        let current = initial;
        const change = (next: MealPlan) => {
            current = next;
            return next;
        };
        const api = {
            mealPlan: vi.fn(async () => current),
            adjustMealPlan: vi.fn(async () =>
                change({
                    ...current,
                    meals: current.meals.map((m) => (m.keep ? m : { ...m, title: "Cheaper " + m.title })),
                }),
            ),
            adjustPlannedMeal: vi.fn(async (_p: string, id: string) =>
                change({
                    ...current,
                    meals: current.meals.map((m) =>
                        m.id === id ? { ...m, title: "Spicier " + m.title } : m,
                    ),
                }),
            ),
            anotherPlannedMeal: vi.fn(async (_p: string, id: string) =>
                change({
                    ...current,
                    meals: current.meals.map((m) => (m.id === id ? { ...m, title: "Something else" } : m)),
                }),
            ),
            addPlannedMeal: vi.fn(async (_p: string, type: string) =>
                change({
                    ...current,
                    meals: [...current.meals, meal("new-1", type as PlanMeal["type"], "A new " + type)],
                }),
            ),
            keepPlannedMeal: vi.fn(async (_p: string, id: string, keep: boolean) =>
                change({ ...current, meals: current.meals.map((m) => (m.id === id ? { ...m, keep } : m)) }),
            ),
            deletePlannedMeal: vi.fn(async (_p: string, id: string) => {
                const gone = current.meals.find((m) => m.id === id)!;
                return {
                    plan: change({ ...current, meals: current.meals.filter((m) => m.id !== id) }),
                    meal: { ...gone },
                };
            }),
            restorePlannedMeal: vi.fn(async (_p: string, m: { id: string }) =>
                change({
                    ...current,
                    meals: [...current.meals, current.meals[0] && (m as unknown as PlanMeal)],
                }),
            ),
            ...overrides,
        };
        render(<PlanScreen api={asApi(api)} planId="plan-1" pollMs={pollMs} />);
        return api;
    };

    test("while the plan is being written it says Planning N meals… and then shows the plan when it's ready", async () => {
        let polls = 0;
        const generating = plan({ status: "generating", meals: [] });
        const api = open(generating, {
            mealPlan: vi.fn(async () => (++polls < 3 ? generating : plan())),
        });

        expect(await screen.findByRole("status")).toHaveTextContent("Planning 6 meals…");
        expect(await screen.findByRole("heading", { level: 1, name: "Your meals" })).toBeInTheDocument();
        await screen.findByRole("article", { name: "Chicken wraps" });
        expect(api.mealPlan.mock.calls.length).toBeGreaterThanOrEqual(3);
    });

    test("meals are grouped by type with the summary, facts and a Keep toggle, as in the mockup", async () => {
        open();

        expect(
            await screen.findByText("1 dinner · 2 lunches · 3 things to buy are shared"),
        ).toBeInTheDocument();
        expect(screen.getByText("Plan meals · Step 2 of 3")).toBeInTheDocument();
        const sections = screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
        expect(sections).toEqual(["Lunch2 meals", "Dinner1 meal"]);
        const wraps = screen.getByRole("article", { name: "Chicken wraps" });
        expect(within(wraps).getByText("Uses 2 things you have")).toBeInTheDocument();
        expect(within(wraps).getByText("Shares peppers, spinach with 2 other meals")).toBeInTheDocument();
        expect(
            within(screen.getByRole("article", { name: "Tomato soup" })).getByText("Uses 1 thing you have"),
        ).toBeInTheDocument();
        expect(within(screen.getByRole("article", { name: "Tomato soup" })).queryByText(/Shares/)).toBeNull();
        expect(within(wraps).getByRole("button", { name: "Keep" })).toHaveAttribute("aria-pressed", "false");
        expect(screen.getByRole("button", { name: "Add a lunch" })).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Add a dinner" })).toBeInTheDocument();
        expect(screen.queryByRole("link", { name: /what to buy/i })).toBeNull(); // #125 isn't built yet
    });

    test("a Use first chip appears only for a meal that carries one", async () => {
        open();

        const dinner = await screen.findByRole("article", { name: "Fajita traybake" });
        expect(within(dinner).getByText("Use first: chicken")).toBeInTheDocument();
        expect(
            within(screen.getByRole("article", { name: "Chicken wraps" })).queryByText(/Use first/),
        ).toBeNull();
    });

    test("diet and kit warnings show as Check before cooking", async () => {
        open(
            plan({
                meals: [
                    meal("dinner-1", "dinner", "Beef chilli", {
                        warnings: ["Uses Beef mince, which is meat or fish."],
                    }),
                ],
            }),
        );

        const card = await screen.findByRole("article", { name: "Beef chilli" });
        expect(within(card).getByText("Check before cooking")).toBeInTheDocument();
        expect(within(card).getByText("Uses Beef mince, which is meat or fish.")).toBeInTheDocument();
    });

    test("Keep toggles the meal on the server", async () => {
        const api = open();
        const wraps = await screen.findByRole("article", { name: "Chicken wraps" });

        fireEvent.click(within(wraps).getByRole("button", { name: "Keep" }));

        await waitFor(() => expect(api.keepPlannedMeal).toHaveBeenCalledWith("plan-1", "lunch-1", true));
        await waitFor(() =>
            expect(
                within(screen.getByRole("article", { name: "Chicken wraps" })).getByRole("button", {
                    name: "Keep",
                }),
            ).toHaveAttribute("aria-pressed", "true"),
        );
    });

    test("Adjust asks what to change and sends it; Cancel closes it", async () => {
        const api = open();
        const soup = await screen.findByRole("article", { name: "Tomato soup" });
        fireEvent.click(within(soup).getByRole("button", { name: "Adjust" }));
        expect(within(soup).getByLabelText("What would you change?")).toHaveFocus();
        fireEvent.click(within(soup).getByRole("button", { name: "Cancel" }));
        expect(within(soup).queryByLabelText("What would you change?")).toBeNull();

        fireEvent.click(within(soup).getByRole("button", { name: "Adjust" }));
        expect(within(soup).getAllByRole("button", { name: "Adjust" })[0]).toBeDisabled(); // the confirm button: nothing typed yet
        fireEvent.change(within(soup).getByLabelText("What would you change?"), {
            target: { value: " make it spicier " },
        });
        fireEvent.click(within(soup).getAllByRole("button", { name: "Adjust" })[0]);

        await waitFor(() =>
            expect(api.adjustPlannedMeal).toHaveBeenCalledWith("plan-1", "lunch-2", "make it spicier"),
        );
        expect(await screen.findByRole("article", { name: "Spicier Tomato soup" })).toBeInTheDocument();
        expect(screen.queryByLabelText("What would you change?")).toBeNull();
    });

    test("Another one replaces the meal", async () => {
        const api = open();

        fireEvent.click(
            within(await screen.findByRole("article", { name: "Fajita traybake" })).getByRole("button", {
                name: "Another one",
            }),
        );

        await waitFor(() => expect(api.anotherPlannedMeal).toHaveBeenCalledWith("plan-1", "dinner-1"));
        expect(await screen.findByRole("article", { name: "Something else" })).toBeInTheDocument();
    });

    test("Add a dinner adds one under Dinner", async () => {
        const api = open();

        fireEvent.click(await screen.findByRole("button", { name: "Add a dinner" }));

        await waitFor(() => expect(api.addPlannedMeal).toHaveBeenCalledWith("plan-1", "dinner"));
        expect(await screen.findByRole("article", { name: "A new dinner" })).toBeInTheDocument();
        expect(screen.getByText("Dinner").closest("h2")).toHaveTextContent("2 meals");
    });

    test("Delete removes a meal with a toast and Undo puts it back", async () => {
        const api = open();

        fireEvent.click(await screen.findByRole("button", { name: "Delete Tomato soup" }));

        await waitFor(() => expect(api.deletePlannedMeal).toHaveBeenCalledWith("plan-1", "lunch-2"));
        expect(screen.queryByRole("article", { name: "Tomato soup" })).toBeNull();
        expect(await screen.findByRole("status", { name: "" })).toHaveTextContent("Deleted Tomato soup");
        fireEvent.click(screen.getByRole("button", { name: "Undo" }));

        await waitFor(() => expect(api.restorePlannedMeal).toHaveBeenCalledTimes(1));
        expect(api.restorePlannedMeal.mock.calls[0][1]).toMatchObject({
            id: "lunch-2",
            title: "Tomato soup",
        });
        await waitFor(() => expect(screen.queryByText("Deleted Tomato soup")).toBeNull());
    });

    test("Adjust the whole plan sends the wish and keeps the kept meals", async () => {
        const api = open(
            plan({
                meals: [
                    meal("dinner-1", "dinner", "Kept dinner", { keep: true }),
                    meal("dinner-2", "dinner", "Other dinner"),
                ],
            }),
        );
        await screen.findByRole("article", { name: "Kept dinner" });

        const form = screen.getByRole("textbox", { name: "Adjust the whole plan" });
        expect(screen.getByText("Meals you've marked Keep stay as they are.")).toBeInTheDocument();
        fireEvent.change(form, { target: { value: "cheaper" } });
        fireEvent.click(within(form.closest("form")!).getByRole("button", { name: "Adjust" }));

        await waitFor(() => expect(api.adjustMealPlan).toHaveBeenCalledWith("plan-1", "cheaper"));
        expect(await screen.findByRole("article", { name: "Cheaper Other dinner" })).toBeInTheDocument();
        expect(screen.getByRole("article", { name: "Kept dinner" })).toBeInTheDocument();
        expect(form).toHaveValue("");
    });

    test("a failed change leaves the plan as it was and says why", async () => {
        const api = open(plan(), {
            anotherPlannedMeal: vi.fn(async () => {
                throw new ApiError(503, "x", { error: "Busy right now, try again in a minute." });
            }),
        });

        fireEvent.click(
            within(await screen.findByRole("article", { name: "Fajita traybake" })).getByRole("button", {
                name: "Another one",
            }),
        );

        expect(await screen.findByRole("alert")).toHaveTextContent("Busy right now, try again in a minute.");
        expect(screen.getByRole("article", { name: "Fajita traybake" })).toBeInTheDocument();
        expect(api.anotherPlannedMeal).toHaveBeenCalledTimes(1);
        expect(
            within(screen.getByRole("article", { name: "Fajita traybake" })).getByRole("button", {
                name: "Another one",
            }),
        ).toBeEnabled();
    });

    test("while a change runs the buttons are off", async () => {
        let release: () => void = () => undefined;
        const api = open(plan(), {
            anotherPlannedMeal: vi.fn(
                () => new Promise<MealPlan>((resolve) => (release = () => resolve(plan()))),
            ),
        });
        fireEvent.click(
            within(await screen.findByRole("article", { name: "Fajita traybake" })).getByRole("button", {
                name: "Another one",
            }),
        );

        expect(await screen.findByText("Working on it…")).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Add a dinner" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "Delete Tomato soup" })).toBeDisabled();
        await act(async () => release());
        await waitFor(() => expect(screen.queryByText("Working on it…")).toBeNull());
        expect(api.anotherPlannedMeal).toHaveBeenCalledTimes(1);
    });

    test("a plan that couldn't be written says so and goes back", async () => {
        open(plan({ status: "failed", meals: [], error: "Couldn't plan just now. Try again." }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Couldn't plan just now. Try again.");
        window.history.pushState({ from: "/plan" }, "", "/plan/plan-1");
        fireEvent.click(screen.getByRole("button", { name: "Back to choices" }));
        await waitFor(() => expect(window.location.pathname).toBe("/plan"));
    });

    test("a plan that's gone, or can't be loaded, says so", async () => {
        open(plan(), {
            mealPlan: vi.fn(async () => {
                throw new ApiError(404, "x");
            }),
        });
        expect(await screen.findByRole("alert")).toHaveTextContent("That plan isn't there any more.");
    });

    test("a load failure can be retried", async () => {
        const mealPlan = vi.fn().mockRejectedValueOnce(new Error("down")).mockResolvedValue(plan());
        open(plan(), { mealPlan });

        expect(await screen.findByRole("alert")).toHaveTextContent("Couldn't load the plan.");
        fireEvent.click(screen.getByRole("button", { name: "Try again" }));
        expect(await screen.findByRole("article", { name: "Chicken wraps" })).toBeInTheDocument();
    });
});

describe("helpers", () => {
    test("wording", () => {
        expect(mealsText(1)).toBe("1 meal");
        expect(mealsText(6)).toBe("6 meals");
        expect(planLabel(1)).toBe("Plan 1 meal");
        expect(planError(new ApiError(429, "x", { error: "Slow down." }), "fallback")).toBe("Slow down.");
        expect(planError(new Error("x"), "fallback")).toBe("fallback");
        expect(sharesText(meal("a", "dinner", "A"))).toBeNull();
        expect(
            sharesText(meal("a", "dinner", "A", { shares: { names: ["a", "b", "c"], otherMeals: 1 } })),
        ).toBe("Shares a, b with 1 other meal");
    });

    test("the summary counts meals by type and the things shared across meals", () => {
        expect(planSummary(plan())).toBe("1 dinner · 2 lunches · 3 things to buy are shared"); // peppers, spinach, feta
        expect(planSummary(plan({ meals: [meal("b", "breakfast", "Porridge")] }))).toBe("1 breakfast");
    });
});
