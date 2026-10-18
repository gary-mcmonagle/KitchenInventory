import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { ApiError, type ApiClient, type CheckedOut, type Product, type ProductUnit } from "./api";
import { ItemScreen, unitLine } from "./ItemScreen";
import { NameScreen } from "./NameScreen";
import { StockScreen } from "./StockScreen";

/** Status messages other than the scan feed region (#54). */
const notFeed = { name: (name: string) => name !== "Scan feed" };

// Dates show the year only when it isn't this year, so pin "today" (only Date is faked; timers stay real).
beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-10-18T09:00:00Z"));
});
afterEach(() => vi.useRealTimers());

test("a date in another year shows the year", async () => {
    setup([milk({ units: [unit("u1", { estimatedExpiryDate: "2028-10-16", daysUntilExpiry: 729 })] })]);
    expect(await screen.findByText("Monday 16 October 2028")).toBeInTheDocument();
    expect(screen.getByText("Bought 13 Oct · est. 16 Oct 2028")).toBeInTheDocument();
});

const unit = (id: string, overrides: Partial<ProductUnit> = {}): ProductUnit => ({
    unitId: id,
    checkedInAt: "2026-10-13T09:00:00.000Z",
    estimatedExpiryDate: "2026-10-20",
    expirySource: "shelfLife",
    daysUntilExpiry: 2,
    openedOn: null,
    frozenOn: null,
    defrostedOn: null,
    currentStorage: null,
    estimatePending: false,
    canUndoStateChange: false,
    ...overrides,
});

const milk = (overrides: Partial<Product> = {}): Product => ({
    productId: "p-milk",
    barcode: "4056489899112",
    status: "known",
    displayName: "Semi-skimmed milk",
    brand: "Dairy Manor",
    size: "2 pint",
    inStockCount: 2,
    latestCheckedInAt: "2026-10-15T09:00:00.000Z",
    latestDeviceRole: "in",
    shelfLifeDays: 7,
    shelfLifeSource: "jev",
    usualStorage: "fridge",
    opened: { days: 4, storage: "fridge", source: "jev" },
    frozen: { days: 90, storage: "freezer", source: "jev" },
    defrosted: { days: 2, storage: "fridge", source: "jev" },
    openedCount: 0,
    frozenCount: 0,
    units: [
        unit("u1"),
        unit("u2", {
            checkedInAt: "2026-10-15T09:00:00.000Z",
            estimatedExpiryDate: "2026-10-22",
            daysUntilExpiry: 4,
        }),
    ],
    ...overrides,
});

function setup(products: Product[], overrides: Partial<Record<keyof ApiClient, unknown>> = {}) {
    const queue = [...products];
    const api = {
        product: vi.fn(async () => (queue.length > 1 ? queue.shift()! : queue[0])),
        checkOutUnit: vi.fn(async (unitId: string): Promise<CheckedOut> => ({
            unitId,
            productId: "p-milk",
            displayName: "Semi-skimmed milk",
            remainingCount: 1,
        })),
        undoCheckOut: vi.fn(async (unitId: string) => ({
            unitId,
            productId: "p-milk",
            count: 2,
            restored: true,
        })),
        setUnitExpiry: vi.fn(async (unitId: string, date: string) => ({ unitId, estimatedExpiryDate: date })),
        setShelfLife: vi.fn(async (productId: string, days: number) => ({ productId, shelfLifeDays: days })),
        editDetails: vi.fn(async (productId: string) => ({ productId, displayName: "x" })),
        ...overrides,
    };
    window.history.pushState(null, "", "/products/p-milk");
    render(<ItemScreen api={api as unknown as ApiClient} productId="p-milk" />);
    return api;
}

test("heading, use-by card, units and barcode as in the mockup", async () => {
    setup([milk()]);

    expect(await screen.findByRole("heading", { level: 1, name: "Semi-skimmed milk" })).toBeInTheDocument();
    expect(screen.getByText("Dairy Manor · 2 pint")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Edit details" })).toHaveAttribute(
        "href",
        "/products/p-milk/edit",
    );
    expect(screen.getByRole("button", { name: "Back to stock" })).toBeInTheDocument();

    expect(screen.getByText("Tuesday 20 October")).toBeInTheDocument();
    const card = screen.getByText("Estimated use by").parentElement!;
    expect(card.className).toMatch(/_soon_/);
    expect(card).toHaveTextContent("In 2 days. Bought 13 Oct; Semi-skimmed milk usually keeps about 7 days.");

    const units = within(screen.getByRole("region", { name: "In the cupboard" })).getAllByRole("listitem");
    expect(units.map((u) => u.textContent)).toEqual([
        "Unit 1Bought 13 Oct · est. 20 OctCheck out",
        "Unit 2Bought 15 Oct · est. 22 OctCheck out",
    ]);
    expect(screen.getByText("Barcode 4056 4898 9911 2")).toBeInTheDocument();
});

test.each([
    [-3, "_expired_", "Expired 3 days ago."],
    [-1, "_expired_", "Expired yesterday."],
    [0, "_soon_", "Today."],
    [1, "_soon_", "Tomorrow."],
    [3, "_soon_", "In 3 days."],
    [4, "_later_", "In 4 days."],
])("use-by card at %i days", async (days, tone, text) => {
    setup([milk({ units: [unit("u1", { daysUntilExpiry: days })] })]);
    const card = (await screen.findByText("Estimated use by")).parentElement!;
    expect(card.className).toMatch(new RegExp(tone));
    expect(card).toHaveTextContent(text);
});

test("no estimate yet is neutral", async () => {
    setup([
        milk({
            shelfLifeDays: null,
            units: [unit("u1", { estimatedExpiryDate: null, daysUntilExpiry: null, expirySource: null })],
        }),
    ]);
    const card = (await screen.findByText("Estimated use by")).parentElement!;
    expect(card.className).toMatch(/_neutral_/);
    expect(card).toHaveTextContent("No estimate yet");
    expect(screen.getByText("Bought 13 Oct · no estimate yet")).toBeInTheDocument();
});

test("dates set by hand are marked", () => {
    expect(unitLine(unit("u", { expirySource: "manual" }))).toBe("Bought 13 Oct · est. 20 Oct (set by you)");
    // 23:30 UTC on 13 Oct is the 14th in the UK.
    expect(unitLine(unit("u", { checkedInAt: "2026-10-13T23:30:00.000Z" }))).toBe(
        "Bought 14 Oct · est. 20 Oct",
    );
});

test("check out a unit: toast, then Undo puts it back", async () => {
    const api = setup([milk(), milk({ units: [unit("u1")] }), milk()]);
    await screen.findByRole("button", { name: "Check out unit 2" });

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Check out unit 2" })));

    expect(api.checkOutUnit).toHaveBeenCalledWith("u2");
    expect(screen.getByRole("status", notFeed)).toHaveTextContent("Checked out Semi-skimmed milk");
    expect(screen.queryByRole("button", { name: "Check out unit 2" })).not.toBeInTheDocument(); // reloaded

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo" })));

    expect(api.undoCheckOut).toHaveBeenCalledWith("u2");
    expect(await screen.findByRole("button", { name: "Check out unit 2" })).toBeInTheDocument();
});

test("checking out the last unit returns to the stock list", async () => {
    const api = setup([milk({ units: [unit("u1")] })], {
        checkOutUnit: vi.fn(async (unitId: string) => ({
            unitId,
            productId: "p-milk",
            displayName: "x",
            remainingCount: 0,
        })),
    });
    await screen.findByText("Unit 1");

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Check out unit 1" })));

    expect(api.checkOutUnit).toHaveBeenCalledWith("u1");
    expect(window.location.pathname).toBe("/");
});

test("a unit already gone says so", async () => {
    setup([milk()], { checkOutUnit: vi.fn(async () => Promise.reject(new ApiError(409, "gone"))) });
    await screen.findByRole("button", { name: "Check out unit 1" });

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Check out unit 1" })));

    expect(screen.getByRole("status", notFeed)).toHaveTextContent("That one's already been checked out.");
});

test("save a date for the chosen unit", async () => {
    const api = setup([milk()]);
    await screen.findByText("Estimate looks wrong?");
    expect(screen.getByLabelText("Use by date for unit 1")).toHaveValue("2026-10-20"); // the soonest by default

    fireEvent.change(screen.getByLabelText("Unit"), { target: { value: "u2" } });
    expect(screen.getByLabelText("Use by date for unit 2")).toHaveValue("2026-10-22");
    fireEvent.change(screen.getByLabelText("Use by date for unit 2"), { target: { value: "2026-10-25" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save date" })));

    expect(api.setUnitExpiry).toHaveBeenCalledWith("u2", "2026-10-25");
    expect(screen.getByRole("status", notFeed)).toHaveTextContent("Saved the date for unit 2.");
});

test("one unit: no unit picker", async () => {
    setup([milk({ units: [unit("u1")] })]);
    await screen.findByText("Estimate looks wrong?");
    expect(screen.queryByLabelText("Unit")).not.toBeInTheDocument();
});

test("change shelf life: the current value is preselected, and saving applies to new purchases", async () => {
    const api = setup([milk()]);
    await screen.findByText("Estimate looks wrong?");
    expect(screen.getByText(/applies to Semi-skimmed milk you buy from now on/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Change shelf life" }));
    const select = screen.getByLabelText("Semi-skimmed milk usually keeps");
    expect(select).toHaveValue("7");
    expect(
        within(select)
            .getAllByRole("option")
            .map((o) => o.textContent),
    ).toEqual([
        "3 days",
        "5 days",
        "1 week",
        "2 weeks",
        "1 month",
        "3 months",
        "6 months",
        "1 year",
        "2 years",
    ]);
    fireEvent.change(select, { target: { value: "14" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save shelf life" })));

    expect(api.setShelfLife).toHaveBeenCalledWith("p-milk", 14);
    expect(screen.getByRole("status", notFeed)).toHaveTextContent(
        "Saved. It applies to ones you buy from now on.",
    );
});

test("a current shelf life that isn't in the list is still offered", async () => {
    setup([milk({ shelfLifeDays: 1095 })]);
    fireEvent.click(await screen.findByRole("button", { name: "Change shelf life" }));
    const select = screen.getByLabelText("Semi-skimmed milk usually keeps");
    expect(select).toHaveValue("1095");
    expect(within(select).getAllByRole("option").at(-1)!.textContent).toBe("3 years");
});

test("back goes to the stock list", async () => {
    setup([milk()]);
    fireEvent.click(await screen.findByRole("button", { name: "Back to stock" }));
    expect(window.location.pathname).toBe("/");
});

test("Edit details opens the form prefilled; Save and Cancel go back to item detail", async () => {
    const api = {
        product: vi.fn(async () => milk()),
        editDetails: vi.fn(async (productId: string) => ({ productId, displayName: "Milk" })),
    };
    window.history.pushState(null, "", "/products/p-milk/edit");
    render(<NameScreen api={api as unknown as ApiClient} productId="p-milk" mode="edit" />);

    expect(await screen.findByRole("heading", { name: "Edit product" })).toBeInTheDocument();
    expect(screen.getByLabelText("Name")).toHaveValue("Semi-skimmed milk");
    expect(screen.getByLabelText("Brand")).toHaveValue("Dairy Manor");
    expect(screen.getByLabelText("Size")).toHaveValue("2 pint");
    expect(screen.getByRole("button", { name: "Cancel" })).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: " Milk " } });
    fireEvent.change(screen.getByLabelText("Size"), { target: { value: "" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save" })));

    expect(api.editDetails).toHaveBeenCalledWith("p-milk", {
        name: "Milk",
        brand: "Dairy Manor",
        size: undefined,
    });
    expect(window.location.pathname).toBe("/products/p-milk");
});

test("Edit details validates like naming", async () => {
    const api = { product: vi.fn(async () => milk()), editDetails: vi.fn() };
    render(<NameScreen api={api as unknown as ApiClient} productId="p-milk" mode="edit" />);
    await screen.findByRole("heading", { name: "Edit product" });

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: " " } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save" })));

    expect(screen.getByText("Enter a name.")).toBeInTheDocument();
    expect(api.editDetails).not.toHaveBeenCalled();
});

test("the name in the stock list opens item detail", async () => {
    const api = {
        stock: async () => ({
            generatedAt: "",
            totalUnits: 1,
            useFirstCount: 0,
            products: [
                {
                    productId: "p milk",
                    barcode: "4056489899112",
                    displayName: "Semi-skimmed milk",
                    brand: null,
                    size: null,
                    count: 1,
                    latestCheckedInAt: "2026-10-17T18:00:00.000Z",
                    status: "known" as const,
                    section: "later" as const,
                    soonestEstimatedExpiryDate: null,
                    daysUntilSoonest: null,
                    shelfLifeDays: null,
                },
            ],
        }),
    } as unknown as ApiClient;
    window.history.pushState(null, "", "/");
    render(<StockScreen api={api} />);

    fireEvent.click(await screen.findByRole("link", { name: "Semi-skimmed milk" }));

    expect(window.location.pathname).toBe("/products/p%20milk");
});

// ---- Opened, frozen, defrosted (#65)

const pesto = (units: ProductUnit[]): Product =>
    milk({
        productId: "p-milk",
        displayName: "Pesto",
        brand: "Sacla",
        size: "190 g",
        usualStorage: "cupboard",
        shelfLifeDays: 180,
        opened: { days: 4, storage: "fridge", source: "jev" },
        units,
    });

const openedJar = unit("u1", {
    estimatedExpiryDate: "2026-10-22",
    daysUntilExpiry: 4,
    expirySource: "opened",
    openedOn: "2026-10-18",
    currentStorage: "fridge",
    canUndoStateChange: true,
});

test("More opens a sheet per unit: Opened and Froze it, and Defrosted only for a frozen unit", async () => {
    const frozen = unit("u2", { frozenOn: "2026-10-16", currentStorage: "freezer", expirySource: "frozen" });
    setup([pesto([unit("u1"), frozen])]);

    fireEvent.click(await screen.findByRole("button", { name: "More actions for Unit 1" }));
    const first = screen.getByRole("group", { name: "Unit 1: opened, frozen or defrosted" });
    expect(
        within(first)
            .getAllByRole("button")
            .map((b) => b.textContent),
    ).toEqual(["Opened", "Froze it", "Different day?"]);
    expect(screen.getByRole("button", { name: "More actions for Unit 1" })).toHaveAttribute(
        "aria-expanded",
        "true",
    );

    fireEvent.click(screen.getByRole("button", { name: "More actions for Unit 2" }));
    const second = screen.getByRole("group", { name: "Unit 2: opened, frozen or defrosted" });
    expect(
        within(second)
            .getAllByRole("button")
            .map((b) => b.textContent),
    ).toEqual(["Opened", "Defrosted", "Different day?"]);
    expect(screen.queryByRole("group", { name: /Unit 1/ })).not.toBeInTheDocument(); // one sheet at a time
});

test("Opened today: the unit updates with its pill, the date, the storage hint, and Undo puts it back", async () => {
    const markUnit = vi.fn(async () => ({ productId: "p-milk", unit: openedJar }));
    const undoStateChange = vi.fn(async () => ({ productId: "p-milk", unit: unit("u1") }));
    setup([pesto([unit("u1")]), pesto([openedJar]), pesto([unit("u1")])], { markUnit, undoStateChange });

    fireEvent.click(await screen.findByRole("button", { name: "More actions for Unit 1" }));
    fireEvent.click(screen.getByRole("button", { name: "Opened" }));

    expect(await screen.findByText("Marked as opened")).toBeInTheDocument();
    expect(markUnit).toHaveBeenCalledWith("u1", "opened", undefined);
    const row = within(screen.getByRole("region", { name: "In the cupboard" })).getAllByRole("listitem")[0];
    expect(row).toHaveTextContent("Unit 1Opened");
    expect(row).toHaveTextContent("Bought 13 Oct · opened 18 Oct · est. 22 Oct");
    expect(row).toHaveTextContent("Keep in the fridge");
    expect(screen.getByText("Estimated use by").parentElement).toHaveTextContent(
        "Opened 18 Oct; Pesto keeps about 4 days once opened. Keep in the fridge.",
    );
    expect(screen.queryByRole("group", { name: /Unit 1:/ })).not.toBeInTheDocument();
    expect(screen.getByLabelText("Use by date for unit 1")).toHaveValue("2026-10-22"); // the fix form follows the new date

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Undo" })));

    expect(undoStateChange).toHaveBeenCalledWith("u1");
    expect(row).not.toHaveTextContent("Opened");
});

test("Different day? sends the date picked, and never a day in the future", async () => {
    const markUnit = vi.fn(async () => ({ productId: "p-milk", unit: unit("u1") }));
    setup([pesto([unit("u1")])], { markUnit });

    fireEvent.click(await screen.findByRole("button", { name: "More actions for Unit 1" }));
    fireEvent.click(screen.getByRole("button", { name: "Different day?" }));
    const date = screen.getByLabelText("On");
    expect(date).toHaveAttribute("max", "2026-10-18");
    fireEvent.change(date, { target: { value: "2026-10-15" } });
    fireEvent.click(screen.getByRole("button", { name: "Froze it" }));

    await vi.waitFor(() => expect(markUnit).toHaveBeenCalledWith("u1", "frozen", "2026-10-15"));
});

test("frozen and defrosted units read as such, with pills, and a pending estimate says so", async () => {
    const frozen = unit("u1", {
        frozenOn: "2026-10-16",
        currentStorage: "freezer",
        expirySource: "frozen",
        estimatedExpiryDate: "2027-01-14",
        daysUntilExpiry: 88,
    });
    const pending = unit("u2", { openedOn: "2026-10-18", expirySource: "opened", estimatePending: true });
    setup([pesto([pending, frozen])]);

    const rows = within(await screen.findByRole("region", { name: "In the cupboard" })).getAllByRole(
        "listitem",
    );
    expect(rows[0]).toHaveTextContent(
        "Bought 13 Oct · opened 18 Oct · new estimate on its way · est. 20 Oct",
    );
    expect(rows[1]).toHaveTextContent("Unit 2Frozen");
    expect(rows[1]).toHaveTextContent("Bought 13 Oct · frozen 16 Oct · est. 14 Jan 2027");
    expect(rows[1]).toHaveTextContent("Keep in the freezer");
});

test("a refused change (e.g. already checked out) says so and reloads", async () => {
    const markUnit = vi.fn().mockRejectedValue(new ApiError(409, "conflict"));
    const api = setup([pesto([unit("u1")])], { markUnit });

    fireEvent.click(await screen.findByRole("button", { name: "More actions for Unit 1" }));
    fireEvent.click(screen.getByRole("button", { name: "Froze it" }));

    expect(await screen.findByText("That one's changed. Have a look and try again.")).toBeInTheDocument();
    expect(api.product).toHaveBeenCalledTimes(2);
});

test("Change 'once opened' shelf life saves the chosen days", async () => {
    const setOpenedShelfLife = vi.fn(async (productId: string, days: number) => ({
        productId,
        shelfLifeDays: days,
    }));
    setup([pesto([unit("u1")])], { setOpenedShelfLife });

    fireEvent.click(await screen.findByRole("button", { name: "Change “once opened” shelf life" }));
    const select = screen.getByLabelText("Once opened, Pesto keeps");
    expect(select).toHaveValue("4");
    fireEvent.change(select, { target: { value: "14" } });
    fireEvent.click(screen.getByRole("button", { name: "Save “once opened”" }));

    expect(await screen.findByText("Saved. It applies to ones you open from now on.")).toBeInTheDocument();
    expect(setOpenedShelfLife).toHaveBeenCalledWith("p-milk", 14);
});
