import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { NoAccessError, type ApiClient, type Stock, type StockProduct } from "./api";
import { StockScreen } from "./StockScreen";

const product = (overrides: Partial<StockProduct>): StockProduct => ({
    productId: crypto.randomUUID(),
    barcode: "5000354922831",
    displayName: "Beef Gravy",
    brand: "Bisto",
    size: "230g",
    count: 1,
    latestCheckedInAt: "2026-10-17T18:00:00.000Z",
    status: "known",
    ...overrides,
});

const stockOf = (...products: StockProduct[]): Stock => ({
    generatedAt: "2026-10-17T22:00:00.000Z",
    totalUnits: products.reduce((n, p) => n + p.count, 0),
    products,
});

function fakeApi(...responses: Array<Stock | Error>) {
    const stock = vi.fn(async () => {
        const next = responses.length > 1 ? responses.shift()! : responses[0];
        if (next instanceof Error) throw next;
        return next;
    });
    return { api: { stock } as unknown as ApiClient, stock };
}

test("named products in one section, in the API's order, with counts and meta lines", async () => {
    const { api } = fakeApi(
        stockOf(
            product({ displayName: "Beef Gravy", count: 2 }),
            product({ displayName: "walnut halves", brand: null, size: "200g" }),
        ),
    );
    render(<StockScreen api={api} />);

    const section = await screen.findByRole("region", { name: /In stock/ });
    expect(within(section).getByText("2 items")).toBeInTheDocument();
    const rows = within(section).getAllByRole("listitem");
    expect(rows.map((r) => r.textContent)).toEqual([
        "BBeef GravyBisto · 230g · ×2Check out",
        "Wwalnut halves200gCheck out",
    ]);
    expect(screen.getByText("3 items")).toBeInTheDocument(); // header summary: units
    expect(screen.queryByRole("region", { name: /Waiting for a name/ })).not.toBeInTheDocument();
});

test("unnamed products wait for a name, with the grouped barcode and scan date", async () => {
    const { api } = fakeApi(
        stockOf(
            product({ displayName: "Beef Gravy" }),
            product({
                displayName: "Unknown item (501001234567)",
                barcode: "501001234567",
                status: "noMatch",
                latestCheckedInAt: "2026-10-15T09:00:00.000Z",
            }),
            product({
                displayName: "Unknown item (20055363)",
                barcode: "20055363",
                status: "lookupFailed",
                count: 2,
            }),
        ),
    );
    render(<StockScreen api={api} />);

    const section = await screen.findByRole("region", { name: /Waiting for a name/ });
    const rows = within(section).getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText("Unknown item")).toBeInTheDocument();
    expect(within(rows[0]).getByText("Barcode 5010 0123 4567")).toBeInTheDocument();
    expect(within(rows[0]).getByText("No name yet")).toBeInTheDocument();
    expect(within(rows[0]).getByText("Scanned 15 Oct")).toBeInTheDocument();
    expect(within(rows[1]).getByText("Barcode 2005 5363 · ×2")).toBeInTheDocument();
});

test("only Check out on named rows; nothing tappable that belongs to later cards", async () => {
    const { api } = fakeApi(
        stockOf(product({}), product({ status: "noMatch", displayName: "Unknown item (1)" })),
    );
    render(<StockScreen api={api} />);

    await screen.findByRole("region", { name: /In stock/ });
    expect(screen.getAllByRole("button").map((b) => b.getAttribute("aria-label"))).toEqual([
        "Check out one Beef Gravy",
    ]);
    const unnamed = screen.getByRole("region", { name: /Waiting for a name/ });
    expect(within(unnamed).queryAllByRole("button")).toHaveLength(0);
    expect(screen.queryAllByRole("link")).toHaveLength(0);
    expect(screen.queryByRole("searchbox")).not.toBeInTheDocument();
    expect(screen.queryByRole("navigation")).not.toBeInTheDocument();
});

test("empty stock", async () => {
    const { api } = fakeApi(stockOf());
    render(<StockScreen api={api} />);

    expect(await screen.findByText(/Nothing in stock yet\./)).toHaveTextContent(
        "Nothing in stock yet.Scan something in.",
    );
    expect(screen.getByText("0 items")).toBeInTheDocument();
});

test("loading shows skeleton rows, then the list", async () => {
    let resolve!: (s: Stock) => void;
    const api = { stock: () => new Promise<Stock>((r) => (resolve = r)) } as unknown as ApiClient;
    render(<StockScreen api={api} />);

    expect(screen.getByRole("status", { name: "Loading your stock" })).toBeInTheDocument();
    await act(async () => resolve(stockOf(product({}))));
    expect(screen.queryByRole("status", { name: "Loading your stock" })).not.toBeInTheDocument();
    expect(screen.getByText("Beef Gravy")).toBeInTheDocument();
});

test("an error offers Retry, which fetches again", async () => {
    const { api, stock } = fakeApi(new Error("boom"), stockOf(product({})));
    render(<StockScreen api={api} />);

    const retry = await screen.findByRole("button", { name: "Retry" });
    expect(screen.getByText("Couldn't load your stock.")).toBeInTheDocument();
    fireEvent.click(retry);
    expect(await screen.findByText("Beef Gravy")).toBeInTheDocument();
    expect(stock).toHaveBeenCalledTimes(2);
});

test("no access shows a short message", async () => {
    const { api } = fakeApi(new NoAccessError("no"));
    render(<StockScreen api={api} />);

    expect(await screen.findByRole("alert")).toHaveTextContent("this account can't use Cupboard Tracker");
});

test("returning to the app reloads the list", async () => {
    const { api, stock } = fakeApi(stockOf(product({ count: 2 })), stockOf(product({ count: 3 })));
    render(<StockScreen api={api} />);
    await screen.findByText("2 items");

    vi.spyOn(document, "visibilityState", "get").mockReturnValue("visible");
    await act(async () => document.dispatchEvent(new Event("visibilitychange")));

    expect(await screen.findByText("3 items")).toBeInTheDocument();
    expect(stock).toHaveBeenCalledTimes(2);
});

test("pull to refresh reloads the list", async () => {
    const { api, stock } = fakeApi(stockOf(product({ count: 2 })), stockOf(product({ count: 4 })));
    const { container } = render(<StockScreen api={api} />);
    await screen.findByText("2 items");
    const surface = container.firstElementChild!;

    fireEvent.touchStart(surface, { touches: [{ clientY: 100 }] });
    fireEvent.touchMove(surface, { touches: [{ clientY: 190 }] });
    expect(screen.getByText("Release to refresh")).toBeInTheDocument();
    await act(async () => fireEvent.touchEnd(surface));

    expect(await screen.findByText("4 items")).toBeInTheDocument();
    expect(stock).toHaveBeenCalledTimes(2);
});

test("a short pull doesn't refresh", async () => {
    const { api, stock } = fakeApi(stockOf(product({ count: 2 })));
    const { container } = render(<StockScreen api={api} />);
    await screen.findByText("2 items");
    const surface = container.firstElementChild!;

    fireEvent.touchStart(surface, { touches: [{ clientY: 100 }] });
    fireEvent.touchMove(surface, { touches: [{ clientY: 130 }] });
    expect(screen.getByText("Pull to refresh")).toBeInTheDocument();
    await act(async () => fireEvent.touchEnd(surface));

    expect(stock).toHaveBeenCalledTimes(1);
});

test("offline: says so, and reloads when back online", async () => {
    const onLine = vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
    const { api, stock } = fakeApi(stockOf(product({})));
    render(<StockScreen api={api} />);

    expect(screen.getByText("You're offline. Connect to see your stock.")).toBeInTheDocument();
    expect(stock).not.toHaveBeenCalled();

    onLine.mockReturnValue(true);
    await act(async () => window.dispatchEvent(new Event("online")));
    expect(await screen.findByText("Beef Gravy")).toBeInTheDocument();
    expect(screen.queryByText(/You're offline/)).not.toBeInTheDocument();
});

afterEach(() => vi.restoreAllMocks());
