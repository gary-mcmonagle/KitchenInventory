import { groupBarcode, initial, itemsLabel, metaLine, scannedLabel, todayLine } from "./format";

test("meta line leaves out missing parts and shows ×qty only above 1", () => {
    expect(metaLine("Bisto", "230g", 2)).toBe("Bisto · 230g · ×2");
    expect(metaLine("Bisto", "230g", 1)).toBe("Bisto · 230g");
    expect(metaLine(null, "230g", 3)).toBe("230g · ×3");
    expect(metaLine("Bisto", null, 1)).toBe("Bisto");
    expect(metaLine(null, null, 1)).toBe("");
    expect(metaLine(" ", "", 1)).toBe("");
});

test("barcodes are grouped in fours", () => {
    expect(groupBarcode("501001234567")).toBe("5010 0123 4567");
    expect(groupBarcode("5000354922831")).toBe("5000 3549 2283 1");
    expect(groupBarcode("20055363")).toBe("2005 5363");
});

test("dates are UK time", () => {
    // 23:30 UTC on 17 October is 00:30 on the 18th in BST.
    expect(scannedLabel("2026-10-17T23:30:00.000Z")).toBe("Scanned 18 Oct");
    expect(todayLine(new Date("2026-10-17T22:30:00Z"))).toBe("Saturday 17 October");
});

test("items label and tile initial", () => {
    expect(itemsLabel(1)).toBe("1 item");
    expect(itemsLabel(30)).toBe("30 items");
    expect(initial("walnut halves")).toBe("W");
    expect(initial("50% Less Sodium Salt")).toBe("5");
    expect(initial("горчица")).toBe("Г");
    expect(initial("…")).toBe("?");
});

test("relative times, in UK time", async () => {
    const { relativeTime } = await import("./format");
    const now = new Date("2026-10-18T09:00:00Z"); // 10:00 BST
    expect(relativeTime("2026-10-18T08:59:40Z", now)).toBe("just now");
    expect(relativeTime("2026-10-18T08:55:00Z", now)).toBe("5 minutes ago");
    expect(relativeTime("2026-10-18T08:59:00Z", now)).toBe("1 minute ago");
    expect(relativeTime("2026-10-18T06:30:00Z", now)).toBe("today at 07:30");
    expect(relativeTime("2026-10-17T17:42:00Z", now)).toBe("yesterday at 18:42");
    expect(relativeTime("2026-10-12T17:42:00Z", now)).toBe("on 12 Oct at 18:42");
});

test("expiry badges: text and colour at the boundaries (as the API's ExpiryBadge)", async () => {
    const { expiryBadge } = await import("./format");
    const on = (days: number) => {
        const date = new Date(Date.UTC(2026, 9, 18 + days)).toISOString().slice(0, 10);
        return expiryBadge(days, date);
    };
    expect(on(-1)).toEqual({ text: "Est. expired", tone: "expired" });
    expect(on(0)).toEqual({ text: "Est. today", tone: "soon" });
    expect(on(1)).toEqual({ text: "Est. tomorrow", tone: "soon" });
    expect(on(3)).toEqual({ text: "Est. 3 days", tone: "soon" });
    expect(on(4)).toEqual({ text: "Est. 4 days", tone: "later" });
    expect(on(14)).toEqual({ text: "Est. 14 days", tone: "later" });
    expect(on(15)).toEqual({ text: "Est. 2 Nov", tone: "later" });
    expect(on(182)).toEqual({ text: "Est. 18 Apr", tone: "later" });
    expect(on(183)).toEqual({ text: "Est. Apr 2027", tone: "later" });
    expect(on(364)).toEqual({ text: "Est. Oct 2027", tone: "later" }); // never "Est. 17 Oct", which reads like today
});

test("shelf life in words, and the bought note", async () => {
    const { keepsAbout, boughtNote } = await import("./format");
    expect([2, 4, 7, 14, 30, 90, 180, 365, 730, 1095].map(keepsAbout)).toEqual([
        "2 days",
        "4 days",
        "7 days",
        "2 weeks",
        "a month",
        "3 months",
        "6 months",
        "a year",
        "2 years",
        "3 years",
    ]);
    expect(keepsAbout(1)).toBe("1 day");
    expect(keepsAbout(21)).toBe("3 weeks");
    // The soonest unit expires on 15 Oct and keeps 5 days: it was bought on 10 Oct.
    expect(boughtNote("2026-10-15", 5)).toBe("Bought 10 Oct · keeps about 5 days");
    expect(boughtNote("2027-01-02", 7)).toBe("Bought 26 Dec · keeps about 7 days");
});

test("item detail dates show the year only when it isn't this year", async () => {
    const { longDate, shortDate } = await import("./format");
    const now = new Date("2026-10-18T09:00:00Z");
    expect(longDate("2026-10-20", now)).toBe("Tuesday 20 October");
    expect(longDate("2028-10-16", now)).toBe("Monday 16 October 2028");
    expect(shortDate("2026-10-20", now)).toBe("20 Oct");
    expect(shortDate("2027-04-15", now)).toBe("15 Apr 2027");
    // New Year's Eve, 23:30 UTC on 31 Dec is still 31 Dec in the UK (GMT).
    expect(shortDate("2027-01-02", new Date("2026-12-31T23:30:00Z"))).toBe("2 Jan 2027");
});

test("scanners screen times (UK time)", async () => {
    const { dayTime, timeDay, interval } = await import("./format");
    const now = new Date("2026-10-18T08:00:00Z"); // 09:00 BST
    expect(dayTime("2026-10-18T07:15:00Z", now)).toBe("today 08:15");
    expect(dayTime("2026-10-17T17:42:00Z", now)).toBe("yesterday 18:42");
    expect(dayTime("2026-10-15T11:20:00Z", now)).toBe("15 Oct 12:20");
    expect(timeDay("2026-10-18T07:15:00Z", now)).toBe("08:15 today");
    expect(timeDay("2026-10-17T17:42:00Z", now)).toBe("18:42 yesterday");
    expect(timeDay("2026-10-14T18:03:00Z", now)).toBe("14 Oct, 19:03");
    expect([interval(120), interval(60), interval(300), interval(30)]).toEqual([
        "2 minutes",
        "1 minute",
        "5 minutes",
        "30 seconds",
    ]);
});
