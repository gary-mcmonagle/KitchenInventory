import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { ApiError, type ApiClient, type Product } from "./api";
import { NameScreen, validate } from "./NameScreen";
import { StockScreen } from "./StockScreen";

const unknown: Product = {
    productId: "p-1",
    barcode: "501001234567",
    status: "noMatch",
    displayName: "Unknown item (501001234567)",
    brand: null,
    size: null,
    inStockCount: 1,
    latestCheckedInAt: new Date(Date.now() - 5 * 60000).toISOString(),
    latestDeviceRole: "in",
    shelfLifeDays: null,
    shelfLifeSource: null,
    units: [],
    usualStorage: null,
    opened: null,
    frozen: null,
    defrosted: null,
    openedCount: 0,
    frozenCount: 0,
    aisleId: null,
    aisleSource: null,
    icon: null,
    iconSource: null,
    genericName: null,
};

function deferred<T>() {
    let resolve!: (v: T) => void;
    const promise = new Promise<T>((res) => (resolve = res));
    return { promise, resolve };
}

function setup(overrides: Record<string, unknown> = {}) {
    const api = {
        product: vi.fn(async () => unknown),
        nameProduct: vi.fn(async () => ({ productId: "p-1", displayName: "Smoked paprika" })),
        ...overrides,
    };
    window.history.pushState(null, "", "/products/p-1/name");
    render(<NameScreen api={api as unknown as ApiClient} productId="p-1" />);
    return api;
}

const type = (label: string, value: string) =>
    fireEvent.change(screen.getByLabelText(label), { target: { value } });

test("shows the mockup's manual parts: back, title, context, three labelled fields, Save and Not now", async () => {
    setup();

    expect(await screen.findByText("Scanned 5 minutes ago on the check-in scanner.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Back to stock" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { level: 1, name: "Name this product" })).toBeInTheDocument();
    expect(screen.getByText("Barcode 5010 0123 4567")).toBeInTheDocument();
    expect(screen.getByLabelText("Name")).toHaveFocus();
    expect(screen.getByLabelText("Brand")).toBeInTheDocument();
    expect(screen.getByLabelText("Size")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save product" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Not now" })).toBeInTheDocument();
    expect(screen.queryByText(/Retake photo|How long it usually keeps|Looks right/)).not.toBeInTheDocument();
});

test("Name is required: nothing is sent", async () => {
    const api = setup();
    await screen.findByLabelText("Name");

    fireEvent.click(screen.getByRole("button", { name: "Save product" }));

    expect(screen.getByText("Enter a name.")).toBeInTheDocument();
    expect(screen.getByLabelText("Name")).toHaveAttribute("aria-invalid", "true");
    expect(api.nameProduct).not.toHaveBeenCalled();
});

test("saving sends the trimmed details, disables Save meanwhile, then returns to the list", async () => {
    const pending = deferred<{ productId: string; displayName: string }>();
    const api = setup({ nameProduct: vi.fn(() => pending.promise) });
    await screen.findByLabelText("Name");
    type("Name", "  Smoked paprika ");
    type("Brand", "Schwartz");

    fireEvent.click(screen.getByRole("button", { name: "Save product" }));

    expect(api.nameProduct).toHaveBeenCalledWith("p-1", {
        name: "Smoked paprika",
        brand: "Schwartz",
        size: undefined,
    });
    expect(screen.getByRole("button", { name: "Saving…" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Saving…" }));
    expect(api.nameProduct).toHaveBeenCalledTimes(1);

    await act(async () => pending.resolve({ productId: "p-1", displayName: "Smoked paprika" }));
    expect(window.location.pathname).toBe("/");
});

test("Not now and Back save nothing and return to the list", async () => {
    const api = setup();
    await screen.findByLabelText("Name");
    type("Name", "Something");

    fireEvent.click(screen.getByRole("button", { name: "Not now" }));
    expect(window.location.pathname).toBe("/");

    window.history.pushState(null, "", "/products/p-1/name");
    fireEvent.click(screen.getByRole("button", { name: "Back to stock" }));
    expect(window.location.pathname).toBe("/");
    expect(api.nameProduct).not.toHaveBeenCalled();
});

test("field errors from the API show inline", async () => {
    setup({
        nameProduct: vi.fn(async () =>
            Promise.reject(
                new ApiError(400, "bad", { errors: { size: "Keep the size to 60 characters or fewer." } }),
            ),
        ),
    });
    await screen.findByLabelText("Name");
    type("Name", "Paprika");

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save product" })));

    expect(screen.getByText("Keep the size to 60 characters or fewer.")).toBeInTheDocument();
    expect(window.location.pathname).toBe("/products/p-1/name");
    expect(screen.getByRole("button", { name: "Save product" })).toBeEnabled();
});

test("already named (409) says so", async () => {
    setup({ nameProduct: vi.fn(async () => Promise.reject(new ApiError(409, "named"))) });
    await screen.findByLabelText("Name");
    type("Name", "Paprika");

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save product" })));

    expect(screen.getByRole("alert")).toHaveTextContent("This product already has a name.");
});

test("a product that already has a name can't be named here", async () => {
    setup({ product: vi.fn(async () => ({ ...unknown, status: "known", displayName: "Beef Gravy" })) });

    expect(await screen.findByRole("alert")).toHaveTextContent(
        "This product already has a name: Beef Gravy.",
    );
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save product" })).not.toBeInTheDocument();
});

test("an unknown product", async () => {
    setup({ product: vi.fn(async () => Promise.reject(new ApiError(404, "nope"))) });

    expect(await screen.findByRole("alert")).toHaveTextContent("We couldn't find that product.");
});

test("validation rules", () => {
    expect(validate("", "", "")).toEqual({ name: "Enter a name." });
    expect(validate("a".repeat(101), "", "")).toHaveProperty("name");
    expect(validate("ok", "b".repeat(61), "s".repeat(61))).toEqual({
        brand: "Keep the brand to 60 characters or fewer.",
        size: "Keep the size to 60 characters or fewer.",
    });
    expect(validate("a".repeat(100), "b".repeat(60), "")).toEqual({});
});

test("Name it in the stock list opens the naming screen for that product", async () => {
    const api = {
        stock: async () => ({
            generatedAt: "",
            totalUnits: 1,
            useFirstCount: 0,
            products: [
                {
                    productId: "p 1",
                    barcode: "501001234567",
                    displayName: "Unknown item (501001234567)",
                    brand: null,
                    size: null,
                    count: 1,
                    latestCheckedInAt: "2026-10-17T18:00:00.000Z",
                    status: "noMatch",
                    section: "waitingForName",
                    soonestEstimatedExpiryDate: null,
                    daysUntilSoonest: null,
                    shelfLifeDays: null,
                },
            ],
        }),
    } as unknown as ApiClient;
    window.history.pushState(null, "", "/");
    render(<StockScreen api={api} />);

    const unnamed = await screen.findByRole("region", { name: /Waiting for a name/ });
    fireEvent.click(within(unnamed).getByRole("button", { name: "Name it" }));

    expect(window.location.pathname).toBe("/products/p%201/name");
});

afterEach(() => window.history.pushState(null, "", "/"));

test("Edit details: the aisle is saved with the form, and an icon is picked from a searchable grid (#71, #72)", async () => {
    const known: Product = {
        ...unknown,
        status: "known",
        displayName: "Sourdough",
        aisleId: "other",
        aisleSource: "jev",
        icon: "cake",
        iconSource: "jev",
    };
    const api = {
        product: vi.fn(async () => known),
        editDetails: vi.fn(async () => ({ productId: "p-1", displayName: "Sourdough" })),
        setAisle: vi.fn(async () => ({ productId: "p-1", aisle: "bakery" })),
        setIcon: vi.fn(async (_id: string, icon: string | null) => ({ productId: "p-1", icon })),
    };
    window.history.pushState(null, "", "/products/p-1/edit");
    render(<NameScreen api={api as unknown as ApiClient} productId="p-1" mode="edit" />);

    fireEvent.change(await screen.findByLabelText("Aisle"), { target: { value: "bakery" } });
    fireEvent.click(screen.getByRole("button", { name: "Change" }));
    fireEvent.change(screen.getByLabelText("Search icons"), { target: { value: "bre" } });
    const grid = screen.getByRole("group", { name: "Icons" });
    expect(
        within(grid)
            .getAllByRole("button")
            .map((b) => b.getAttribute("aria-label")),
    ).toEqual(["bread", "bread machine", "bread one"]);
    await act(async () => fireEvent.click(within(grid).getByRole("button", { name: "bread" })));
    expect(api.setIcon).toHaveBeenCalledWith("p-1", "bread");
    expect(screen.getByText("Chosen by you")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Change" }));
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Let Jev choose" })));
    expect(api.setIcon).toHaveBeenLastCalledWith("p-1", null);

    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save" })));
    expect(api.setAisle).toHaveBeenCalledWith("p-1", "bakery");
});

test("Edit details: meat, fish or neither is shown, and saved only when changed (#112)", async () => {
    const known: Product = {
        ...unknown,
        status: "known",
        displayName: "Pesto",
        aisleId: "tinsJars",
        aisleSource: "jev",
        animalContent: "unknown",
        animalContentSource: "jev",
    };
    const api = {
        product: vi.fn(async () => known),
        editDetails: vi.fn(async () => ({ productId: "p-1", displayName: "Pesto" })),
        setAisle: vi.fn(),
        setAnimalContent: vi.fn(async () => ({ productId: "p-1", animalContent: "fish" })),
    };
    window.history.pushState(null, "", "/products/p-1/edit");
    render(<NameScreen api={api as unknown as ApiClient} productId="p-1" mode="edit" />);

    const select = await screen.findByLabelText("Meat / fish / neither");
    expect(select).toHaveValue("");
    expect(
        within(select)
            .getAllByRole("option")
            .map((o) => o.textContent),
    ).toEqual(["Not sure yet", "Meat", "Fish", "Neither"]);

    fireEvent.change(select, { target: { value: "fish" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save" })));

    expect(api.setAnimalContent).toHaveBeenCalledWith("p-1", "fish");
});

test("Edit details: an unchanged meat, fish or neither isn't sent again (#112)", async () => {
    const known: Product = {
        ...unknown,
        status: "known",
        displayName: "Ham",
        aisleId: "meatFish",
        animalContent: "meat",
    };
    const api = {
        product: vi.fn(async () => known),
        editDetails: vi.fn(async () => ({ productId: "p-1", displayName: "Ham" })),
        setAisle: vi.fn(),
        setAnimalContent: vi.fn(),
    };
    window.history.pushState(null, "", "/products/p-1/edit");
    render(<NameScreen api={api as unknown as ApiClient} productId="p-1" mode="edit" />);

    expect(await screen.findByLabelText("Meat / fish / neither")).toHaveValue("meat");
    await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save" })));

    expect(api.setAnimalContent).not.toHaveBeenCalled();
});

describe("Edit details: generic name (#90)", () => {
    const known: Product = {
        ...unknown,
        status: "known",
        displayName: "SEMI-SKIMMED BRITISH MILK",
        aisleId: "dairyEggs",
        aisleSource: "jev",
        genericName: { id: "milk", name: "Milk", source: "foundry" },
    };
    const names = [
        {
            id: "mature cheddar",
            name: "Mature cheddar",
            aisleId: "dairyEggs",
            aisleName: "Dairy & eggs",
            productCount: 2,
        },
        {
            id: "semi-skimmed milk",
            name: "Semi-skimmed milk",
            aisleId: "dairyEggs",
            aisleName: "Dairy & eggs",
            productCount: 3,
        },
    ];
    const setup = () => {
        const api = {
            product: vi.fn(async () => known),
            editDetails: vi.fn(async () => ({ productId: "p-1", displayName: known.displayName })),
            setAisle: vi.fn(),
            genericNames: vi.fn(async () => ({ genericNames: names })),
            setGenericName: vi.fn(async () => ({ productId: "p-1", genericName: names[1], created: false })),
        };
        window.history.pushState(null, "", "/products/p-1/edit");
        render(<NameScreen api={api as unknown as ApiClient} productId="p-1" mode="edit" />);
        return api;
    };

    test("shows the current name, searches existing names with their aisles, and saves the pick as manual", async () => {
        const api = setup();
        expect(await screen.findByText("Shopping list name: Milk")).toBeInTheDocument();

        const search = screen.getByRole("combobox", { name: "Generic name" });
        await act(async () => fireEvent.focus(search));
        fireEvent.change(search, { target: { value: "skim" } });
        const list = screen.getByRole("listbox", { name: "Generic names" });
        expect(
            within(list)
                .getAllByRole("option")
                .map((o) => o.textContent),
        ).toEqual(["Semi-skimmed milkDairy & eggs · 3 products", "Use a new name: skim"]);

        fireEvent.click(within(list).getByRole("button", { name: /Semi-skimmed milk/ }));
        expect(screen.getByText("Shopping list name: Semi-skimmed milk (changed)")).toBeInTheDocument();
        await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save" })));

        expect(api.genericNames).toHaveBeenCalledTimes(1);
        expect(api.setGenericName).toHaveBeenCalledWith("p-1", { genericNameId: "semi-skimmed milk" });
    });

    test("a new name can be typed in, and an exact existing name isn't offered as new", async () => {
        const api = setup();
        const search = await screen.findByRole("combobox", { name: "Generic name" });
        await act(async () => fireEvent.focus(search));

        fireEvent.change(search, { target: { value: "mature cheddar" } });
        expect(screen.queryByText(/Use a new name/)).not.toBeInTheDocument();

        fireEvent.change(search, { target: { value: "  Whole   milk " } });
        fireEvent.click(screen.getByRole("button", { name: "Use a new name: Whole milk" }));
        await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save" })));

        expect(api.setGenericName).toHaveBeenCalledWith("p-1", { name: "Whole milk" });
    });

    test("saving without touching it doesn't change the generic name", async () => {
        const api = setup();
        await screen.findByText("Shopping list name: Milk");
        await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save" })));
        expect(api.setGenericName).not.toHaveBeenCalled();
        expect(api.genericNames).not.toHaveBeenCalled(); // loaded only when the field is used
    });
});

describe("Identify from a photo (Gary, 2026-10-18)", () => {
    const photoSetup = (recognisePhoto: ReturnType<typeof vi.fn>) => {
        const api = {
            product: vi.fn(async () => unknown),
            nameProduct: vi.fn(async () => ({ productId: "p-1", displayName: "x" })),
            recognisePhoto,
        };
        window.history.pushState(null, "", "/products/p-1/name");
        render(
            <NameScreen
                api={api as unknown as ApiClient}
                productId="p-1"
                prepare={async (f) => new Blob([f], { type: "image/jpeg" })}
            />,
        );
        return api;
    };
    const shoot = () =>
        fireEvent.change(screen.getByLabelText("Photo of the item"), {
            target: { files: [new File(["jpeg"], "photo.jpg", { type: "image/jpeg" })] },
        });
    const item = (name: string, brand: string | null = null, size: string | null = null) => ({
        name,
        brand,
        size,
        kind: "packaged" as const,
        quantity: 1,
        match: null,
    });

    test("one item fills in the form to check and save", async () => {
        const api = photoSetup(
            vi.fn(async () => ({
                available: true,
                readable: true,
                items: [item("Smoked paprika", "Schwartz", "48 g")],
            })),
        );
        await screen.findByLabelText("Name");

        shoot();

        expect(
            await screen.findByText("Filled in from your photo. Check it, then save."),
        ).toBeInTheDocument();
        expect(screen.getByLabelText("Name")).toHaveValue("Smoked paprika");
        expect(screen.getByLabelText("Brand")).toHaveValue("Schwartz");
        expect(screen.getByLabelText("Size")).toHaveValue("48 g");
        expect(screen.getByRole("button", { name: "Retake photo" })).toBeInTheDocument();
        await act(async () => fireEvent.click(screen.getByRole("button", { name: "Save product" })));
        expect(api.nameProduct).toHaveBeenCalledWith("p-1", {
            name: "Smoked paprika",
            brand: "Schwartz",
            size: "48 g",
        });
    });

    test("several items: pick which one it is", async () => {
        photoSetup(
            vi.fn(async () => ({
                available: true,
                readable: true,
                items: [item("Leeks"), item("Pitted green olives", "Crespo", "340 g")],
            })),
        );
        await screen.findByLabelText("Name");

        shoot();
        const choices = await screen.findByRole("group", { name: "Which one is it?" });
        fireEvent.click(within(choices).getByRole("button", { name: /Pitted green olives/ }));

        expect(screen.getByLabelText("Name")).toHaveValue("Pitted green olives");
        expect(screen.getByLabelText("Brand")).toHaveValue("Crespo");
    });

    test("nothing recognised, or recognition unavailable, falls back to typing", async () => {
        const recognise = vi
            .fn()
            .mockResolvedValueOnce({ available: true, readable: false, items: [] })
            .mockResolvedValueOnce({ available: false, readable: false, items: [] });
        photoSetup(recognise);
        await screen.findByLabelText("Name");

        shoot();
        expect(
            await screen.findByText("We couldn't work that one out. Type it in or retake the photo."),
        ).toBeInTheDocument();
        shoot();
        await vi.waitFor(() => expect(recognise).toHaveBeenCalledTimes(2));
        expect(screen.getByLabelText("Name")).toHaveValue("");
    });
});
