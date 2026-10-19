import type { Scanners } from "./api";

/** The Scanners button's status dot (#87): red for a problem, amber for things to sort out, none otherwise. */
export interface ScannerStatus {
    level: "problem" | "attention" | "none";
    /** How many things need attention: problems plus things to sort out. */
    count: number;
}

/**
 * Red when the hub is offline or a scanner is reported not connected; amber when there are check-ins to identify or
 * unmatched check-outs not yet dismissed (the scanners screen only lists undismissed ones); none otherwise.
 */
export function scannerStatus(s: Scanners): ScannerStatus {
    const problems = (s.hub && !s.hub.online ? 1 : 0) + s.devices.filter((d) => d.present === false).length;
    const toSort = s.noMatchCheckIns.length + s.unmatchedCheckOuts.length;
    return {
        level: problems > 0 ? "problem" : toSort > 0 ? "attention" : "none",
        count: problems + toSort,
    };
}

export function scannerLabel(status: ScannerStatus | null): string {
    if (!status || status.count === 0) return "Scanners and notifications";
    return `Scanners and notifications: ${status.count} need${status.count === 1 ? "s" : ""} attention`;
}
