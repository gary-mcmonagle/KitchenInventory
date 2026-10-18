const UK = "Europe/London";

/** "Saturday 17 October", in UK time. */
export function todayLine(now: Date = new Date()): string {
    return now.toLocaleDateString("en-GB", { weekday: "long", day: "numeric", month: "long", timeZone: UK });
}

/** "1 item", "30 items". */
export function itemsLabel(count: number): string {
    return `${count} ${count === 1 ? "item" : "items"}`;
}

/** "brand · size · ×qty", leaving out what's missing; ×qty only above 1. */
export function metaLine(brand: string | null, size: string | null, count: number): string {
    return [brand?.trim(), size?.trim(), count > 1 ? `×${count}` : null].filter(Boolean).join(" · ");
}

/** "5010 0123 4567": digits in groups of four. */
export function groupBarcode(barcode: string): string {
    return barcode.replace(/(\d{4})(?=\d)/g, "$1 ");
}

/** "Scanned 17 Oct", in UK time. */
export function scannedLabel(iso: string): string {
    const date = new Date(iso).toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: UK });
    return `Scanned ${date}`;
}

/** The tile's letter: the name's first letter or digit. */
export function initial(name: string): string {
    const match = name.match(/[\p{L}\p{N}]/u);
    return match ? match[0].toUpperCase() : "?";
}

/** "just now", "5 minutes ago", "today at 18:42", "yesterday at 18:42", "on 12 Oct at 18:42" (UK time). */
export function relativeTime(iso: string, now: Date = new Date()): string {
    const at = new Date(iso);
    const minutes = Math.floor((now.getTime() - at.getTime()) / 60000);
    if (minutes < 1) return "just now";
    if (minutes < 60) return `${minutes} ${minutes === 1 ? "minute" : "minutes"} ago`;
    const day = (d: Date) => d.toLocaleDateString("en-CA", { timeZone: UK }); // YYYY-MM-DD in UK time
    const time = at.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", timeZone: UK });
    if (day(at) === day(now)) return `today at ${time}`;
    if (day(at) === day(new Date(now.getTime() - 86400000))) return `yesterday at ${time}`;
    return `on ${at.toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: UK })} at ${time}`;
}

export type ExpiryTone = "expired" | "soon" | "later";

/** The "Est. …" badge (#52), as in the mockup; the API's digest uses the same rules (ExpiryBadge.cs). Expired is red,
 * 3 days or fewer amber, the rest green. Beyond 14 days it's the date ("Est. 2 Nov"), or month and year when it's over
 * a year away ("Est. Oct 2028"). */
export function expiryBadge(daysUntil: number, isoDate: string): { text: string; tone: ExpiryTone } {
    const tone: ExpiryTone = daysUntil < 0 ? "expired" : daysUntil <= 3 ? "soon" : "later";
    const date = new Date(`${isoDate}T00:00:00Z`);
    const when =
        daysUntil < 0
            ? "expired"
            : daysUntil === 0
              ? "today"
              : daysUntil === 1
                ? "tomorrow"
                : daysUntil <= 14
                  ? `${daysUntil} days`
                  : daysUntil > 365
                    ? date.toLocaleDateString("en-GB", { month: "short", year: "numeric", timeZone: "UTC" })
                    : date.toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: "UTC" });
    return { text: `Est. ${when}`, tone };
}

/** A shelf life in words: "4 days", "2 weeks", "a month", "6 months", "a year", "2 years". */
export function keepsAbout(days: number): string {
    const plural = (n: number, unit: string, one: string) => (n === 1 ? one : `${n} ${unit}s`);
    if (days < 14) return plural(days, "day", "1 day");
    if (days < 28) return plural(Math.round(days / 7), "week", "a week");
    if (days < 365) return plural(Math.round(days / 30), "month", "a month");
    return plural(Math.round(days / 365), "year", "a year");
}

/** "Bought 10 Oct · keeps about 5 days": the bought date of the soonest unit (its expiry minus the shelf life). */
export function boughtNote(soonestIsoDate: string, shelfLifeDays: number): string {
    const bought = new Date(`${soonestIsoDate}T00:00:00Z`);
    bought.setUTCDate(bought.getUTCDate() - shelfLifeDays);
    const date = bought.toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: "UTC" });
    return `Bought ${date} · keeps about ${keepsAbout(shelfLifeDays)}`;
}
