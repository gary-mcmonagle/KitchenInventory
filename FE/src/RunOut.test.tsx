import { HubConnectionState } from "@microsoft/signalr";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { ApiError, type ApiClient, type RunOutChoices, type ShoppingListItem } from "./api";
import { ListScreen } from "./ListScreen";
import { memoryPersistence } from "./listStore";
import { RunOutCard, RunOutSheet } from "./RunOut";
import { ScanFeedView } from "./ScanFeedView";
import type { FeedToast } from "./scanFeed";
import { removedBatchFromUrl } from "./StockScreen";
import type { FeedConnection } from "./useScanFeed";
import { ShoppingListProvider } from "./useShoppingList";

// Add to the shopping list when we run out (#89).

const NOW = new Date("2026-10-20T17:50:00.000Z"); // 18:50 BST

const flush = () => act(async () => {});

class QuietConnection implements FeedConnection {
    state: string = HubConnectionState.Disconnected;
    on() {}
    onreconnected() {}
    async start() {
        this.state = HubConnectionState.Connected;
    }
    async stop() {}
}

const item = (id: string, name: string, overrides: Partial<ShoppingListItem> = {}): ShoppingListItem => ({
    id,
    name,
    note: null,
    productId: null,
    aisleId: "other",
    aisleName: "Other",
    findingAisle: false,
    source: "typed",
    status: "open",
    addedByName: "Gary",
    addedAt: "2026-10-20T07:12:00.000Z",
    doneByName: null,
    doneAt: null,
    updatedAt: "2026-10-20T07:12:00.000Z",
    version: 1,
    removed: false,
    ...overrides,
});

// ---- Item detail: the setting

describe("the item detail setting", () => {
    function card(
        status: { addWhenOut: boolean; canSet?: boolean },
        setAddWhenOut = vi.fn(async () => ({})),
    ) {
        const api = {
            runOutStatus: vi.fn(async () => ({
                genericNameId: "semi-skimmed milk",
                name: "Semi-skimmed milk",
                addWhenOut: status.addWhenOut,
                inStock: 2,
                canSet: status.canSet ?? true,
            })),
            setAddWhenOut,
        };
        const onSaved = vi.fn();
        render(
            <RunOutCard
                api={api as unknown as ApiClient}
                productId="p-milk"
                inStockCount={1}
                onSaved={onSaved}
            />,
        );
        return { api, onSaved };
    }

    test("is drawn with the generic name, every brand counting, and the stock across the name", async () => {
        card({ addWhenOut: false });
        await flush();
        const box = screen.getByRole("checkbox", {
            name: /Add Semi-skimmed milk to the shopping list when we run out/,
        });
        expect(box).not.toBeChecked();
        expect(
            screen.getByText("Counts every semi-skimmed milk, whatever the brand. 2 in stock now."),
        ).toBeInTheDocument();
    });

    test("turning it on saves at once for the generic name and says so", async () => {
        const { api, onSaved } = card({ addWhenOut: false });
        await flush();

        await act(async () => fireEvent.click(screen.getByRole("checkbox")));

        expect(api.setAddWhenOut).toHaveBeenCalledWith("semi-skimmed milk", true);
        expect(screen.getByRole("checkbox")).toBeChecked();
        expect(onSaved).toHaveBeenCalledWith("Will add Semi-skimmed milk when it runs out");
    });

    test("turning it off says it won't add it", async () => {
        const { api, onSaved } = card({ addWhenOut: true });
        await flush();

        await act(async () => fireEvent.click(screen.getByRole("checkbox")));

        expect(api.setAddWhenOut).toHaveBeenCalledWith("semi-skimmed milk", false);
        expect(onSaved).toHaveBeenCalledWith("Won't add Semi-skimmed milk automatically");
    });

    test("a failed save puts it back and says so", async () => {
        const { onSaved } = card(
            { addWhenOut: false },
            vi.fn(async () => {
                throw new ApiError(500, "down");
            }),
        );
        await flush();

        await act(async () => fireEvent.click(screen.getByRole("checkbox")));

        expect(screen.getByRole("checkbox")).not.toBeChecked();
        expect(onSaved).toHaveBeenCalledWith("Couldn't save that. Try again.");
    });

    test("is hidden until the product has a generic name", async () => {
        card({ addWhenOut: false, canSet: false });
        await flush();
        expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    });
});

// ---- The sheet after a check-out

const CHOICES: RunOutChoices = {
    checkedOutBy: "Sam",
    at: "2026-10-20T17:42:00.000Z",
    items: [
        {
            productId: "p-milk",
            genericNameId: "semi-skimmed milk",
            name: "Semi-skimmed milk",
            left: 0,
            onList: false,
            addWhenOut: false,
            canSet: true,
        },
        {
            productId: "p-hummus",
            genericNameId: "hummus",
            name: "Hummus",
            left: 1,
            onList: false,
            addWhenOut: true,
            canSet: true,
        },
        {
            productId: "p-penne",
            genericNameId: "penne",
            name: "Wholewheat penne",
            left: 0,
            onList: true,
            addWhenOut: false,
            canSet: true,
        },
    ],
};

function sheet(
    answer: RunOutChoices | Error = CHOICES,
    source: { batchId: string } | { productId: string } = { batchId: "b1" },
) {
    const load = vi.fn(async () => {
        if (answer instanceof Error) throw answer;
        return answer;
    });
    const api = {
        runOutForBatch: load,
        runOutForProduct: load,
        setAddWhenOut: vi.fn(async () => ({})),
        shoppingList: vi.fn(async () => ({ asOf: "x", items: [] })),
        addToList: vi.fn(async (id: string, add: { name: string }) => ({
            item: item(id, add.name),
            merged: false,
        })),
    };
    const onClose = vi.fn();
    render(
        <ShoppingListProvider
            api={api as unknown as ApiClient}
            connect={() => new QuietConnection()}
            persistence={memoryPersistence()}
        >
            <RunOutSheet api={api as unknown as ApiClient} source={source} onClose={onClose} now={NOW} />
        </ShoppingListProvider>,
    );
    return { api, onClose };
}

const rows = () => within(screen.getByRole("dialog")).getAllByRole("listitem");

describe("the sheet after a check-out", () => {
    test("step 1 lists the removed items with what's left, Add or On the list, and Not now", async () => {
        sheet();
        await flush();

        const dialog = screen.getByRole("dialog", { name: "Add to the shopping list?" });
        expect(within(dialog).getByText("Checked out by Sam · 18:42")).toBeInTheDocument();
        expect(rows().map((r) => r.textContent)).toEqual([
            "Semi-skimmed milkNone leftAdd",
            "Hummus1 leftAdd",
            "Wholewheat penneNone leftOn the list",
        ]);
        expect(within(dialog).getByRole("button", { name: "Not now" })).toBeInTheDocument();
    });

    test("Add puts it on the list, then asks to do it automatically; Yes, always turns the setting on", async () => {
        const { api } = sheet();
        await flush();

        await act(async () =>
            fireEvent.click(
                screen.getByRole("button", { name: "Add Semi-skimmed milk to the shopping list" }),
            ),
        );

        expect(api.addToList).toHaveBeenCalledWith(
            expect.any(String),
            expect.objectContaining({
                name: "Semi-skimmed milk",
                productId: "p-milk",
                source: "checkOutPrompt",
            }),
        );
        const dialog = screen.getByRole("dialog", {
            name: "Add Semi-skimmed milk automatically whenever we run out?",
        });
        expect(within(dialog).getByText("Added Semi-skimmed milk to the list")).toBeInTheDocument();

        await act(async () => fireEvent.click(screen.getByRole("button", { name: "Yes, always" })));

        expect(api.setAddWhenOut).toHaveBeenCalledWith("semi-skimmed milk", true);
        expect(
            screen.getByText("Will add Semi-skimmed milk automatically when it runs out."),
        ).toBeInTheDocument();
        expect(rows()[0]).toHaveTextContent("On the list");
        expect(screen.getByRole("button", { name: "Done" })).toBeInTheDocument();
    });

    test("Just this time adds it without changing the setting", async () => {
        const { api } = sheet();
        await flush();
        await act(async () =>
            fireEvent.click(
                screen.getByRole("button", { name: "Add Semi-skimmed milk to the shopping list" }),
            ),
        );

        fireEvent.click(screen.getByRole("button", { name: "Just this time" }));

        expect(api.setAddWhenOut).not.toHaveBeenCalled();
        expect(screen.getByRole("dialog", { name: "Add to the shopping list?" })).toBeInTheDocument();
        expect(rows()[0]).toHaveTextContent("On the list");
    });

    test("step 2 is skipped when the setting is already on", async () => {
        sheet();
        await flush();

        await act(async () =>
            fireEvent.click(screen.getByRole("button", { name: "Add Hummus to the shopping list" })),
        );

        expect(screen.getByRole("dialog", { name: "Add to the shopping list?" })).toBeInTheDocument();
        expect(screen.queryByRole("button", { name: "Yes, always" })).not.toBeInTheDocument();
    });

    test("Not now closes it", async () => {
        const { onClose } = sheet();
        await flush();
        fireEvent.click(screen.getByRole("button", { name: "Not now" }));
        expect(onClose).toHaveBeenCalled();
    });

    test("scanner check-outs say so, and a toast's sheet says it was just checked out", async () => {
        sheet({ ...CHOICES, checkedOutBy: "the scanner" });
        await flush();
        expect(screen.getByText("Checked out at the scanner · 18:42")).toBeInTheDocument();
    });

    test("opened from a toast it's for that product", async () => {
        const { api } = sheet({ ...CHOICES, items: CHOICES.items.slice(0, 1) }, { productId: "p-milk" });
        await flush();
        expect(api.runOutForProduct).toHaveBeenCalledWith("p-milk");
        expect(screen.getByText("Just checked out")).toBeInTheDocument();
    });

    test("closes by itself when there's nothing to offer or the update has expired", async () => {
        const empty = sheet({ ...CHOICES, items: [] });
        await flush();
        expect(empty.onClose).toHaveBeenCalled();
    });

    test("closes by itself on a 404 (expired)", async () => {
        vi.spyOn(console, "warn").mockImplementation(() => {});
        const expired = sheet(new ApiError(404, "expired"));
        await flush();
        expect(expired.onClose).toHaveBeenCalled();
    });
});

// ---- Getting there: the push link and the green toast

describe("opening the sheet", () => {
    test("a push link names its batch", () => {
        expect(removedBatchFromUrl("?removed=0f8fad5b-d9cb-469f-a165-70867728950e")).toBe(
            "0f8fad5b-d9cb-469f-a165-70867728950e",
        );
        expect(removedBatchFromUrl("?removed=nonsense")).toBeNull();
        expect(removedBatchFromUrl("")).toBeNull();
    });

    const toast = (overrides: Partial<FeedToast>): FeedToast => ({
        scanId: "s1",
        kind: "ok",
        title: "Checked out Semi-skimmed milk",
        role: "out",
        at: NOW.toISOString(),
        productId: "p-milk",
        expiresAt: NOW.getTime() + 10_000,
        ...overrides,
    });

    test("tapping a green Checked out toast offers to add it; a check-in toast isn't a button", () => {
        const onCheckedOut = vi.fn();
        const feed = new Map([
            ["s1", toast({})],
            ["s2", toast({ scanId: "s2", role: "in", title: "Added Bread", productId: "p-bread" })],
        ]);
        render(
            <ScanFeedView feed={feed} now={NOW.getTime()} onDismiss={() => {}} onCheckedOut={onCheckedOut} />,
        );

        fireEvent.click(
            screen.getByRole("button", {
                name: /Checked out Semi-skimmed milk\. Add it to the shopping list\?/,
            }),
        );

        expect(onCheckedOut).toHaveBeenCalledWith("p-milk");
        expect(screen.queryByRole("button", { name: /Added Bread/ })).not.toBeInTheDocument();
    });
});

// ---- On the list

test("automatic adds show the Auto tag and when the last one was used", async () => {
    const api = {
        shoppingList: vi.fn(async () => ({
            asOf: "x",
            items: [
                item("1", "Semi-skimmed milk", {
                    source: "autoRunOut",
                    addedByName: "Cupboard",
                    addedAt: "2026-10-20T07:12:00.000Z",
                }),
            ],
        })),
        listSuggestions: vi.fn(async () => ({ suggestions: [] })),
        shoppingListViewed: vi.fn(async () => undefined),
    };
    render(
        <ShoppingListProvider
            api={api as unknown as ApiClient}
            connect={() => new QuietConnection()}
            persistence={memoryPersistence()}
        >
            <ListScreen api={api as unknown as ApiClient} now={NOW} />
        </ShoppingListProvider>,
    );
    await flush();

    const row = within(screen.getByRole("list", { name: "To buy" })).getByRole("listitem");
    expect(row).toHaveTextContent("Semi-skimmed milkAutoAdded when the last one was used · 08:12");
});
