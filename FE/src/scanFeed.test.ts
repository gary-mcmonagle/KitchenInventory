import {
    LOADING_MS,
    OK_MS,
    applyCatchUp,
    applyEvent,
    expire,
    feedTime,
    toastSubtitle,
    visibleToasts,
    type FeedEvent,
    type FeedState,
} from "./scanFeed";

const T0 = Date.parse("2026-10-18T09:00:00.000Z");
const iso = (ms: number) => new Date(ms).toISOString();
const empty: FeedState = new Map();

const received = (
    scanId: string,
    at = T0,
    barcode = "5000354922831",
    role: "in" | "out" = "in",
): FeedEvent => ({
    type: "scanReceived",
    scanId,
    deviceId: `scanner-${role}`,
    role,
    occurredAt: iso(at),
    barcode,
    scannedAt: iso(at - 500),
});
const identified = (
    scanId: string,
    at = T0 + 3000,
    outcome: "checkedIn" | "checkedOut" = "checkedIn",
): FeedEvent => ({
    type: "scanIdentified",
    scanId,
    deviceId: "scanner-in",
    role: outcome === "checkedIn" ? "in" : "out",
    occurredAt: iso(at),
    outcome,
    productId: "p-gravy",
    displayName: "Beef Gravy",
    brand: "Bisto",
    size: null,
    unitId: scanId,
});
const needsName = (scanId: string, productId = "p-x", at = T0 + 3000): FeedEvent => ({
    type: "scanNeedsIdentification",
    scanId,
    deviceId: "scanner-in",
    role: "in",
    occurredAt: iso(at),
    productId,
    barcode: "501001234567",
    displayName: "Unknown item (501001234567)",
    productStatus: "noMatch",
    unitId: scanId,
});

test("grey, then green, by scan ID; green goes after 10 s", () => {
    let s = applyEvent(empty, received("s1"), T0);
    expect(s.get("s1")).toMatchObject({ kind: "loading", title: "Reading 5000 3549 2283 1", role: "in" });

    s = applyEvent(s, identified("s1"), T0 + 3000);
    expect(s.get("s1")).toMatchObject({
        kind: "ok",
        title: "Added Beef Gravy",
        at: iso(T0),
        expiresAt: T0 + 3000 + OK_MS,
    });

    expect(expire(s, T0 + 3000 + OK_MS - 1).state.size).toBe(1);
    expect(expire(s, T0 + 3000 + OK_MS).state.size).toBe(0);
});

test("never backwards: a late scanReceived doesn't turn green grey", () => {
    let s = applyEvent(empty, identified("s1"), T0);
    s = applyEvent(s, received("s1"), T0 + 1);
    expect(s.get("s1")!.kind).toBe("ok");
});

test("check-out wording, and not in stock", () => {
    let s = applyEvent(empty, identified("s1", T0, "checkedOut"), T0);
    expect(s.get("s1")!.title).toBe("Checked out Beef Gravy");
    s = applyEvent(
        s,
        {
            type: "checkOutUnmatched",
            scanId: "s2",
            deviceId: "scanner-out",
            role: "out",
            occurredAt: iso(T0),
            barcode: "20055363",
            productId: null,
            displayName: null,
        },
        T0,
    );
    expect(s.get("s2")).toMatchObject({
        kind: "unmatched",
        title: "Not in stock: 2005 5363",
        expiresAt: T0 + OK_MS,
    });
    s = applyEvent(
        s,
        {
            type: "checkOutUnmatched",
            scanId: "s3",
            deviceId: "scanner-out",
            role: "out",
            occurredAt: iso(T0),
            barcode: "1",
            productId: "p",
            displayName: "Beef Gravy",
        },
        T0,
    );
    expect(s.get("s3")!.title).toBe("Not in stock: Beef Gravy");
});

test("amber stays until named or dismissed, here or on another device", () => {
    const s = applyEvent(empty, needsName("s1"), T0);
    expect(s.get("s1")).toMatchObject({
        kind: "warn",
        title: "No match for 5010 0123 4567",
        productId: "p-x",
        expiresAt: null,
    });
    expect(expire(s, T0 + 3_600_000).state.size).toBe(1);

    expect(
        applyEvent(
            s,
            { type: "productNamed", productId: "p-x", displayName: "Mustard", occurredAt: iso(T0) },
            T0,
        ).size,
    ).toBe(0);
    expect(applyEvent(s, { type: "feedDismissed", productId: "p-x", occurredAt: iso(T0) }, T0).size).toBe(0);
    expect(applyEvent(s, { type: "feedDismissed", productId: "other", occurredAt: iso(T0) }, T0)).toBe(s);
});

test("amber toasts for the same product collapse into the newest", () => {
    let s = applyEvent(empty, needsName("s1", "p-x", T0), T0);
    s = applyEvent(s, needsName("s2", "p-x", T0 + 60_000), T0 + 60_000);
    expect([...s.keys()]).toEqual(["s2"]);
    s = applyEvent(s, needsName("s1", "p-x", T0), T0 + 61_000); // an older one arriving late is ignored
    expect([...s.keys()]).toEqual(["s2"]);
    s = applyEvent(s, needsName("s3", "p-y", T0), T0);
    expect(s.size).toBe(2);
});

test("a skipped scan's grey toast goes", () => {
    const s = applyEvent(empty, received("s1", T0, "2012345678903"), T0);
    expect(
        applyEvent(
            s,
            {
                type: "scanSkipped",
                scanId: "s1",
                deviceId: "d",
                role: "in",
                occurredAt: iso(T0),
                barcode: "2012345678903",
                reason: "storeLabel",
            },
            T0,
        ).size,
    ).toBe(0);
});

test("a grey toast with no follow-up goes after 60 s and says so", () => {
    const s = applyEvent(empty, received("s1"), T0);
    expect(expire(s, T0 + LOADING_MS - 1)).toEqual({ state: s, loadingTimedOut: false });
    const after = expire(s, T0 + LOADING_MS);
    expect(after.state.size).toBe(0);
    expect(after.loadingTimedOut).toBe(true);
});

test("newest on top, 3 shown, the rest counted", () => {
    let s: FeedState = empty;
    for (let i = 0; i < 5; i++) s = applyEvent(s, needsName(`s${i}`, `p${i}`, T0 + i * 1000), T0);
    const { shown, more } = visibleToasts(s);
    expect(shown.map((t) => t.scanId)).toEqual(["s4", "s3", "s2"]);
    expect(more).toBe(2);
});

test("catch-up merges by scan ID, shows amber always, and keeps old news quiet", () => {
    let s = applyEvent(empty, received("stuck", T0), T0);
    s = applyCatchUp(
        s,
        [
            identified("stuck", T0 + 2000), // settles the grey toast (recent, so green)
            identified("old", T0 - 60_000), // old green: not shown
            received("ancient", T0 - 120_000), // old grey: not shown
            needsName("amber", "p-x", T0 - 3_600_000), // amber: always
        ],
        T0 + 3000,
    );
    expect(s.get("stuck")!.kind).toBe("ok");
    expect(s.has("old")).toBe(false);
    expect(s.has("ancient")).toBe(false);
    expect(s.get("amber")!.kind).toBe("warn");

    // A grey toast whose outcome is now old news is settled away rather than turned green.
    let t = applyEvent(empty, received("late", T0), T0);
    t = applyCatchUp(t, [identified("late", T0 + 1000)], T0 + 30_000);
    expect(t.has("late")).toBe(false);
});

test("subtitles: scanner and relative time, refreshed as time passes (UK time)", () => {
    const now = new Date("2026-10-18T18:00:00Z"); // 19:00 BST
    expect(toastSubtitle({ role: "in", at: "2026-10-18T17:59:40Z" }, now)).toBe(
        "Check-in scanner · just now",
    );
    expect(toastSubtitle({ role: "out", at: "2026-10-18T17:59:00Z" }, now)).toBe(
        "Check-out scanner · 1 minute ago",
    );
    expect(feedTime("2026-10-18T17:15:00Z", now)).toBe("45 minutes ago");
    expect(feedTime("2026-10-18T09:30:00Z", now)).toBe("10:30");
    expect(feedTime("2026-10-17T17:42:00Z", now)).toBe("18:42 yesterday");
    expect(feedTime("2026-10-15T09:00:00Z", now)).toBe("15 Oct");
});
