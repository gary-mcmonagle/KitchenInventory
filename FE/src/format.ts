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
/** Further out than this, the badge shows the month and year: a day and month alone would read like this year. */
export const DAYS_SHOWN_AS_DAY_AND_MONTH = 182;

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
                  : daysUntil > DAYS_SHOWN_AS_DAY_AND_MONTH
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

/** The year in the UK today. */
function ukYear(now: Date): string {
    return now.toLocaleDateString("en-GB", { year: "numeric", timeZone: UK });
}

/** "Sunday 18 October", from a YYYY-MM-DD date; with the year ("Monday 16 October 2028") when it isn't this year. */
export function longDate(isoDate: string, now: Date = new Date()): string {
    // Built from parts: browsers differ on punctuation ("Monday, 16 October 2028" in Chrome).
    const date = new Date(`${isoDate}T00:00:00Z`);
    const part = (options: Intl.DateTimeFormatOptions) =>
        date.toLocaleDateString("en-GB", { ...options, timeZone: "UTC" });
    const year = isoDate.slice(0, 4) === ukYear(now) ? "" : ` ${isoDate.slice(0, 4)}`;
    return `${part({ weekday: "long" })} ${part({ day: "numeric" })} ${part({ month: "long" })}${year}`;
}

/** "18 Oct", from a YYYY-MM-DD date; "16 Oct 2028" when it isn't this year. */
export function shortDate(isoDate: string, now: Date = new Date()): string {
    return new Date(`${isoDate}T00:00:00Z`).toLocaleDateString("en-GB", {
        day: "numeric",
        month: "short",
        year: isoDate.slice(0, 4) === ukYear(now) ? undefined : "numeric",
        timeZone: "UTC",
    });
}

/** "13 Oct": a check-in time as its UK calendar date. */
export function boughtDate(iso: string): string {
    return new Date(iso).toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: UK });
}

/** The use-by card's first sentence: "In 2 days.", "Tomorrow.", "Today.", "Expired 3 days ago.". */
export function untilSentence(daysUntil: number): string {
    if (daysUntil < -1) return `Expired ${-daysUntil} days ago.`;
    if (daysUntil === -1) return "Expired yesterday.";
    if (daysUntil === 0) return "Today.";
    if (daysUntil === 1) return "Tomorrow.";
    return `In ${daysUntil} days.`;
}

/** "today 08:15", "yesterday 18:42", "15 Oct 12:20" (UK time). */
export function dayTime(iso: string, now: Date = new Date()): string {
    const at = new Date(iso);
    const time = at.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", timeZone: UK });
    const day = (d: Date) => d.toLocaleDateString("en-CA", { timeZone: UK });
    if (day(at) === day(now)) return `today ${time}`;
    if (day(at) === day(new Date(now.getTime() - 86400000))) return `yesterday ${time}`;
    return `${at.toLocaleDateString("en-GB", { day: "numeric", month: "short", timeZone: UK })} ${time}`;
}

/** "08:15 today", "18:42 yesterday", "15 Oct, 12:20" (UK time): for "Unplugged since …". */
export function timeDay(iso: string, now: Date = new Date()): string {
    const [day, time] = splitDayTime(dayTime(iso, now));
    return day === "today" || day === "yesterday" ? `${time} ${day}` : `${day}, ${time}`;
}

function splitDayTime(text: string): [string, string] {
    const i = text.lastIndexOf(" ");
    return [text.slice(0, i), text.slice(i + 1)];
}

/** "2 minutes", "1 minute", "30 seconds": how often the hub reports in. */
export function interval(seconds: number): string {
    if (seconds % 60 === 0) {
        const minutes = seconds / 60;
        return `${minutes} ${minutes === 1 ? "minute" : "minutes"}`;
    }
    return `${seconds} seconds`;
}
