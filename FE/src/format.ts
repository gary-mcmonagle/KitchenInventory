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
