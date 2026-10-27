import type { Scanners } from "./api";

/** The Settings button's status dot (#87, #122): red for a problem, amber for things to sort out, none otherwise. It's
 * only about scanners. */
export interface ScannerStatus {
    level: "problem" | "attention" | "none";
    /** How many things need attention: problems plus things to sort out. */
    count: number;
    /** The Raspberry Pi hasn't been heard from lately. */
    hubOffline: boolean;
    /** Scanners reported not connected. */
    unplugged: number;
}

/**
 * Red when the hub is offline or a scanner is reported not connected; amber when there are check-ins to identify or
 * unmatched check-outs not yet dismissed (the scanners screen only lists undismissed ones); none otherwise.
 */
export function scannerStatus(s: Scanners): ScannerStatus {
    const hubOffline = !!s.hub && !s.hub.online;
    const unplugged = s.devices.filter((d) => d.present === false).length;
    const problems = (hubOffline ? 1 : 0) + unplugged;
    const toSort = s.noMatchCheckIns.length + s.unmatchedCheckOuts.length;
    return {
        level: problems > 0 ? "problem" : toSort > 0 ? "attention" : "none",
        count: problems + toSort,
        hubOffline,
        unplugged,
    };
}

/** The Settings button's `aria-label` (project/SettingsButton.dc.html): "Settings", "Settings: a scanner is unplugged"
 * or "Settings: 2 need attention". */
export function scannerLabel(status: ScannerStatus | null): string {
    if (!status || status.count === 0) return "Settings";
    if (status.level === "problem") {
        if (!status.hubOffline && status.unplugged === 1) return "Settings: a scanner is unplugged";
        if (!status.hubOffline && status.unplugged > 1)
            return `Settings: ${status.unplugged} scanners are unplugged`;
        if (status.hubOffline && status.unplugged === 0) return "Settings: the Raspberry Pi is offline";
    }

    return `Settings: ${status.count} need${status.count === 1 ? "s" : ""} attention`;
}
