import { act, render } from "@testing-library/react";
import { useListViewed, VIEWED_EVERY_MS } from "./listViewed";

// Last viewed (#86): the List tells the API when it's on screen, so a list push only mentions what's new.

function Probe({
    api,
    changes,
    now,
}: {
    api: { shoppingListViewed: () => Promise<void> };
    changes: unknown;
    now: () => number;
}) {
    useListViewed(api, changes, now);
    return null;
}

let visibility: DocumentVisibilityState = "visible";
beforeEach(() => {
    visibility = "visible";
    vi.spyOn(document, "visibilityState", "get").mockImplementation(() => visibility);
});
afterEach(() => vi.restoreAllMocks());

const setVisibility = (state: DocumentVisibilityState) => {
    visibility = state;
    act(() => {
        document.dispatchEvent(new Event("visibilitychange"));
    });
};

test("opening the List tells the API once", () => {
    const api = { shoppingListViewed: vi.fn(async () => undefined) };
    render(<Probe api={api} changes={1} now={() => 0} />);
    expect(api.shoppingListViewed).toHaveBeenCalledTimes(1);
});

test("live changes while it's on screen tell it at most once a minute", () => {
    const api = { shoppingListViewed: vi.fn(async () => undefined) };
    let time = 0;
    const now = () => time;
    const { rerender } = render(<Probe api={api} changes={1} now={now} />);

    time = 30_000;
    rerender(<Probe api={api} changes={2} now={now} />);
    expect(api.shoppingListViewed).toHaveBeenCalledTimes(1);

    time = VIEWED_EVERY_MS;
    rerender(<Probe api={api} changes={3} now={now} />);
    expect(api.shoppingListViewed).toHaveBeenCalledTimes(2);

    time = VIEWED_EVERY_MS + 10_000;
    rerender(<Probe api={api} changes={4} now={now} />);
    expect(api.shoppingListViewed).toHaveBeenCalledTimes(2);
});

test("coming back to the app on the List tells it; changes while hidden don't", () => {
    const api = { shoppingListViewed: vi.fn(async () => undefined) };
    let time = 0;
    const now = () => time;
    const { rerender } = render(<Probe api={api} changes={1} now={now} />);

    setVisibility("hidden");
    time = 5 * VIEWED_EVERY_MS;
    rerender(<Probe api={api} changes={2} now={now} />);
    expect(api.shoppingListViewed).toHaveBeenCalledTimes(1);

    setVisibility("visible");
    expect(api.shoppingListViewed).toHaveBeenCalledTimes(2);
});

test("leaving the List stops it, and a failure is only a warning", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const api = { shoppingListViewed: vi.fn(async () => Promise.reject(new Error("offline"))) };
    const { unmount } = render(<Probe api={api} changes={1} now={() => 0} />);
    await act(async () => {});
    expect(warn).toHaveBeenCalled();

    unmount();
    setVisibility("hidden");
    setVisibility("visible");
    expect(api.shoppingListViewed).toHaveBeenCalledTimes(1);
});
