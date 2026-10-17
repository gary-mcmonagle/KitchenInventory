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
