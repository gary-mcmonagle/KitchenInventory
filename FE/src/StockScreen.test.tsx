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
    ...overrides,
});

const stockOf = (...products: StockProduct[]): Stock => ({
    generatedAt: "2026-10-17T22:00:00.000Z",
    totalUnits: products.reduce((n, p) => n + p.count, 0),
    useFirstCount: products.filter((p) => p.section === "useFirst").length,
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

test("named products without an estimate are in Later, in the API's order, with counts and meta lines", async () => {
    const { api } = fakeApi(
        stockOf(
            product({ displayName: "Beef Gravy", count: 2 }),
            product({ displayName: "walnut halves", brand: null, size: "200g" }),
        ),
    );
    render(<StockScreen api={api} />);

    const section = await screen.findByRole("region", { name: "Other" }); // no aisle yet (#71)
    expect(within(section).getByText("2 items")).toBeInTheDocument();
    const rows = within(section).getAllByRole("listitem");
    expect(rows.map((r) => r.textContent)).toEqual([
        "BBeef GravyBisto · 230g · ×2OpenRemove",
        "Wwalnut halves200gOpenRemove",
    ]);
    expect(screen.getByText("3 items · 0 to use first")).toBeInTheDocument(); // header summary: units
    expect(screen.queryByRole("region", { name: /Waiting for a name/ })).not.toBeInTheDocument();
});

test("Use first and Later with the mockup's badges, notes, summary and disclaimer", async () => {
    const { api } = fakeApi(
        stockOf(
            product({
                displayName: "Hummus",
                aisleId: "chilledDeli",
                section: "useFirst",
                soonestEstimatedExpiryDate: "2026-10-15",
                daysUntilSoonest: -1,
                shelfLifeDays: 5,
            }),
            product({
                displayName: "Semi-skimmed milk",
                aisleId: "dairyEggs",
                count: 2,
                section: "useFirst",
                soonestEstimatedExpiryDate: "2026-10-18",
                daysUntilSoonest: 2,
                shelfLifeDays: 7,
            }),
            product({
                displayName: "Greek yoghurt",
                aisleId: "dairyEggs",
                section: "later",
                soonestEstimatedExpiryDate: "2026-10-20",
                daysUntilSoonest: 4,
                shelfLifeDays: 14,
            }),
            product({
                displayName: "Baked beans",
                aisleId: "tinsJars",
                section: "later",
                soonestEstimatedExpiryDate: "2028-10-03",
                daysUntilSoonest: 718,
                shelfLifeDays: 730,
            }),
            product({ displayName: "Walnuts", section: "later" }),
            product({ displayName: "Unknown item (1)", status: "noMatch", barcode: "1" }),
        ),
    );
    render(<StockScreen api={api} />);

    const deli = await screen.findByRole("region", { name: "Chilled & deli" });
    expect(screen.getByText("7 items · 2 to use first")).toBeInTheDocument();
    expect(
        screen.getByText(
            "Dates are estimates: the day it was bought plus how long that kind of food usually keeps.",
        ),
    ).toBeInTheDocument();
    const [hummus] = within(deli).getAllByRole("listitem");
    expect(within(deli).getByText("1 to use first").className).toMatch(/_aislePillExpired_/);
    const dairy = screen.getByRole("region", { name: "Dairy & eggs" });
    expect(within(dairy).getByText("1 to use first").className).not.toMatch(/_aislePillExpired_/);
    expect(within(dairy).getByText("2 items")).toBeInTheDocument();
    const [milk, yoghurt] = within(dairy).getAllByRole("listitem"); // use first first, then the rest
    expect(within(hummus).getByText("Est. expired").className).toMatch(/_expired_/);
    expect(within(hummus).getByText("Bought 10 Oct · keeps about 5 days")).toBeInTheDocument();
    expect(within(milk).getByText("Est. 2 days").className).toMatch(/_soon_/);
    expect(within(milk).getByText("Bought 11 Oct · keeps about 7 days")).toBeInTheDocument();

    const [beans] = within(screen.getByRole("region", { name: "Tins & jars" })).getAllByRole("listitem");
    const [walnuts] = within(screen.getByRole("region", { name: "Other" })).getAllByRole("listitem");
    expect(within(yoghurt).getByText("Est. 4 days").className).toMatch(/_later_/);
    expect(within(yoghurt).getByText("Bought 6 Oct · keeps about 2 weeks")).toBeInTheDocument();
    expect(within(beans).getByText("Est. Oct 2028").className).toMatch(/_later_/);
    expect(within(beans).getByText("Bought 4 Oct · keeps about 2 years")).toBeInTheDocument();
    expect(walnuts.textContent).toBe("WWalnutsBisto · 230gOpenRemove"); // no estimate yet: no badge or note

    // Aisles in shop order (#71), then Waiting for a name.
    const sections = screen
        .getAllByRole("region")
        .map((r) => r.getAttribute("aria-label") ?? r.getAttribute("aria-labelledby"));
    expect(sections).toEqual([
        "Dairy & eggs",
        "Chilled & deli",
        "Tins & jars",
        "Other",
        "section-waiting-for-a-name",
    ]);
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

test("Check out and the name link on named rows, Name it on unnamed rows, nothing else tappable", async () => {
    const { api } = fakeApi(
        stockOf(product({}), product({ status: "noMatch", displayName: "Unknown item (1)" })),
    );
    render(<StockScreen api={api} />);

    await screen.findByRole("region", { name: "Other" });
    expect(screen.getAllByRole("button").map((b) => b.getAttribute("aria-label") ?? b.textContent)).toEqual([
        "Use first · 0",
        "Other1 item",
        "Mark one Beef Gravy as opened",
        "Remove one Beef Gravy",
        "Name it",
    ]);
    const unnamed = screen.getByRole("region", { name: /Waiting for a name/ });
    expect(
        within(unnamed)
            .getAllByRole("button")
            .map((b) => b.textContent),
    ).toEqual(["Name it"]);
    // Each row has one link, stretched over the row (#100): a known product's to item detail (#53), an unknown one's
    // to Name it. The header has Scanners and Add (#87).
    expect(screen.getAllByRole("link").map((l) => l.getAttribute("aria-label") ?? l.textContent)).toEqual([
        "Scanners and notifications",
        "Add",
        "Beef Gravy",
        "Unknown item",
    ]);
    expect(screen.getByRole("searchbox", { name: "Search stock" })).toBeInTheDocument(); // #91
    expect(screen.queryByRole("navigation")).not.toBeInTheDocument();
});

test("empty stock", async () => {
    const { api } = fakeApi(stockOf());
    render(<StockScreen api={api} />);

    expect(await screen.findByText(/Nothing in stock yet\./)).toHaveTextContent(
        "Nothing in stock yet.Scan something in.",
    );
    expect(screen.getByText("0 items · 0 to use first")).toBeInTheDocument();
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
    await screen.findByText("2 items · 0 to use first");

    vi.spyOn(document, "visibilityState", "get").mockReturnValue("visible");
    await act(async () => document.dispatchEvent(new Event("visibilitychange")));

    expect(await screen.findByText("3 items · 0 to use first")).toBeInTheDocument();
    expect(stock).toHaveBeenCalledTimes(2);
});

test("pull to refresh reloads the list", async () => {
    const { api, stock } = fakeApi(stockOf(product({ count: 2 })), stockOf(product({ count: 4 })));
    const { container } = render(<StockScreen api={api} />);
    await screen.findByText("2 items · 0 to use first");
    const surface = container.firstElementChild!;

    fireEvent.touchStart(surface, { touches: [{ clientY: 100 }] });
    fireEvent.touchMove(surface, { touches: [{ clientY: 190 }] });
    expect(screen.getByText("Release to refresh")).toBeInTheDocument();
    await act(async () => fireEvent.touchEnd(surface));

    expect(await screen.findByText("4 items · 0 to use first")).toBeInTheDocument();
    expect(stock).toHaveBeenCalledTimes(2);
});

test("a short pull doesn't refresh", async () => {
    const { api, stock } = fakeApi(stockOf(product({ count: 2 })));
    const { container } = render(<StockScreen api={api} />);
    await screen.findByText("2 items · 0 to use first");
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

test("opened and frozen units show as pills, and an opened unit's note says when it was opened (#65)", async () => {
    const { api } = fakeApi(
        stockOf(
            product({
                displayName: "Pesto",
                count: 3,
                openedCount: 1,
                frozenCount: 2,
                soonestEstimatedExpiryDate: "2026-10-20",
                daysUntilSoonest: 2,
                shelfLifeDays: 180,
                soonestState: "opened",
                soonestStateOn: "2026-10-16",
                section: "useFirst",
            }),
            product({ displayName: "Bread", openedCount: 2 }),
        ),
    );
    render(<StockScreen api={api} />);

    const pesto = (await screen.findByText("Pesto")).closest("li")!;
    expect(within(pesto).getByText("Opened", { selector: "span" }).className).toMatch(/_opened_/);
    expect(within(pesto).getByText("2 frozen").className).toMatch(/_frozen_/);
    expect(pesto).toHaveTextContent("Opened 16 Oct");
    expect(pesto).not.toHaveTextContent("Bought");
    expect((await screen.findByText("Bread")).closest("li")).toHaveTextContent("2 opened");
});

test("Opened sits above Check out, opens the next one with an Undo toast, and is greyed out once one's open (#65)", async () => {
    const pesto = product({ displayName: "Pesto", count: 2 });
    const { api } = fakeApi(stockOf(pesto), stockOf({ ...pesto, openedCount: 1 }), stockOf(pesto));
    const openNext = vi.fn(async () => ({ productId: pesto.productId, unit: { unitId: "u-next" } }));
    const undoStateChange = vi.fn(async () => ({ productId: pesto.productId, unit: { unitId: "u-next" } }));
    Object.assign(api, { openNext, undoStateChange });
    render(<StockScreen api={api} />);

    const open = await screen.findByRole("button", { name: "Mark one Pesto as opened" });
    const buttons = within(open.parentElement!).getAllByRole("button");
    expect(buttons.map((b) => b.textContent)).toEqual(["Open", "Remove"]); // Opened first, above
    fireEvent.click(open);

    expect(await screen.findByText("Marked Pesto as opened")).toBeInTheDocument();
    expect(openNext).toHaveBeenCalledWith(pesto.productId);
    const greyed = await screen.findByRole("button", { name: "One Pesto is already open" });
    expect(greyed).toBeDisabled();

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo" })));
    expect(undoStateChange).toHaveBeenCalledWith("u-next");
});

test("Opened is greyed out when every unit is in the freezer", async () => {
    const { api } = fakeApi(stockOf(product({ displayName: "Peas", count: 2, frozenCount: 2 })));
    render(<StockScreen api={api} />);

    expect(await screen.findByRole("button", { name: "Mark one Peas as opened" })).toBeDisabled();
});

// ---- Aisles (#71) and icons (#72)

const aisleStock = () =>
    stockOf(
        product({
            displayName: "Bananas",
            aisleId: "fruitVeg",
            icon: "banana",
            section: "useFirst",
            useFirst: true,
            daysUntilSoonest: 1,
            soonestEstimatedExpiryDate: "2026-10-19",
        }),
        product({ displayName: "Leeks", aisleId: "fruitVeg", icon: null }),
        product({ displayName: "Beef Gravy", aisleId: "saucesOils", icon: "bottle-one" }),
        product({ displayName: "Mystery", aisleId: "other", icon: null }),
        product({ displayName: "Unknown item (1)", status: "noMatch", barcode: "1" }),
    );

test("aisles start open, and closing one is remembered on this device (Gary, 2026-10-18)", async () => {
    window.localStorage.removeItem("cupboard.aisles.open");
    const { api } = fakeApi(aisleStock());
    const { unmount } = render(<StockScreen api={api} />);

    const fruit = await screen.findByRole("button", { name: /Fruit & veg/ });
    expect(fruit).toHaveAttribute("aria-expanded", "true");
    expect(fruit).toHaveTextContent("Fruit & veg1 to use first2 items");
    const sauces = screen.getByRole("button", { name: /Sauces, oils & condiments/ });
    expect(sauces).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText("Beef Gravy")).toBeInTheDocument();

    fireEvent.click(sauces);
    expect(screen.queryByText("Beef Gravy")).not.toBeInTheDocument();
    unmount();

    render(<StockScreen api={fakeApi(aisleStock()).api} />);
    await screen.findByText("Leeks");
    expect(screen.queryByText("Beef Gravy")).not.toBeInTheDocument(); // still closed on this device
});

test("broken storage falls back to the defaults", async () => {
    const getItem = vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
        throw new Error("blocked");
    });
    render(<StockScreen api={fakeApi(aisleStock()).api} />);

    expect(await screen.findByRole("button", { name: /Fruit & veg/ })).toHaveAttribute(
        "aria-expanded",
        "true",
    );
    expect(screen.getByRole("button", { name: /Other/ })).toHaveAttribute("aria-expanded", "true");
    getItem.mockRestore();
});

test("Use first shows only what needs using, in expanded aisles, and turning it off restores the view", async () => {
    window.localStorage.removeItem("cupboard.aisles.open");
    render(<StockScreen api={fakeApi(aisleStock()).api} />);
    const toggle = await screen.findByRole("button", { name: "Use first · 1" });
    expect(toggle).toHaveAttribute("aria-pressed", "false");

    fireEvent.click(toggle);

    expect(toggle).toHaveAttribute("aria-pressed", "true");
    expect(screen.getAllByRole("region").map((r) => r.getAttribute("aria-label"))).toEqual(["Fruit & veg"]);
    expect(screen.getByText("Bananas")).toBeInTheDocument();
    expect(screen.queryByText("Leeks")).not.toBeInTheDocument();
    expect(screen.queryByText("Unknown item")).not.toBeInTheDocument();

    fireEvent.click(toggle);
    expect(screen.getByRole("button", { name: /Sauces/ })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText("Leeks")).toBeInTheDocument();
});

test("with nothing to use first, the toggle still works and says so", async () => {
    render(
        <StockScreen api={fakeApi(stockOf(product({ displayName: "Leeks", aisleId: "fruitVeg" }))).api} />,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Use first · 0" }));

    expect(screen.getByText("Nothing needs using up right now.")).toBeInTheDocument();
});

test("tiles show the product's icon, else its aisle's, else the letter", async () => {
    render(<StockScreen api={fakeApi(aisleStock()).api} />);

    const tile = (name: string) =>
        (screen.getByText(name).closest("li") as HTMLElement).querySelector("[aria-hidden='true']")!;
    await screen.findByText("Bananas");
    expect(tile("Bananas").querySelector("svg")).not.toBeNull();
    expect(tile("Leeks").querySelector("svg")).not.toBeNull(); // the Fruit & veg icon
    expect(tile("Mystery").textContent).toBe("M"); // Other: the letter
});

test("grouping: shop order, use first first within an aisle, unknown items apart", async () => {
    const { groupByAisle } = await import("./StockScreen");
    const groups = groupByAisle(aisleStock().products);

    expect(groups.map((g) => g.aisle.id)).toEqual(["fruitVeg", "saucesOils", "other"]);
    expect(groups[0].products.map((p) => p.displayName)).toEqual(["Bananas", "Leeks"]);
    expect([groups[0].useFirstCount, groups[0].anyExpired]).toEqual([1, false]);
});

// ---- Generic names as the row title (#92, option A)

test("a generic name leads the row with the full name and size underneath; same or none looks as before", async () => {
    const { api } = fakeApi(
        stockOf(
            product({
                displayName: "Cravendale Filtered Semi Skimmed Milk",
                brand: "Cravendale",
                size: "2 pint",
                genericName: "Semi-skimmed milk",
                aisleId: "dairyEggs",
                icon: null,
            }),
            product({
                displayName: "Asda Semi Skimmed British Milk",
                brand: "Asda",
                size: "2 litre",
                count: 2,
                genericName: "Semi-skimmed milk",
                aisleId: "dairyEggs",
                icon: null,
            }),
            product({
                displayName: "Bananas",
                brand: null,
                size: null,
                genericName: " bananas ",
                aisleId: "fruitVeg",
            }),
            product({
                displayName: "Beef Gravy",
                brand: "Bisto",
                size: "230g",
                genericName: null,
                aisleId: "saucesOils",
            }),
        ),
    );
    render(<StockScreen api={api} />);

    const dairy = await screen.findByRole("region", { name: "Dairy & eggs" });
    const rows = within(dairy).getAllByRole("listitem");
    expect(
        rows.map((r) => [
            within(r).getByRole("link").textContent,
            r.querySelector('[class*="meta"]')?.textContent,
        ]),
    ).toEqual([
        ["Semi-skimmed milk", "Asda Semi Skimmed British Milk · 2 litre · ×2"],
        ["Semi-skimmed milk", "Cravendale Filtered Semi Skimmed Milk · 2 pint"],
    ]);
    expect(rows[0].textContent?.startsWith("S")).toBe(true); // the tile's letter follows the title; order unchanged (by full name)
    expect(
        within(rows[1]).getByRole("button", { name: "Remove one Cravendale Filtered Semi Skimmed Milk" }),
    ).toBeInTheDocument();

    const bananas = within(screen.getByRole("region", { name: "Fruit & veg" })).getByRole("listitem");
    expect(within(bananas).getByRole("link")).toHaveTextContent("Bananas");
    expect(
        within(screen.getByRole("region", { name: "Sauces, oils & condiments" })).getByRole("listitem")
            .textContent,
    ).toContain("Beef GravyBisto · 230g");
});

test("checking out a generically titled row names the exact product in the toast", async () => {
    const milk = product({
        displayName: "Cravendale Filtered Semi Skimmed Milk",
        size: "2 pint",
        genericName: "Semi-skimmed milk",
        aisleId: "dairyEggs",
    });
    const { api } = fakeApi(stockOf(milk));
    const checkOut = vi.fn(async () => ({
        unitId: "u1",
        productId: milk.productId,
        displayName: milk.displayName,
        remainingCount: 0,
    }));
    (api as unknown as { checkOut: typeof checkOut }).checkOut = checkOut;
    render(<StockScreen api={api} />);

    const remove = await screen.findByRole("button", {
        name: "Remove one Cravendale Filtered Semi Skimmed Milk",
    });
    await act(async () => fireEvent.click(remove));

    expect(screen.getByText("Checked out Cravendale Filtered Semi Skimmed Milk")).toBeInTheDocument();
});

test("a tapped stock change push (/?removed=<batch>) opens “Add to the shopping list?” once (#89)", async () => {
    const batch = "0f8fad5b-d9cb-469f-a165-70867728950e";
    window.history.replaceState(null, "", `/?removed=${batch}`);
    const runOutForBatch = vi.fn(async () => ({
        checkedOutBy: "Sam",
        at: null,
        items: [
            {
                productId: "p",
                genericNameId: "milk",
                name: "Milk",
                left: 0,
                onList: false,
                addWhenOut: false,
                canSet: true,
            },
        ],
    }));
    const api = { stock: vi.fn(async () => stockOf(product({}))), runOutForBatch } as unknown as ApiClient;
    render(<StockScreen api={api} />);

    expect(await screen.findByRole("dialog", { name: "Add to the shopping list?" })).toBeInTheDocument();
    expect(runOutForBatch).toHaveBeenCalledWith(batch);
    expect(window.location.search).toBe(""); // a reload or Back doesn't open it again
    fireEvent.click(screen.getByRole("button", { name: "Not now" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});
