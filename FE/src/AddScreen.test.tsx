import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { addedMessage, AddScreen, rowsFromPhoto, toAddItem } from "./AddScreen";
import { ApiError, type ApiClient, type LooseProduct, type PhotoRecognition } from "./api";
import { takeFlash } from "./flash";
import { Shell } from "./Shell";

const loose: LooseProduct[] = [
    {
        productId: "bananas",
        displayName: "Bananas",
        shelfLifeDays: 5,
        lastAddedAt: "2026-10-17T10:00:00.000Z",
    },
    { productId: "sourdough", displayName: "Sourdough loaf", shelfLifeDays: 4, lastAddedAt: null },
];

const photo: PhotoRecognition = {
    available: true,
    readable: true,
    items: [
        { name: "leeks", brand: null, size: null, kind: "loose", quantity: 2, match: null },
        {
            name: "bananas",
            brand: null,
            size: null,
            kind: "loose",
            quantity: 6,
            match: { productId: "bananas", displayName: "Bananas" },
        },
        {
            name: "Milk chocolate fruit and nut bar",
            brand: "Cadbury",
            size: "180g",
            kind: "packaged",
            quantity: 1,
            match: null,
        },
    ],
};

function deferred<T>() {
    let resolve!: (v: T) => void;
    let reject!: (e: unknown) => void;
    const promise = new Promise<T>((res, rej) => ((resolve = res), (reject = rej)));
    return { promise, resolve, reject };
}

function setup(overrides: Record<string, unknown> = {}, assignScanId?: string) {
    const api = {
        looseProducts: vi.fn(async () => ({ products: loose })),
        recognisePhoto: vi.fn(async () => photo),
        addToStock: vi.fn(async (_id: string, items: Array<{ quantity: number }>) => ({
            units: items.reduce((n, i) => n + i.quantity, 0),
            items: items.map((i, n) => ({
                productId: `p${n}`,
                displayName: `Item ${n}`,
                quantity: i.quantity,
                createdProduct: false,
            })),
        })),
        assignStoreLabel: vi.fn(async () => ({
            scanId: "scan-1",
            unitId: "scan-1",
            productId: "ham",
            displayName: "Ham hock terrine",
        })),
        ...overrides,
    };
    const prepare = vi.fn(async (file: Blob) => new Blob([file], { type: "image/jpeg" }));
    window.history.pushState(null, "", assignScanId ? `/store-labels/${assignScanId}` : "/add");
    render(<AddScreen api={api as unknown as ApiClient} assignScanId={assignScanId} prepare={prepare} />);
    return { api, prepare };
}

const shoot = () =>
    fireEvent.change(screen.getByLabelText("Photo"), {
        target: { files: [new File(["jpeg"], "photo.jpg", { type: "image/jpeg" })] },
    });

const rows = () => within(screen.getByRole("list", { name: "To add" })).getAllByRole("listitem");

beforeEach(() => {
    takeFlash();
});

test("shows the title, Take a photo, the quick pick and typing, with Add to stock off until something's chosen", async () => {
    setup();

    expect(screen.getByRole("heading", { level: 1, name: "Add to stock" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Take a photo" })).toBeInTheDocument();
    expect(screen.getByLabelText("Photo")).toHaveAttribute("capture", "environment");
    expect(screen.getByLabelText("Photo")).toHaveAttribute("accept", "image/*");
    expect(await screen.findByRole("button", { name: "Bananas" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add to stock" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeInTheDocument();
});

test("photo → reading → a ticked checklist with counts, details and matches; Add sends every ticked row", async () => {
    const reply = deferred<PhotoRecognition>();
    const { api, prepare } = setup({ recognisePhoto: vi.fn(() => reply.promise) });

    shoot();

    expect(await screen.findByRole("heading", { name: "Looking at your photo" })).toBeInTheDocument();
    expect(screen.getByText("This takes a couple of seconds. The photo is not saved.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add to stock" })).toBeDisabled();
    await act(async () => reply.resolve(photo));

    expect(prepare).toHaveBeenCalledTimes(1);
    expect(api.recognisePhoto).toHaveBeenCalledWith(expect.any(Blob));
    expect(rows()).toHaveLength(3);
    expect(screen.getAllByLabelText("Name").map((i) => (i as HTMLInputElement).value)).toEqual([
        "leeks",
        "Bananas",
        "Milk chocolate fruit and nut bar",
    ]);
    expect(within(rows()[1]).getByText(/in your stock list/)).toHaveTextContent(
        "Matches Bananas in your stock list",
    );
    expect(within(rows()[2]).getByText("Cadbury · 180g")).toBeInTheDocument();
    expect(within(rows()[1]).getByText("6")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Retake" })).toBeInTheDocument();

    fireEvent.click(within(rows()[2]).getByRole("checkbox")); // untick the chocolate
    fireEvent.click(screen.getByRole("button", { name: "Add to stock" }));

    await vi.waitFor(() => expect(api.addToStock).toHaveBeenCalled());
    const [requestId, items] = api.addToStock.mock.calls[0];
    expect(requestId).toMatch(/^[0-9a-f-]{36}$/);
    expect(items).toEqual([
        { name: "leeks", quantity: 2, source: "photo" },
        { productId: "bananas", quantity: 6, source: "photo" },
    ]);
    await vi.waitFor(() => expect(window.location.pathname).toBe("/"));
    expect(takeFlash()).toBe("Added 8 items");
});

test("editing a matched row's name drops the match: the API matches by the new name", async () => {
    const { api } = setup();
    shoot();
    await screen.findByText(/Matches/);

    fireEvent.change(screen.getAllByLabelText("Name")[1], { target: { value: "Plantains" } });
    expect(screen.queryByText(/Matches/)).not.toBeInTheDocument();
    fireEvent.click(within(rows()[0]).getByRole("checkbox"));
    fireEvent.click(within(rows()[2]).getByRole("checkbox"));
    fireEvent.click(screen.getByRole("button", { name: "Add to stock" }));

    await vi.waitFor(() => expect(api.addToStock).toHaveBeenCalled());
    expect(api.addToStock.mock.calls[0][1]).toEqual([{ name: "Plantains", quantity: 6, source: "photo" }]);
});

test("the quantity stepper stays between 1 and 20", async () => {
    setup();
    fireEvent.click(await screen.findByRole("button", { name: "Sourdough loaf" }));
    const row = rows()[0];
    const fewer = within(row).getByRole("button", { name: "One fewer" });
    const more = within(row).getByRole("button", { name: "One more" });

    expect(fewer).toBeDisabled();
    for (let i = 0; i < 25; i++) fireEvent.click(more);
    expect(within(row).getByText("20")).toBeInTheDocument();
    expect(more).toBeDisabled();
    fireEvent.click(fewer);
    expect(within(row).getByText("19")).toBeInTheDocument();
});

test("nothing found, an unreadable photo, recognition unavailable and a failed call say so, and the other ways still work", async () => {
    const recognisePhoto = vi
        .fn()
        .mockResolvedValueOnce({ available: true, readable: true, items: [] })
        .mockResolvedValueOnce({ available: true, readable: false, items: [] })
        .mockResolvedValueOnce({ available: false, readable: false, items: [] })
        .mockRejectedValueOnce(new ApiError(500, "boom"));
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    setup({ recognisePhoto });

    shoot();
    expect(
        await screen.findByText("We couldn't see anything we recognise. Try again closer, or type it below."),
    ).toBeInTheDocument();
    shoot();
    expect(
        await screen.findByText("That photo is too blurry or dark to read. Try again closer."),
    ).toBeInTheDocument();
    shoot();
    expect(
        await screen.findByText("We couldn't read that photo. Pick it from the list or type it below."),
    ).toBeInTheDocument();
    shoot();
    await vi.waitFor(() => expect(recognisePhoto).toHaveBeenCalledTimes(4));
    expect(
        await screen.findByText("We couldn't read that photo. Pick it from the list or type it below."),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Bananas" }));
    expect(rows()).toHaveLength(1);
    expect(warn).toHaveBeenCalledWith("Photo recognition failed", expect.any(ApiError));
    warn.mockRestore();
});

test("retake replaces the photo's rows but keeps picked and typed ones", async () => {
    const recognisePhoto = vi
        .fn()
        .mockResolvedValueOnce(photo)
        .mockResolvedValueOnce({ available: true, readable: true, items: [photo.items[2]] });
    setup({ recognisePhoto });
    fireEvent.click(await screen.findByRole("button", { name: "Sourdough loaf" }));

    shoot();
    await vi.waitFor(() => expect(rows()).toHaveLength(4));
    shoot();
    await vi.waitFor(() => expect(rows()).toHaveLength(2));

    expect(screen.getAllByLabelText("Name").map((i) => (i as HTMLInputElement).value)).toEqual([
        "Sourdough loaf",
        "Milk chocolate fruit and nut bar",
    ]);
});

test("quick pick searches, adds a row and bumps it when picked again; typing adds a new item", async () => {
    const { api } = setup();
    await screen.findByRole("button", { name: "Bananas" });

    fireEvent.change(screen.getByPlaceholderText("Search loose items"), { target: { value: "sour" } });
    expect(screen.queryByRole("button", { name: "Bananas" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Sourdough loaf" }));
    fireEvent.click(screen.getByRole("button", { name: "Sourdough loaf" }));
    fireEvent.change(screen.getByPlaceholderText("Search loose items"), { target: { value: "zzz" } });
    expect(screen.getByText("Nothing matches that.")).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Name of something else"), { target: { value: "  Leeks " } });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(rows()).toHaveLength(2);
    expect(within(rows()[0]).getByText("2")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Add to stock" }));
    await vi.waitFor(() => expect(api.addToStock).toHaveBeenCalled());
    expect(api.addToStock.mock.calls[0][1]).toEqual([
        { productId: "sourdough", quantity: 2, source: "pick" },
        { name: "Leeks", quantity: 1, source: "typed" },
    ]);
});

test("a failed add keeps the list and retries with the same request ID", async () => {
    const addToStock = vi
        .fn()
        .mockRejectedValueOnce(new ApiError(500, "boom"))
        .mockResolvedValueOnce({
            units: 1,
            items: [{ productId: "b", displayName: "Bananas", quantity: 1 }],
        });
    setup({ addToStock });
    fireEvent.click(await screen.findByRole("button", { name: "Bananas" }));

    fireEvent.click(screen.getByRole("button", { name: "Add to stock" }));
    expect(await screen.findByText("Couldn't add that. Try again.")).toBeInTheDocument();
    expect(rows()).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Add to stock" }));

    await vi.waitFor(() => expect(addToStock).toHaveBeenCalledTimes(2));
    expect(addToStock.mock.calls[1][0]).toBe(addToStock.mock.calls[0][0]);
    await vi.waitFor(() => expect(takeFlash()).toBe("Added Bananas"));
});

test("a ticked row with no name isn't sent", async () => {
    const { api } = setup();
    fireEvent.click(await screen.findByRole("button", { name: "Bananas" }));
    fireEvent.change(screen.getAllByLabelText("Name")[0], { target: { value: "  " } });

    fireEvent.click(screen.getByRole("button", { name: "Add to stock" }));

    expect(await screen.findByText("Give every ticked item a name.")).toBeInTheDocument();
    expect(api.addToStock).not.toHaveBeenCalled();
});

test("Cancel and back go to the stock list without adding", async () => {
    const { api } = setup();
    fireEvent.click(await screen.findByRole("button", { name: "Bananas" }));

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(window.location.pathname).toBe("/");
    expect(api.addToStock).not.toHaveBeenCalled();
});

test("assign mode: 'What is this?', one row, no quantity; the newest choice replaces it", async () => {
    const { api } = setup({}, "scan-1");

    expect(screen.getByRole("heading", { level: 1, name: "What is this?" })).toBeInTheDocument();
    fireEvent.click(await screen.findByRole("button", { name: "Bananas" }));
    fireEvent.change(screen.getByLabelText("Name of something else"), {
        target: { value: "Ham hock terrine" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    expect(rows()).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "One more" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "This is it" }));

    await vi.waitFor(() => expect(api.assignStoreLabel).toHaveBeenCalled());
    expect(api.assignStoreLabel).toHaveBeenCalledWith("scan-1", {
        productId: undefined,
        name: "Ham hock terrine",
        brand: undefined,
        size: undefined,
        source: "typed",
    });
    expect(api.addToStock).not.toHaveBeenCalled();
    await vi.waitFor(() => expect(takeFlash()).toBe("Added Ham hock terrine"));
});

test("assign mode: a photo gives one row, and an already-assigned label says so", async () => {
    const { api } = setup(
        { assignStoreLabel: vi.fn().mockRejectedValue(new ApiError(409, "conflict")) },
        "scan-1",
    );

    shoot();
    await vi.waitFor(() => expect(rows()).toHaveLength(1));
    fireEvent.click(screen.getByRole("button", { name: "This is it" }));

    expect(await screen.findByText("That store label has already been sorted out.")).toBeInTheDocument();
    expect(api.assignStoreLabel).toHaveBeenCalledWith("scan-1", expect.objectContaining({ source: "photo" }));
});

test("helpers: rows from a photo, what's sent, and the toast text", () => {
    const [leeks, bananas] = rowsFromPhoto({
        available: true,
        readable: true,
        items: [{ ...photo.items[0], quantity: 40 }, photo.items[1]],
    });
    expect(leeks.quantity).toBe(20);
    expect(toAddItem({ ...leeks, brand: "Tesco" })).toEqual({
        name: "leeks",
        brand: "Tesco",
        size: undefined,
        quantity: 20,
        source: "photo",
    });
    expect(toAddItem(bananas)).toEqual({ productId: "bananas", quantity: 6, source: "photo" });
    expect(addedMessage([{ displayName: "Bananas", quantity: 6 }])).toBe("Added 6 × Bananas");
    expect(addedMessage([{ displayName: "Leeks", quantity: 1 }])).toBe("Added Leeks");
    expect(
        addedMessage([
            { displayName: "Leeks", quantity: 1 },
            { displayName: "Bananas", quantity: 2 },
        ]),
    ).toBe("Added 3 items");
});

test("the Add pill in the bottom nav opens the Add screen, and the stock list shows the toast after adding", async () => {
    const api = {
        me: async () => ({ name: "Cupboard test", objectId: "o" }),
        ping: async () => ({ status: "ok", commit: "abc1234" }),
        stock: async () => ({ generatedAt: "", totalUnits: 0, useFirstCount: 0, products: [] }),
        scanFeed: async () => ({ generatedAt: "", events: [] }),
        looseProducts: async () => ({ products: loose }),
        addToStock: async () => ({
            units: 6,
            items: [{ productId: "b", displayName: "Bananas", quantity: 6 }],
        }),
    } as unknown as ApiClient;
    window.history.pushState(null, "", "/");
    render(<Shell api={api} buildSha="def5678" />);

    fireEvent.click(screen.getByRole("link", { name: "Add an item" }));
    expect(await screen.findByRole("heading", { level: 1, name: "Add to stock" })).toBeInTheDocument();
    fireEvent.click(await screen.findByRole("button", { name: "Bananas" }));
    fireEvent.click(screen.getByRole("button", { name: "Add to stock" }));

    expect(await screen.findByText("Added 6 × Bananas")).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1, name: "Cupboard" })).toBeInTheDocument();
});
