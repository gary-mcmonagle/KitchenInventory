import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { OurTasteScreen } from "./OurTasteScreen";
import { fakeRatingsApi, rating } from "./ratingsFixture";
import { RatingsProvider } from "./useRatings";

// Our taste (#128): project/OurTaste.dc.html.

const NOW = new Date("2026-11-08T17:00:00.000Z");

const RATINGS = [
    rating({
        id: "a",
        title: "Chicken fajita traybake",
        context: "afterCooking",
        ratedAt: "2026-11-06T18:00:00.000Z",
    }),
    rating({
        id: "a2",
        title: "Chicken fajita traybake",
        personName: "Sam",
        mine: false,
        ratedAt: "2026-11-06T17:00:00.000Z",
    }),
    rating({
        id: "b",
        title: "Beans on toast",
        meal: "snack",
        kind: "readyNow",
        ratedAt: "2026-11-01T09:00:00.000Z",
    }),
    rating({
        id: "c",
        title: "Smoky bean chilli",
        personName: "Sam",
        mine: false,
        context: "afterCooking",
        ratedAt: "2026-11-02T09:00:00.000Z",
    }),
    rating({
        id: "e",
        title: "Mushroom risotto",
        personName: "Sam",
        mine: false,
        rating: "down",
        reasons: ["ingredient"],
        dislikedIngredients: ["mushrooms"],
        ratedAt: "2026-11-08T09:00:00.000Z",
    }),
    rating({
        id: "f",
        title: "Slow-cooked beef ragu",
        rating: "down",
        reasons: ["tookTooLong"],
        ratedAt: "2026-11-05T09:00:00.000Z",
    }),
    rating({ id: "g", title: "Thai green curry", rating: "up", ratedAt: "2026-11-02T20:00:00.000Z" }),
    rating({
        id: "g2",
        title: "Thai green curry",
        personName: "Sam",
        mine: false,
        rating: "down",
        reasons: ["tooSpicy"],
        ratedAt: "2026-11-03T20:00:00.000Z",
    }),
];

function show(api = fakeRatingsApi(RATINGS)) {
    render(
        <RatingsProvider api={api.asClient()}>
            <OurTasteScreen now={NOW} />
        </RatingsProvider>,
    );
    return api;
}

const tab = (name: RegExp) => screen.getByRole("tab", { name });
const titles = () =>
    within(screen.getByRole("tabpanel"))
        .queryAllByRole("listitem")
        .map((li) => li.querySelector("span")!.textContent);

beforeEach(() => {
    window.history.replaceState({}, "", "/taste");
});

test("two tabs with counts, newest first; a dish people disagree about is in both", async () => {
    show();

    await waitFor(() => expect(tab(/^Liked/)).toHaveTextContent("Liked · 4"));
    expect(tab(/^Not for us/)).toHaveTextContent("Not for us · 3");
    expect(tab(/^Liked/)).toHaveAttribute("aria-selected", "true");
    expect(titles()).toEqual([
        "Chicken fajita traybake",
        "Thai green curry",
        "Smoky bean chilli",
        "Beans on toast",
    ]);

    fireEvent.click(tab(/^Not for us/));

    expect(titles()).toEqual(["Mushroom risotto", "Slow-cooked beef ragu", "Thai green curry"]);
});

test("each row has the title, meal, when, who rated it, the reasons, and After cooking", async () => {
    show();
    await screen.findByRole("tab", { name: /^Liked/ });
    await waitFor(() => expect(titles().length).toBeGreaterThan(0));

    const fajita = screen.getByText("Chicken fajita traybake").closest("li")!;
    expect(within(fajita).getByText("After cooking")).toBeInTheDocument();
    expect(within(fajita).getByText("Dinner · 2 days ago")).toBeInTheDocument();
    expect(within(fajita).getByText("Gary 👍 · Sam 👍")).toBeInTheDocument();
    expect(screen.getByText("Beans on toast").closest("li")).toHaveTextContent("Snack · last week");
    expect(
        within(screen.getByText("Beans on toast").closest("li")!).queryByText("After cooking"),
    ).not.toBeInTheDocument();

    fireEvent.click(tab(/^Not for us/));
    expect(screen.getByText("Mushroom risotto").closest("li")).toHaveTextContent("Didn't like: mushrooms");
    expect(screen.getByText("Mushroom risotto").closest("li")).toHaveTextContent("Sam 👎");
    expect(screen.getByText("Slow-cooked beef ragu").closest("li")).toHaveTextContent("Took too long");
    const curry = screen.getByText("Thai green curry").closest("li")!;
    expect(curry).toHaveTextContent("Sam 👎 · Gary 👍");
    expect(curry).toHaveTextContent("Too spicy");
});

test("you can remove your own rating, not Sam's", async () => {
    show();
    await waitFor(() => expect(titles().length).toBeGreaterThan(0));

    expect(screen.getByRole("button", { name: "Remove your rating of Beans on toast" })).toBeInTheDocument();
    expect(
        screen.queryByRole("button", { name: "Remove your rating of Smoky bean chilli" }),
    ).not.toBeInTheDocument();
    fireEvent.click(tab(/^Not for us/));
    expect(
        screen.queryByRole("button", { name: "Remove your rating of Mushroom risotto" }),
    ).not.toBeInTheDocument();
});

test("removing says so with Undo, which puts the same rating back", async () => {
    const api = show();
    await waitFor(() => expect(titles().length).toBeGreaterThan(0));

    fireEvent.click(screen.getByRole("button", { name: "Remove your rating of Beans on toast" }));

    await waitFor(() => expect(screen.queryByText("Beans on toast")).not.toBeInTheDocument());
    expect(api.deleteRating).toHaveBeenCalledWith("b");
    expect(screen.getByRole("status")).toHaveTextContent("Removed your rating");
    expect(tab(/^Liked/)).toHaveTextContent("Liked · 3");

    fireEvent.click(screen.getByRole("button", { name: "Undo" }));

    await waitFor(() => expect(screen.getByText("Beans on toast")).toBeInTheDocument());
    expect(api.putRating).toHaveBeenCalledTimes(1);
    expect(api.putRating.mock.calls[0][0]).toBe("b");
    expect(api.putRating.mock.calls[0][1]).toMatchObject({
        rating: "up",
        kind: "readyNow",
        title: "Beans on toast",
    });
    expect(tab(/^Liked/)).toHaveTextContent("Liked · 4");
});

test("a rating that can't be removed says so and stays", async () => {
    const api = fakeRatingsApi(RATINGS);
    api.deleteRating.mockRejectedValueOnce(new Error("down"));
    show(api);
    await waitFor(() => expect(titles().length).toBeGreaterThan(0));

    fireEvent.click(screen.getByRole("button", { name: "Remove your rating of Beans on toast" }));

    expect(await screen.findByText("Couldn't remove that. Try again.")).toBeInTheDocument();
    expect(screen.getByText("Beans on toast")).toBeInTheDocument();
});

test("you can change your own rating, with Undo", async () => {
    const api = show();
    await waitFor(() => expect(titles().length).toBeGreaterThan(0));

    fireEvent.click(
        screen.getByRole("button", { name: "Change your rating of Beans on toast to not for us" }),
    );

    await waitFor(() => expect(screen.queryByText("Beans on toast")).not.toBeInTheDocument()); // it moved to Not for us
    expect(api.putRating.mock.calls[0][1]).toMatchObject({
        rating: "down",
        title: "Beans on toast",
        kind: "readyNow",
    });
    expect(api.store.ratings.filter((r) => r.title === "Beans on toast")).toHaveLength(1); // replaced, not added
    expect(screen.getByRole("status")).toHaveTextContent("Changed to 👎");

    fireEvent.click(screen.getByRole("button", { name: "Undo" }));

    await waitFor(() => expect(screen.getByText("Beans on toast")).toBeInTheDocument());
    expect(api.store.ratings.find((r) => r.title === "Beans on toast")!.rating).toBe("up");
});

test("empty tabs say what to do", async () => {
    show(fakeRatingsApi([rating({ id: "a", title: "Only liked" })]));
    await screen.findByRole("tab", { name: /^Liked/ });

    fireEvent.click(tab(/^Not for us/));

    expect(screen.getByRole("tabpanel")).toHaveTextContent("Nothing here yet");
    expect(tab(/^Not for us/)).toHaveTextContent("Not for us · 0");
});

test("a ratings list that can't be read says so, and Try again reads it", async () => {
    const api = fakeRatingsApi(RATINGS);
    api.ratings.mockRejectedValueOnce(new Error("down")).mockRejectedValueOnce(new Error("down")); // the provider's read and the page's
    show(api);

    fireEvent.click(await screen.findByRole("button", { name: "Try again" }));

    await waitFor(() => expect(tab(/^Liked/)).toBeInTheDocument());
    expect(api.ratings).toHaveBeenCalled();
});

test("it reads the ratings again when it opens, in case someone else rated", async () => {
    const api = show();
    await waitFor(() => expect(api.ratings.mock.calls.length).toBeGreaterThanOrEqual(2)); // the provider's read, then the page's
});

test("the top is kept for the taste profile: nothing is shown there yet, and Back goes to Hungry?", async () => {
    show();
    await screen.findByRole("tab", { name: /^Liked/ });

    expect(screen.getByRole("heading", { name: "Our taste" })).toBeInTheDocument();
    expect(screen.queryByText(/What we've learned/)).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Back to Hungry?" })).toHaveAttribute("href", "/recipes");
});
