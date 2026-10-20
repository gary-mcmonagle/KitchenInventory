import { act, fireEvent, render, screen, within } from "@testing-library/react";
import itemCss from "./Item.module.css?raw";
import stockCss from "./Stock.module.css?raw";
import type { ApiClient, Product, Stock, StockProduct } from "./api";
import { rememberItemPreview } from "./itemPreview";
import { ItemScreen, SLOW_MS } from "./ItemScreen";
import { StockScreen } from "./StockScreen";

// Tap anywhere on a stock row, and item detail opens instantly (#100).

const row = (overrides: Partial<StockProduct> = {}): StockProduct => ({
    productId: "p-milk",
    barcode: "5000169012345",
    displayName: "Cravendale Filtered Semi Skimmed Milk",
    brand: "Cravendale",
    size: "2 pint",
    count: 5,
    latestCheckedInAt: "2026-10-18T09:00:00.000Z",
    status: "known",
    section: "useFirst",
    soonestEstimatedExpiryDate: "2026-10-22",
    daysUntilSoonest: 2,
    shelfLifeDays: 7,
    openedCount: 0,
    frozenCount: 0,
    soonestState: null,
    soonestStateOn: null,
    aisleId: "dairyEggs",
    icon: null,
    useFirst: true,
    genericName: "Semi-skimmed milk",
    ...overrides,
});

const product = (): Product => ({
    productId: "p-milk",
    barcode: "5000169012345",
    status: "known",
    displayName: "Cravendale Filtered Semi Skimmed Milk",
    brand: "Cravendale",
    size: "2 pint",
    inStockCount: 1,
    latestCheckedInAt: "2026-10-18T09:00:00.000Z",
    latestDeviceRole: "in",
    shelfLifeDays: 7,
    shelfLifeSource: "jev",
    usualStorage: "fridge",
    opened: null,
    frozen: null,
    defrosted: null,
    openedCount: 0,
    frozenCount: 0,
    aisleId: "dairyEggs",
    aisleSource: "jev",
    icon: null,
    iconSource: null,
    genericName: { id: "semi-skimmed milk", name: "Semi-skimmed milk", source: "jevMatch" },
    units: [
        {
            unitId: "u1",
            checkedInAt: "2026-10-18T09:00:00.000Z",
            estimatedExpiryDate: "2026-10-22",
            expirySource: "shelfLife",
            daysUntilExpiry: 2,
            openedOn: null,
            frozenOn: null,
            defrostedOn: null,
            currentStorage: null,
            estimatePending: false,
            canUndoStateChange: false,
        },
    ],
});

beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-10-20T09:00:00Z"));
    window.history.pushState(null, "", "/");
});
afterEach(() => vi.useRealTimers());

// ---- The row

test("each row has one link, stretched over the row; the row's buttons aren't inside it and don't navigate", async () => {
    const stock: Stock = { generatedAt: "", totalUnits: 5, useFirstCount: 1, products: [row()] };
    const checkOut = vi.fn(async () => ({
        unitId: "u1",
        productId: "p-milk",
        displayName: "Cravendale Filtered Semi Skimmed Milk",
        remainingCount: 4,
    }));
    const api = { stock: vi.fn(async () => stock), checkOut } as unknown as ApiClient;
    render(<StockScreen api={api} />);

    const li = within(await screen.findByRole("region", { name: "Dairy & eggs" })).getByRole("listitem");
    const links = within(li).getAllByRole("link");
    expect(links).toHaveLength(1);
    expect(links[0].className).toMatch(/nameLink/);
    expect(li.className).toMatch(/row/);
    for (const button of within(li).getAllByRole("button")) expect(links[0].contains(button)).toBe(false);

    await act(async () => fireEvent.click(within(li).getByRole("button", { name: /^Remove one/ })));
    expect(checkOut).toHaveBeenCalled();
    expect(window.location.pathname).toBe("/");

    fireEvent.click(links[0]);
    expect(window.location.pathname).toBe("/products/p-milk");
});

test("the row's styles: the link covers the row, buttons sit above it, pressed and focus states", () => {
    expect(stockCss).toMatch(/\.nameLink::after\s*{[^}]*position:\s*absolute;[^}]*inset:\s*0/);
    expect(stockCss).toMatch(/\.row\s*{[^}]*position:\s*relative/);
    expect(stockCss).toMatch(/z-index:\s*1/);
    expect(stockCss).toMatch(/\.row:has\(\.nameLink:active\)\s*{[^}]*background:\s*var\(--ground\)/);
    expect(stockCss).toMatch(
        /\.row:has\(\.nameLink:focus-visible\)\s*{[^}]*outline:\s*2px solid var\(--accent\)/,
    );
});

// ---- Item detail opening instantly

const never = () => new Promise<Product>(() => undefined);

test("opened from the list, the heading, estimate and placeholders show before the product loads", () => {
    rememberItemPreview(row());
    render(<ItemScreen api={{ product: vi.fn(never) } as unknown as ApiClient} productId="p-milk" />);

    expect(
        screen.getByRole("heading", { level: 1, name: "Cravendale Filtered Semi Skimmed Milk" }),
    ).toBeInTheDocument();
    expect(screen.getByText(/^Listed as/)).toHaveTextContent("Listed as Semi-skimmed milk");
    expect(screen.getByText("Cravendale · 2 pint")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Edit details" })).toBeInTheDocument();
    expect(screen.getByText("Thursday 22 October")).toBeInTheDocument();
    expect(screen.getByRole("status")).toHaveTextContent("Loading");
    expect(screen.getAllByTestId("unit-skeleton")).toHaveLength(3); // one per known unit, up to 3
    expect(screen.getByTestId("units-skeleton")).toHaveAttribute("aria-busy", "true");
    expect(screen.getByTestId("estimate-skeleton")).toHaveAttribute("aria-busy", "true");
    expect(screen.queryByTestId("heading-skeleton")).not.toBeInTheDocument();
});

test("when the product arrives the placeholders are replaced in place, with the same heading", async () => {
    rememberItemPreview(row());
    let finish!: (p: Product) => void;
    render(
        <ItemScreen
            api={{ product: vi.fn(() => new Promise<Product>((r) => (finish = r))) } as unknown as ApiClient}
            productId="p-milk"
        />,
    );
    const heading = screen.getByRole("heading", { level: 1 }).textContent;

    await act(async () => finish(product()));

    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe(heading);
    expect(screen.queryByTestId("unit-skeleton")).not.toBeInTheDocument();
    expect(screen.getByRole("region", { name: "In the cupboard" })).toBeInTheDocument();
});

test("opened directly, the heading is a placeholder too, never a blank screen", () => {
    render(<ItemScreen api={{ product: vi.fn(never) } as unknown as ApiClient} productId="p-other" />);

    expect(screen.getByTestId("heading-skeleton")).toHaveAttribute("aria-busy", "true");
    expect(screen.queryByRole("heading", { level: 1 })).not.toBeInTheDocument();
    expect(screen.getAllByTestId("unit-skeleton").length).toBeGreaterThan(0);
});

test("a slow load says Still loading… after a while", async () => {
    vi.useRealTimers();
    vi.useFakeTimers({ toFake: ["Date", "setTimeout", "clearTimeout"] });
    vi.setSystemTime(new Date("2026-10-20T09:00:00Z"));
    render(<ItemScreen api={{ product: vi.fn(never) } as unknown as ApiClient} productId="p-slow" />);
    expect(screen.queryByText("Still loading…")).not.toBeInTheDocument();

    act(() => vi.advanceTimersByTime(SLOW_MS));

    expect(screen.getByText("Still loading…")).toBeInTheDocument();
});

test("a failed load shows the amber card with Try again, keeping the heading from the list", async () => {
    rememberItemPreview(row());
    const productCall = vi
        .fn()
        .mockRejectedValueOnce(new TypeError("Load failed"))
        .mockResolvedValue(product());
    render(<ItemScreen api={{ product: productCall } as unknown as ApiClient} productId="p-milk" />);

    const card = await screen.findByRole("alert");
    expect(card).toHaveTextContent("Couldn't load this item.");
    expect(card.className).toMatch(/errorCard/);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
        "Cravendale Filtered Semi Skimmed Milk",
    );

    await act(async () => fireEvent.click(within(card).getByRole("button", { name: "Try again" })));
    expect(screen.getByRole("region", { name: "In the cupboard" })).toBeInTheDocument();
});

test("the placeholders don't shimmer with reduced motion", () => {
    expect(itemCss).toMatch(
        /@media \(prefers-reduced-motion: reduce\)\s*{\s*\.skeleton\s*{\s*animation:\s*none/,
    );
});
