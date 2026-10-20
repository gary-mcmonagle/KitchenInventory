import { act, fireEvent, render, screen, within } from "@testing-library/react";
import type { ApiClient, Stock, StockProduct } from "./api";
import { matchesSearch, normalise, searchWords } from "./stockSearch";
import { forgetStockSearch, StockScreen } from "./StockScreen";

// Search the stock list as you type (#91).

const product = (overrides: Partial<StockProduct>): StockProduct => ({
    productId: crypto.randomUUID(),
    barcode: "5000354922831",
    displayName: "Beef Gravy",
    brand: "Bisto",
    size: "230g",
    count: 1,
    latestCheckedInAt: "2026-10-17T18:00:00.000Z",
    status: "known",
    section: overrides.status && overrides.status !== "known" ? "waitingForName" : "later",
    soonestEstimatedExpiryDate: null,
    daysUntilSoonest: null,
    shelfLifeDays: null,
    openedCount: 0,
    frozenCount: 0,
    soonestState: null,
    soonestStateOn: null,
    aisleId: "other",
    icon: null,
    useFirst: false,
    genericName: null,
    ...overrides,
});

const matches = (p: StockProduct, q: string) => matchesSearch(p, searchWords(q));

describe("matching", () => {
    const milk = product({
        displayName: "SEMI-SKIMMED BRITISH MILK",
        brand: "Tesco",
        genericName: "Semi-skimmed milk",
        aisleId: "dairyEggs",
        barcode: "5000169012345",
    });

    test.each([
        ["semi", true], // part of the name
        ["TESCO", true], // brand, any case
        ["skimmed milk", true], // generic name, every word
        ["dairy", true], // aisle name
        ["eggs", true], // aisle name after "&"
        ["5000169", true], // barcode digits
        ["5000 1690 12345", true], // barcode with spaces
        ["tesco beans", false], // every word must match
        ["oat", false],
        ["", true],
    ])("%s → %s", (q, expected) => expect(matches(milk, q)).toBe(expected));

    test("accents and & don't matter; no generic name is fine", () => {
        const creme = product({
            displayName: "Crème fraîche",
            brand: null,
            genericName: null,
            aisleId: "dairyEggs",
        });
        expect(matches(creme, "creme")).toBe(true);
        expect(matches(creme, "FRAICHE")).toBe(true);
        expect(matches(product({ displayName: "Mac & cheese", brand: null }), "mac cheese")).toBe(true);
        expect(normalise("  Fish &  Chips ")).toBe("fish chips");
        expect(matches(product({ displayName: "Leeks", brand: null, barcode: null }), "123")).toBe(false);
    });
});

const stockOf = (...products: StockProduct[]): Stock => ({
    generatedAt: "2026-10-20T08:00:00.000Z",
    totalUnits: products.reduce((n, p) => n + p.count, 0),
    useFirstCount: products.filter((p) => p.section === "useFirst").length,
    products,
});

const STOCK = () =>
    stockOf(
        product({
            displayName: "Bananas",
            brand: null,
            aisleId: "fruitVeg",
            section: "useFirst",
            daysUntilSoonest: 1,
            soonestEstimatedExpiryDate: "2026-10-21",
        }),
        product({ displayName: "Leeks", brand: null, aisleId: "fruitVeg" }),
        product({
            displayName: "Mature cheddar",
            brand: "Tesco",
            aisleId: "dairyEggs",
            genericName: "Mature cheddar",
        }),
        product({
            displayName: "Baked Beans",
            brand: "Tesco",
            aisleId: "tinsJars",
            genericName: "Baked beans",
        }),
        product({ displayName: "Beef Gravy", brand: "Bisto", aisleId: "saucesOils" }),
        product({
            displayName: "Unknown item (20055363)",
            status: "noMatch",
            barcode: "20055363",
            brand: null,
        }),
    );

function setup(stock: Stock = STOCK()) {
    const api = { stock: vi.fn(async () => stock) } as unknown as ApiClient;
    const utils = render(<StockScreen api={api} />);
    return { api, ...utils };
}

const box = () => screen.getByRole("searchbox", { name: "Search stock" });
const type = (text: string) => fireEvent.change(box(), { target: { value: text } });
const regions = () => screen.getAllByRole("region").map((r) => r.getAttribute("aria-label"));

beforeEach(() => {
    forgetStockSearch();
    window.localStorage.removeItem("cupboard.aisles.open");
});

test("the box is as drawn: label, placeholder, search type and Enter hint; no clear button until there's text", async () => {
    setup();
    await screen.findByText("Leeks");
    expect(box()).toHaveAttribute("placeholder", "Search your stock");
    expect(box()).toHaveAttribute("type", "search");
    expect(box()).toHaveAttribute("enterkeyhint", "search");
    expect(screen.queryByRole("button", { name: "Clear search" })).not.toBeInTheDocument();
    type("l");
    expect(screen.getByRole("button", { name: "Clear search" }).className).toMatch(/searchClear/);
});

test("filters on every keystroke, keeping matching aisles open with their match counts and hiding the rest", async () => {
    setup();
    await screen.findByText("Leeks");

    type("t");
    type("te");
    type("tesco");
    expect(regions()).toEqual(["Dairy & eggs", "Tins & jars"]);
    expect(screen.getByRole("button", { name: /Dairy & eggs/ })).toHaveTextContent("1 item");

    type("tesco bean");
    expect(regions()).toEqual(["Tins & jars"]);
    expect(screen.getByText("Baked Beans")).toBeInTheDocument();
    expect(screen.queryByText("Mature cheddar")).not.toBeInTheDocument();
});

test("matching aisles open even if closed on this device, and clearing puts the closed state back unchanged", async () => {
    setup();
    const fruit = await screen.findByRole("button", { name: /Fruit & veg/ });
    fireEvent.click(fruit); // close it
    expect(screen.queryByText("Leeks")).not.toBeInTheDocument();
    const remembered = window.localStorage.getItem("cupboard.aisles.open");

    type("leek");
    expect(screen.getByRole("button", { name: /Fruit & veg/ })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText("Leeks")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Clear search" }));
    expect(box()).toHaveValue("");
    expect(screen.getByRole("button", { name: /Fruit & veg/ })).toHaveAttribute("aria-expanded", "false");
    expect(window.localStorage.getItem("cupboard.aisles.open")).toBe(remembered);
});

test("Waiting for a name shows only when one of its items matches (e.g. by barcode)", async () => {
    setup();
    await screen.findByText("Leeks");

    type("leeks");
    expect(screen.queryByRole("region", { name: /Waiting for a name/ })).not.toBeInTheDocument();

    type("2005 5363");
    expect(screen.getAllByRole("region")).toHaveLength(1);
    expect(screen.getByRole("region", { name: /Waiting for a name/ })).toHaveTextContent("Barcode 2005 5363");
});

test("search and Use first work together, and the Use first count stays the overall number", async () => {
    setup();
    await screen.findByText("Leeks");

    type("fruit");
    fireEvent.click(screen.getByRole("button", { name: "Use first · 1" }));

    expect(regions()).toEqual(["Fruit & veg"]);
    expect(screen.getByText("Bananas")).toBeInTheDocument();
    expect(screen.queryByText("Leeks")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Use first · 1" })).toBeInTheDocument();
});

test("no matches shows the mockup's message", async () => {
    setup();
    await screen.findByText("Leeks");
    type("xyzzy");
    expect(screen.getByText(/Nothing matches that\./)).toHaveTextContent(
        "Nothing matches that.Try another word or filter.",
    );
    expect(screen.queryAllByRole("region")).toHaveLength(0);
});

test("Escape clears it", async () => {
    setup();
    await screen.findByText("Leeks");
    type("leek");
    fireEvent.keyDown(box(), { key: "Escape" });
    expect(box()).toHaveValue("");
    expect(screen.getByText("Beef Gravy")).toBeInTheDocument();
});

test("the query survives leaving the stock list and coming back, but not a reload", async () => {
    const { unmount } = setup();
    await screen.findByText("Leeks");
    type("gravy");
    unmount(); // e.g. to item detail

    setup();
    await screen.findByText("Beef Gravy");
    expect(box()).toHaveValue("gravy");
    expect(screen.queryByText("Leeks")).not.toBeInTheDocument();
    expect(window.localStorage.getItem("cupboard.stock.search")).toBeNull(); // never stored on the device

    forgetStockSearch(); // what a reload does
});

test("a refetch keeps the query applied", async () => {
    const first = STOCK();
    const second = stockOf(
        ...first.products,
        product({ displayName: "Gravy granules", brand: "Bisto", aisleId: "saucesOils" }),
    );
    const stock = vi.fn().mockResolvedValueOnce(first).mockResolvedValue(second);
    render(<StockScreen api={{ stock } as unknown as ApiClient} />);
    await screen.findByText("Leeks");
    type("gravy");

    await act(async () => window.dispatchEvent(new Event("online")));
    await act(async () => document.dispatchEvent(new Event("visibilitychange")));

    const sauces = screen.getByRole("region", { name: "Sauces, oils & condiments" });
    expect(stock.mock.calls.length).toBeGreaterThan(1);
    expect(within(sauces).getAllByRole("listitem")).toHaveLength(2);
    expect(screen.queryByText("Leeks")).not.toBeInTheDocument();
});
