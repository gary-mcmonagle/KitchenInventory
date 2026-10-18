import { HubConnectionState } from "@microsoft/signalr";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import type { ApiClient } from "./api";
import type { FeedEvent } from "./scanFeed";
import { ScanFeedView } from "./ScanFeedView";
import { STOCK_REFETCH_DEBOUNCE_MS, useScanFeed, type FeedConnection } from "./useScanFeed";

class FakeConnection implements FeedConnection {
    handlers = new Map<string, (payload: unknown) => void>();
    reconnected: (() => void) | null = null;
    state: string = HubConnectionState.Disconnected;
    starts = 0;
    on(event: string, handler: (payload: unknown) => void) {
        this.handlers.set(event, handler);
    }
    onreconnected(handler: () => void) {
        this.reconnected = handler;
    }
    async start() {
        this.starts++;
        this.state = HubConnectionState.Connected;
    }
    async stop() {
        this.state = HubConnectionState.Disconnected;
    }
    emit(event: FeedEvent) {
        const { type, ...payload } = event;
        this.handlers.get(type)!(payload);
    }
}

const NOW = Date.parse("2026-10-18T09:00:00.000Z");
const at = (offsetMs = 0) => new Date(NOW + offsetMs).toISOString();

function Harness({
    api,
    connection,
    onStock,
}: {
    api: ApiClient;
    connection: FakeConnection;
    onStock: () => void;
}) {
    const { feed, now, dismiss } = useScanFeed(api, onStock, () => connection);
    return <ScanFeedView feed={feed} now={now} onDismiss={(t) => void dismiss(t)} />;
}

function setup(catchUp: FeedEvent[][] = [[]]) {
    const queue = [...catchUp];
    const api = {
        scanFeed: vi.fn(async () => ({
            generatedAt: at(),
            events: queue.length > 1 ? queue.shift()! : queue[0],
        })),
        dismissFeed: vi.fn(async (productId: string) => ({ productId, feedDismissedAt: at() })),
        dismissStoreLabel: vi.fn(async (scanId: string) => ({ scanId, dismissed: true })),
    };
    const connection = new FakeConnection();
    const onStock = vi.fn();
    render(<Harness api={api as unknown as ApiClient} connection={connection} onStock={onStock} />);
    return { api, connection, onStock };
}

const flush = () => act(async () => {});

beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date", "setTimeout", "clearTimeout", "setInterval", "clearInterval"] });
    vi.setSystemTime(NOW);
});
afterEach(() => vi.useRealTimers());

const gravy = (type: "received" | "identified", scanId = "s1"): FeedEvent =>
    type === "received"
        ? {
              type: "scanReceived",
              scanId,
              deviceId: "scanner-in",
              role: "in",
              occurredAt: at(),
              barcode: "5000354922831",
              scannedAt: at(),
          }
        : {
              type: "scanIdentified",
              scanId,
              deviceId: "scanner-in",
              role: "in",
              occurredAt: at(2000),
              outcome: "checkedIn",
              productId: "p-gravy",
              displayName: "Beef Gravy",
              brand: null,
              size: null,
              unitId: scanId,
          };
const mystery = (scanId = "m1", productId = "p-x"): FeedEvent => ({
    type: "scanNeedsIdentification",
    scanId,
    deviceId: "scanner-in",
    role: "in",
    occurredAt: at(),
    productId,
    barcode: "501001234567",
    displayName: "Unknown item (501001234567)",
    productStatus: "noMatch",
    unitId: scanId,
});

test("connects, catches up, and shows grey then green with the mockup's wording; the stock refetch is debounced", async () => {
    const { api, connection, onStock } = setup();
    await flush();
    expect(connection.starts).toBe(1);
    expect(api.scanFeed).toHaveBeenCalledWith(undefined); // on load: the API's default window

    act(() => connection.emit(gravy("received")));
    const feed = screen.getByRole("status", { name: "Scan feed" });
    expect(feed).toHaveTextContent("Reading 5000 3549 2283 1Check-in scanner · just now");

    act(() => connection.emit(gravy("identified")));
    act(() => connection.emit(gravy("identified"))); // a replay
    expect(feed).toHaveTextContent("Added Beef Gravy");
    expect(onStock).not.toHaveBeenCalled();
    act(() => vi.advanceTimersByTime(STOCK_REFETCH_DEBOUNCE_MS));
    expect(onStock).toHaveBeenCalledTimes(1);

    act(() => vi.advanceTimersByTime(10_000));
    expect(feed).toBeEmptyDOMElement();
});

test("amber: Identify opens Name it; dismiss calls the API and the toast goes", async () => {
    const { api, connection } = setup();
    await flush();
    act(() => connection.emit(mystery()));

    expect(screen.getByRole("button", { name: "Identify" })).toBeInTheDocument();
    const dismiss = screen.getByRole("button", { name: "Dismiss No match for 5010 0123 4567" });
    await act(async () => fireEvent.click(dismiss));

    expect(api.dismissFeed).toHaveBeenCalledWith("p-x");
    expect(screen.getByRole("status", { name: "Scan feed" })).toBeEmptyDOMElement();

    act(() => connection.emit(mystery("m2")));
    fireEvent.click(screen.getByRole("button", { name: "Identify" }));
    expect(window.location.pathname).toBe("/products/p-x/name");
});

test("named or dismissed on another device: the amber toast goes here too", async () => {
    const { connection } = setup();
    await flush();
    act(() => connection.emit(mystery("m1", "p-1")));
    act(() => connection.emit(mystery("m2", "p-2")));

    act(() => connection.emit({ type: "feedDismissed", productId: "p-1", occurredAt: at() }));
    act(() =>
        connection.emit({ type: "productNamed", productId: "p-2", displayName: "Mustard", occurredAt: at() }),
    );

    expect(screen.getByRole("status", { name: "Scan feed" })).toBeEmptyDOMElement();
});

test("not in stock has dismiss only, and dismissing it is local", async () => {
    const { api, connection } = setup();
    await flush();
    act(() =>
        connection.emit({
            type: "checkOutUnmatched",
            scanId: "u1",
            deviceId: "scanner-out",
            role: "out",
            occurredAt: at(),
            barcode: "20055363",
            productId: null,
            displayName: null,
        }),
    );
    expect(screen.queryByRole("button", { name: "Identify" })).not.toBeInTheDocument();

    await act(async () =>
        fireEvent.click(screen.getByRole("button", { name: "Dismiss Not in stock: 2005 5363" })),
    );

    expect(api.dismissFeed).not.toHaveBeenCalled();
    expect(screen.getByRole("status", { name: "Scan feed" })).toBeEmptyDOMElement();
});

test("a reload brings amber toasts back from the API", async () => {
    setup([[mystery()]]);
    await flush();
    expect(screen.getByText("No match for 5010 0123 4567")).toBeInTheDocument();
});

test("after a reconnect it catches up from the last event seen, merging by scan ID", async () => {
    const { api, connection } = setup([[], [gravy("identified")]]);
    await flush();
    act(() => connection.emit(gravy("received")));
    expect(screen.getByText("Reading 5000 3549 2283 1")).toBeInTheDocument();

    await act(async () => connection.reconnected!());

    expect(api.scanFeed).toHaveBeenLastCalledWith(at());
    expect(screen.getByText("Added Beef Gravy")).toBeInTheDocument();
    expect(screen.queryByText("Reading 5000 3549 2283 1")).not.toBeInTheDocument();
});

test("coming back to the app reconnects if the phone dropped the connection", async () => {
    const { api, connection } = setup();
    await flush();
    connection.state = HubConnectionState.Disconnected;

    Object.defineProperty(document, "visibilityState", { value: "visible", configurable: true });
    await act(async () => document.dispatchEvent(new Event("visibilitychange")));

    expect(connection.starts).toBe(2);
    expect(api.scanFeed).toHaveBeenCalledTimes(2);
});

test("a grey toast stuck for 60 s goes and the stock is refetched", async () => {
    const { connection, onStock } = setup();
    await flush();
    act(() => connection.emit(gravy("received")));

    act(() => vi.advanceTimersByTime(60_000));
    act(() => vi.advanceTimersByTime(STOCK_REFETCH_DEBOUNCE_MS));

    expect(screen.getByRole("status", { name: "Scan feed" })).toBeEmptyDOMElement();
    expect(onStock).toHaveBeenCalled();
});

test("at most 3 shown, then +n more waiting", async () => {
    const { connection } = setup();
    await flush();
    for (let i = 0; i < 5; i++) act(() => connection.emit(mystery(`m${i}`, `p${i}`)));
    const feed = screen.getByRole("status", { name: "Scan feed" });
    expect(within(feed).getAllByRole("button", { name: "Identify" })).toHaveLength(3);
    expect(feed).toHaveTextContent("+2 more waiting.");
});

const storeLabel = (scanId = "l1"): FeedEvent => ({
    type: "storeLabelCheckIn",
    scanId,
    deviceId: "scanner-in",
    role: "in",
    occurredAt: at(),
    barcode: "2012345678903",
});

test("a store label asks what it is: Pick opens 'What is this?', and dismissing it calls the API (#58)", async () => {
    const { api, connection } = setup();
    await flush();
    act(() => connection.emit(storeLabel("l1")));
    act(() => connection.emit(storeLabel("l2")));

    const feed = screen.getByRole("status", { name: "Scan feed" });
    expect(feed).toHaveTextContent("Store label: what is it?Check-in scanner · just now");
    await act(async () =>
        fireEvent.click(screen.getAllByRole("button", { name: "Dismiss Store label: what is it?" })[0]),
    );
    expect(api.dismissStoreLabel).toHaveBeenCalledTimes(1);
    expect(within(feed).getAllByRole("button", { name: "Pick" })).toHaveLength(1); // the other one stays

    fireEvent.click(screen.getByRole("button", { name: "Pick" }));
    expect(window.location.pathname).toMatch(/^\/store-labels\/l[12]$/);
});

test("a store label assigned or dismissed elsewhere: its toast turns green or goes; the stock refetches", async () => {
    const { connection, onStock } = setup();
    await flush();
    act(() => connection.emit(storeLabel("l1")));
    act(() => connection.emit(storeLabel("l2")));

    act(() =>
        connection.emit({
            type: "storeLabelResolved",
            scanId: "l1",
            productId: "ham",
            displayName: "Ham hock terrine",
            occurredAt: at(1000),
        }),
    );
    act(() => connection.emit({ type: "feedDismissed", productId: null, scanId: "l2", occurredAt: at() }));

    const feed = screen.getByRole("status", { name: "Scan feed" });
    expect(feed).toHaveTextContent("Added Ham hock terrine");
    expect(feed).not.toHaveTextContent("Store label");
    act(() => vi.advanceTimersByTime(STOCK_REFETCH_DEBOUNCE_MS));
    expect(onStock).toHaveBeenCalled();
});

test("a store-label check-out says to check it out from the app", async () => {
    const { connection } = setup();
    await flush();
    act(() =>
        connection.emit({
            type: "checkOutUnmatched",
            scanId: "o1",
            deviceId: "scanner-out",
            role: "out",
            occurredAt: at(),
            barcode: "2012345678903",
            productId: null,
            displayName: null,
            storeLabel: true,
        }),
    );

    expect(screen.getByRole("status", { name: "Scan feed" })).toHaveTextContent(
        "Store label: check it out from the app",
    );
});
