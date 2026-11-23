import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { ApiError, type ApiClient, type UsualsItem, type UsualsJob } from "./api";
import { UsualsSheet } from "./Usuals";

// "Add usuals" (#134): progress, then the selection.

const item = (
    name: string,
    band: "due" | "maybe",
    aisle = "dairyEggs",
    why = "Bought in 4 of the last 4 weeks",
): UsualsItem => ({
    name,
    genericNameId: null,
    aisle,
    band,
    why,
    productId: `p-${name}`,
});

const job = (overrides: Partial<UsualsJob> = {}): UsualsJob => ({
    jobId: "job-1",
    status: "working",
    step: "history",
    method: null,
    items: [],
    ...overrides,
});

const READY: UsualsItem[] = [
    item("Baby spinach", "due", "fruitVeg"),
    item("Squash", "maybe", "fruitVeg", "Bought in 2 of the last 4 weeks"),
    item("Semi-skimmed milk", "due"),
    item("Medium eggs", "due"),
    item("Chickpeas", "maybe", "tinsJars"),
];

function setup(answers: Array<UsualsJob | Error>, pollMs = 0) {
    const queue = [...answers];
    const next = async () => {
        const a = queue.length > 1 ? queue.shift()! : queue[0];
        if (a instanceof Error) throw a;
        return a;
    };
    const startUsuals = vi.fn(next);
    const usualsJob = vi.fn(next);
    const onAdd = vi.fn();
    const onClose = vi.fn();
    const view = render(
        <UsualsSheet
            api={{ startUsuals, usualsJob } as unknown as ApiClient}
            onAdd={onAdd}
            onClose={onClose}
            pollMs={pollMs}
        />,
    );
    return { startUsuals, usualsJob, onAdd, onClose, ...view };
}

const row = (name: string) => screen.getByRole("button", { name: new RegExp(`^${name}`) });
const addButton = () =>
    screen.getByRole("button", { name: /^(Add \d+ items? to the list|Tick something to add)$/ });

test("it shows the progress steps as the job moves on, then the selection", async () => {
    setup([
        job({ step: "history" }),
        job({ step: "deciding" }),
        job({ step: "list" }),
        job({ status: "ready", step: "list", method: "jev", items: READY }),
    ]);

    expect(screen.getByRole("heading", { name: "Working out what's due" })).toBeInTheDocument();
    expect(screen.getByText("Reading your shopping history")).toBeInTheDocument();
    expect(screen.getByText("Working out what's due", { selector: "span" })).toBeInTheDocument();
    expect(screen.getByText("Leaving out what's already on your list")).toBeInTheDocument();
    expect(screen.getByRole("status", { name: "Working" })).toBeInTheDocument();

    expect(await screen.findByRole("heading", { name: "Your usuals" })).toBeInTheDocument();
    expect(screen.queryByText("Reading your shopping history")).not.toBeInTheDocument();
});

test("the selection groups by aisle in shop order, ticks Due only, and shows each why line and pill", async () => {
    setup([job({ status: "ready", method: "jev", items: READY })]);
    await screen.findByRole("heading", { name: "Your usuals" });

    expect(screen.getAllByRole("region").map((r) => r.getAttribute("aria-label"))).toEqual([
        "Fruit & veg",
        "Dairy & eggs",
        "Tins & jars",
    ]);
    expect(screen.getByText("3 due · 2 maybe")).toBeInTheDocument();
    expect(row("Baby spinach")).toHaveAttribute("aria-pressed", "true");
    expect(row("Squash")).toHaveAttribute("aria-pressed", "false");
    expect(row("Chickpeas")).toHaveAttribute("aria-pressed", "false");
    expect(within(row("Squash")).getByText("Maybe")).toBeInTheDocument();
    expect(within(row("Squash")).getByText("Bought in 2 of the last 4 weeks")).toBeInTheDocument();
    expect(within(row("Baby spinach")).getByText("Due")).toBeInTheDocument();
    expect(addButton()).toHaveTextContent("Add 3 items to the list");
    expect(screen.queryByRole("note")).not.toBeInTheDocument();
});

test("it shows every suggestion however many there are", async () => {
    const many = Array.from({ length: 120 }, (_, i) =>
        item(`Thing ${String(i).padStart(3, "0")}`, i % 3 === 0 ? "maybe" : "due", "tinsJars"),
    );
    setup([job({ status: "ready", method: "jev", items: many })]);
    await screen.findByRole("heading", { name: "Your usuals" });

    expect(screen.getAllByRole("button", { name: /^Thing / })).toHaveLength(120);
    expect(screen.getByText("80 due · 40 maybe")).toBeInTheDocument();
});

test("rows toggle, Select all due and Clear all work, and the add button counts and disables at zero", async () => {
    setup([job({ status: "ready", method: "jev", items: READY })]);
    await screen.findByRole("heading", { name: "Your usuals" });

    fireEvent.click(row("Squash"));
    expect(addButton()).toHaveTextContent("Add 4 items to the list");
    fireEvent.click(screen.getByRole("button", { name: "Select all due" })); // back to exactly the Due ones
    expect(row("Squash")).toHaveAttribute("aria-pressed", "false");
    expect(addButton()).toHaveTextContent("Add 3 items to the list");

    fireEvent.click(screen.getByRole("button", { name: "Clear all" }));
    expect(addButton()).toHaveTextContent("Tick something to add");
    expect(addButton()).toBeDisabled();
    fireEvent.click(row("Medium eggs"));
    expect(addButton()).toHaveTextContent("Add 1 item to the list");
});

test("adding hands over the ticked items only, then closes; nothing is added before that", async () => {
    const { onAdd, onClose } = setup([job({ status: "ready", method: "jev", items: READY })]);
    await screen.findByRole("heading", { name: "Your usuals" });

    fireEvent.click(row("Chickpeas"));
    fireEvent.click(row("Medium eggs")); // untick
    expect(onAdd).not.toHaveBeenCalled();
    fireEvent.click(addButton());

    expect(onAdd).toHaveBeenCalledTimes(1);
    expect(onAdd.mock.calls[0][0].map((i: UsualsItem) => i.name).sort()).toEqual([
        "Baby spinach",
        "Chickpeas",
        "Semi-skimmed milk",
    ]);
    expect(onClose).toHaveBeenCalled();
});

test("Cancel and Escape close without adding", async () => {
    const { onAdd, onClose } = setup([job({ status: "ready", method: "jev", items: READY })]);
    await screen.findByRole("heading", { name: "Your usuals" });

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });

    expect(onClose).toHaveBeenCalledTimes(2);
    expect(onAdd).not.toHaveBeenCalled();
});

test("it says when only the buying pattern was used", async () => {
    setup([job({ status: "ready", method: "pattern", items: READY })]);

    expect(await screen.findByRole("note")).toHaveTextContent("Based on your buying pattern only.");
});

test("with nothing due it says so", async () => {
    const { onClose } = setup([job({ status: "ready", method: "jev", items: [] })]);

    expect(await screen.findByRole("heading", { name: "Nothing looks due" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(onClose).toHaveBeenCalled();
});

test("a failed job says nothing was added and Try again starts it afresh", async () => {
    const { startUsuals, onAdd } = setup([
        job({ status: "failed" }),
        job({ status: "ready", method: "jev", items: READY }),
    ]);

    expect(await screen.findByRole("heading", { name: "Couldn't work out your usuals" })).toBeInTheDocument();
    expect(screen.getByText(/Nothing was added to your list/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(await screen.findByRole("heading", { name: "Your usuals" })).toBeInTheDocument();
    expect(startUsuals).toHaveBeenCalledTimes(2);
    expect(onAdd).not.toHaveBeenCalled();
});

test("a request that errors, or the hourly limit, shows the failed state with the right words", async () => {
    setup([new ApiError(500, "boom")]);
    expect(await screen.findByText(/That took too long or something went wrong/)).toBeInTheDocument();
});

test("the hourly limit has its own words", async () => {
    setup([new ApiError(429, "slow down")]);
    expect(
        await screen.findByText("That's a lot of usuals for one hour. Try again a bit later."),
    ).toBeInTheDocument();
});

test("Stop closes the sheet and stops asking", async () => {
    const { usualsJob, onClose, unmount } = setup([job({ step: "deciding" })], 10);
    await waitFor(() => expect(usualsJob).toHaveBeenCalled());

    fireEvent.click(screen.getByRole("button", { name: "Stop" }));
    expect(onClose).toHaveBeenCalled();
    unmount();
    const calls = usualsJob.mock.calls.length;
    await act(async () => {
        await new Promise((r) => setTimeout(r, 60));
    });
    expect(usualsJob.mock.calls.length).toBe(calls);
});
