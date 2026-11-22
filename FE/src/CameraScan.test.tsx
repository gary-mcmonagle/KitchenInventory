import { act, fireEvent, render, screen, within } from "@testing-library/react";
import type { ApiClient, CameraScanResult } from "./api";
import { ApiError } from "./api";
import { CameraUnavailableError, expandUpcE, type ReaderHandle, type StartReader } from "./barcodeReader";
import { CameraScanScreen, SAME_BARCODE_MS } from "./CameraScanScreen";
import { cameraScanEnabled } from "./features";
import { Shell } from "./Shell";
import { StockScreen } from "./StockScreen";

const GRAVY = "5000354922831";
const BEANS = "5000169005743";

function setup(options: { torch?: boolean; startError?: Error } = {}) {
    let read: (code: string) => void = () => undefined;
    const handle: ReaderHandle = {
        stop: vi.fn(),
        torchSupported: options.torch ?? false,
        setTorch: vi.fn(async () => undefined),
    };
    const start: StartReader = vi.fn(async (_video, onRead) => {
        if (options.startError) throw options.startError;
        read = onRead;
        return handle;
    });
    const cameraScan = vi.fn(
        async (body: {
            requestId: string;
            barcode: string;
            role: "in" | "out";
        }): Promise<CameraScanResult> =>
            body.role === "in"
                ? {
                      outcome: "checkedIn",
                      unitId: `u-${body.barcode}`,
                      productId: "p",
                      displayName: "Beef Gravy",
                      count: 2,
                  }
                : {
                      outcome: "checkedOut",
                      unitId: `o-${body.barcode}`,
                      productId: "p",
                      displayName: "Beef Gravy",
                      remainingCount: 1,
                  },
    );
    const undoCheckIn = vi.fn(async (unitId: string) => ({ unitId, productId: "p", undone: true }));
    const undoCheckOut = vi.fn(async (unitId: string) => ({
        unitId,
        productId: "p",
        count: 2,
        restored: true,
    }));
    const api = { cameraScan, undoCheckIn, undoCheckOut } as unknown as ApiClient;
    const view = render(<CameraScanScreen api={api} start={start} />);
    const scan = async (code: string) => {
        await act(async () => {
            read(code);
        });
    };
    return { ...view, api, cameraScan, undoCheckIn, undoCheckOut, scan, handle, start };
}

const modeButton = (name: string) =>
    within(screen.getByRole("group", { name: "Scan mode" })).getByRole("button", { name });

beforeEach(() => {
    Object.defineProperty(navigator, "onLine", { configurable: true, value: true });
});
afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllEnvs();
});

test("it opens on Check in, and the mode switch changes what a read does", async () => {
    const { scan, cameraScan } = setup();
    expect(modeButton("Check in")).toHaveAttribute("aria-pressed", "true");
    expect(modeButton("Check out")).toHaveAttribute("aria-pressed", "false");

    await scan(GRAVY);
    expect(cameraScan).toHaveBeenLastCalledWith({
        requestId: expect.any(String),
        barcode: GRAVY,
        role: "in",
    });

    fireEvent.click(modeButton("Check out"));
    expect(modeButton("Check out")).toHaveAttribute("aria-pressed", "true");
    await scan(BEANS);
    expect(cameraScan).toHaveBeenLastCalledWith({
        requestId: expect.any(String),
        barcode: BEANS,
        role: "out",
    });
});

test("a check-in shows the banner and a row, and the session counts in and out", async () => {
    const { scan } = setup();
    expect(screen.getByText("Nothing scanned yet.")).toBeInTheDocument();

    await scan(GRAVY);
    expect(screen.getByText("Checked in: Beef Gravy")).toBeInTheDocument();
    expect(screen.getByText("This session: 1 in, 0 out")).toBeInTheDocument();
    expect(screen.getByText("Now 2 in stock")).toBeInTheDocument();

    fireEvent.click(modeButton("Check out"));
    await scan(BEANS);
    expect(screen.getByText("Checked out: Beef Gravy")).toBeInTheDocument();
    expect(screen.getByText("This session: 1 in, 1 out")).toBeInTheDocument();
    expect(screen.getByText("Soonest-dated unit · 1 left")).toBeInTheDocument();
});

test("the same barcode is ignored for 2 seconds, and held in view it counts once; showing it again later counts another", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    const { scan, cameraScan } = setup();

    await scan(GRAVY);
    for (let i = 0; i < 12; i++) {
        vi.advanceTimersByTime(500); // held in view for six seconds: every sighting renews the window
        await scan(GRAVY);
    }
    expect(cameraScan).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(SAME_BARCODE_MS + 100); // taken away, then shown again
    await scan(GRAVY);
    expect(cameraScan).toHaveBeenCalledTimes(2);
    expect(screen.getByText("This session: 2 in, 0 out")).toBeInTheDocument();

    await scan(BEANS); // a different barcode straight away is a new scan
    expect(cameraScan).toHaveBeenCalledTimes(3);
});

test("each row has Undo until Done: a check-in goes through undo check-in, a check-out through undo check-out", async () => {
    const { scan, undoCheckIn, undoCheckOut } = setup();
    await scan(GRAVY);
    fireEvent.click(modeButton("Check out"));
    await scan(BEANS);
    expect(screen.getByText("This session: 1 in, 1 out")).toBeInTheDocument();

    // two rows are both called Beef Gravy; the newest (the check-out) is first
    await act(async () => {
        fireEvent.click(screen.getAllByRole("button", { name: "Undo Beef Gravy" })[0]);
    });
    expect(undoCheckOut).toHaveBeenCalledWith(`o-${BEANS}`);
    expect(screen.getByText("This session: 1 in, 0 out")).toBeInTheDocument();

    await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Undo Beef Gravy" }));
    });
    expect(undoCheckIn).toHaveBeenCalledWith(`u-${GRAVY}`);
    expect(screen.getByText("This session: 0 in, 0 out")).toBeInTheDocument();
    expect(screen.getAllByText("Undone")).toHaveLength(2);
});

test("a failed undo says so and can be tried again", async () => {
    const { scan, undoCheckIn } = setup();
    await scan(GRAVY);
    undoCheckIn.mockRejectedValueOnce(new ApiError(500, "x"));
    await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Undo Beef Gravy" }));
    });
    expect(screen.getByText("Couldn't undo that. Try again.")).toBeInTheDocument();
    expect(screen.getByText("This session: 1 in, 0 out")).toBeInTheDocument();
    await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Undo Beef Gravy" }));
    });
    expect(screen.getByText("This session: 0 in, 0 out")).toBeInTheDocument();
});

test("an unknown barcode is saved to name later, with Undo", async () => {
    const { scan, cameraScan, undoCheckIn } = setup();
    cameraScan.mockResolvedValueOnce({ outcome: "needsName", unitId: "u1", productId: "p1", barcode: GRAVY });
    await scan(GRAVY);

    expect(screen.getByText("New barcode saved to name later")).toBeInTheDocument();
    expect(screen.getByText("Unknown barcode")).toBeInTheDocument();
    expect(screen.getByText("Saved to name later on Scanners")).toBeInTheDocument();
    expect(screen.getByText("Name later")).toBeInTheDocument();
    await act(async () => {
        fireEvent.click(screen.getByRole("button", { name: "Undo Unknown barcode" }));
    });
    expect(undoCheckIn).toHaveBeenCalledWith("u1");
});

test("a store label has nothing to undo", async () => {
    const { scan, cameraScan } = setup();
    cameraScan.mockResolvedValueOnce({ outcome: "needsName", unitId: null, productId: null, barcode: GRAVY });
    await scan(GRAVY);
    expect(screen.getByText("Unknown barcode")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Undo/ })).not.toBeInTheDocument();
});

test("checking out something not in stock shows the red banner and adds no row", async () => {
    const { scan, cameraScan } = setup();
    cameraScan.mockResolvedValueOnce({ outcome: "notInStock", barcode: BEANS });
    fireEvent.click(modeButton("Check out"));
    await scan(BEANS);

    expect(screen.getByText("Not in stock. Nothing changed.")).toBeInTheDocument();
    expect(screen.getByText("Nothing scanned yet.")).toBeInTheDocument();
    expect(screen.getByText("This session")).toBeInTheDocument();
});

test("a scan that couldn't be saved says so, and showing it again retries with the same request id", async () => {
    const { scan, cameraScan } = setup();
    cameraScan.mockRejectedValueOnce(new ApiError(503, "x"));
    await scan(GRAVY);
    expect(screen.getByText("Couldn't save that scan. Try again.")).toBeInTheDocument();
    expect(screen.getByText("Nothing scanned yet.")).toBeInTheDocument();

    await scan(GRAVY); // straight away: the failed one doesn't block it
    expect(cameraScan).toHaveBeenCalledTimes(2);
    expect(cameraScan.mock.calls[1][0].requestId).toBe(cameraScan.mock.calls[0][0].requestId); // so it can't double count
    expect(screen.getByText("This session: 1 in, 0 out")).toBeInTheDocument();
});

test("when the camera can't be used it says how to fix it and nothing else breaks", async () => {
    setup({ startError: new CameraUnavailableError("Permission denied") });
    expect(
        await screen.findByText(
            "We can't use the camera. Allow camera access in your browser settings, or use the scanners.",
        ),
    ).toBeInTheDocument();
    expect(modeButton("Check in")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Done" })).toBeInTheDocument();
});

test("offline: it says so and pauses reading, and reads again when back online", async () => {
    const { scan, cameraScan } = setup();
    Object.defineProperty(navigator, "onLine", { configurable: true, value: false });
    act(() => {
        window.dispatchEvent(new Event("offline"));
    });
    expect(screen.getByText("You're offline. Scans need a connection.")).toBeInTheDocument();
    await scan(GRAVY);
    expect(cameraScan).not.toHaveBeenCalled();

    Object.defineProperty(navigator, "onLine", { configurable: true, value: true });
    act(() => {
        window.dispatchEvent(new Event("online"));
    });
    expect(screen.queryByText("You're offline. Scans need a connection.")).not.toBeInTheDocument();
    await scan(GRAVY);
    expect(cameraScan).toHaveBeenCalledTimes(1);
});

test("the torch button shows only when the camera has one, and turns it on and off", async () => {
    const without = setup();
    await act(async () => {});
    expect(screen.queryByRole("button", { name: "Torch" })).not.toBeInTheDocument();
    without.unmount();

    const { handle } = setup({ torch: true });
    const torch = await screen.findByRole("button", { name: "Torch" });
    await act(async () => {
        fireEvent.click(torch);
    });
    expect(handle.setTorch).toHaveBeenLastCalledWith(true);
    expect(torch).toHaveAttribute("aria-pressed", "true");
    await act(async () => {
        fireEvent.click(torch);
    });
    expect(handle.setTorch).toHaveBeenLastCalledWith(false);
});

test("the camera stops when the screen closes", async () => {
    const { unmount, handle } = setup();
    await act(async () => {});
    unmount();
    expect(handle.stop).toHaveBeenCalled();
});

test("the toggle: unset means on, only false turns it off", () => {
    expect(cameraScanEnabled({})).toBe(true);
    expect(cameraScanEnabled({ VITE_FEATURE_CAMERA_SCAN: "" })).toBe(true);
    expect(cameraScanEnabled({ VITE_FEATURE_CAMERA_SCAN: "true" })).toBe(true);
    expect(cameraScanEnabled({ VITE_FEATURE_CAMERA_SCAN: "false" })).toBe(false);
    expect(cameraScanEnabled({ VITE_FEATURE_CAMERA_SCAN: " False " })).toBe(false);
});

const stockApi = {
    me: async () => ({ name: "Cupboard test", objectId: "o" }),
    ping: async () => ({ status: "ok", commit: "abc1234" }),
    stock: async () => ({ generatedAt: "", totalUnits: 0, products: [] }),
} as unknown as ApiClient;

test("on the stock list the Scan button opens the scanner; with the feature off there is no button and no route", async () => {
    render(<StockScreen api={stockApi} />);
    expect(await screen.findByRole("link", { name: "Scan" })).toHaveAttribute("href", "/scan");
});

test("with the feature off: no Scan button, and /scan is just the stock list", async () => {
    vi.stubEnv("VITE_FEATURE_CAMERA_SCAN", "false");
    window.history.replaceState({}, "", "/scan");
    render(<Shell api={stockApi} buildSha="abc" />);

    expect(await screen.findByText(/Nothing in stock yet/)).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Scan" })).not.toBeInTheDocument();
    expect(screen.queryByRole("group", { name: "Scan mode" })).not.toBeInTheDocument();
    window.history.replaceState({}, "", "/");
});

test("UPC-E is expanded to the 12-digit UPC-A it stands for", () => {
    expect(expandUpcE("04252614")).toBe("042100005264"); // last of the six digits 0, 1 or 2
    expect(expandUpcE("01234535")).toBe("012300000455"); // 3
    expect(expandUpcE("01234549")).toBe("012340000059"); // 4
    expect(expandUpcE("01234565")).toBe("012345000065"); // 5 to 9
    expect(expandUpcE("5000354922831")).toBe("5000354922831"); // not UPC-E: left alone
    expect(expandUpcE("21234565")).toBe("21234565"); // an 8-digit code that is not UPC-E (it starts with 2)
});
