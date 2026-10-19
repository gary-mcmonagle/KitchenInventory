import { useCallback, useEffect, useRef, useState } from "react";
import { NoAccessError, SignInRequiredError, type ApiClient, type Scanners } from "./api";
import { dayTime, groupBarcode, interval, relativeTime, timeDay } from "./format";
import { assignPath, namePath, navigate } from "./router";
import { feedTime } from "./scanFeed";
import { TOAST_MS } from "./StockScreen";
import { signalRConnection, useScanFeed, type FeedConnection } from "./useScanFeed";
import styles from "./Scanners.module.css";
import { ThisPhoneCard } from "./ThisPhoneCard";

type Device = Scanners["devices"][number];
type Toast = { kind: "undone"; name: string } | { kind: "error"; text: string };
const PULL_TO_REFRESH = 70; // px

/**
 * The scanners screen (#55, project/Scanners.dc.html): is everything working (each scanner's receiver and the Pi),
 * and what needs sorting (check-ins to identify, check-outs with no match, and recent check-outs to undo). Reloads on
 * open, on returning to the app, on pull-to-refresh, and when the scan feed says something happened.
 */
export function ScannersScreen({
    api,
    feedConnection = signalRConnection,
}: {
    api: ApiClient;
    feedConnection?: (api: ApiClient) => FeedConnection;
}) {
    const [data, setData] = useState<Scanners | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());
    const [toast, setToast] = useState<Toast | null>(null);
    const [pull, setPull] = useState(0);
    const pullStart = useRef<number | null>(null);
    const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    const live = useRef(true);

    const load = useCallback(async () => {
        try {
            const next = await api.scanners();
            if (!live.current) return;
            setData(next);
            setError(null);
        } catch (e) {
            if (!live.current || e instanceof SignInRequiredError) return;
            setError(
                e instanceof NoAccessError
                    ? "You're signed in, but this account can't use Cupboard Tracker."
                    : "Couldn't load the scanners. Pull down to try again.",
            );
        }
    }, [api]);

    useEffect(() => {
        live.current = true;
        void load();
        const onVisible = () => document.visibilityState === "visible" && void load();
        document.addEventListener("visibilitychange", onVisible);
        return () => {
            live.current = false;
            document.removeEventListener("visibilitychange", onVisible);
            clearTimeout(toastTimer.current);
        };
    }, [load]);

    // Scans change these lists: reload (debounced by the feed) when the feed says stock changed.
    const { now } = useScanFeed(api, () => void load(), feedConnection);

    const showToast = (next: Toast) => {
        clearTimeout(toastTimer.current);
        setToast(next);
        toastTimer.current = setTimeout(() => setToast(null), TOAST_MS);
    };

    const withBusy = async (key: string, action: () => Promise<void>) => {
        if (busy.has(key)) return;
        setBusy((b) => new Set(b).add(key));
        try {
            await action();
        } finally {
            setBusy((b) => {
                const next = new Set(b);
                next.delete(key);
                return next;
            });
        }
    };

    const dismiss = (scanId: string) =>
        withBusy(scanId, async () => {
            try {
                await api.dismissUnmatched(scanId);
                setData(
                    (d) =>
                        d && {
                            ...d,
                            unmatchedCheckOuts: d.unmatchedCheckOuts.filter((u) => u.scanId !== scanId),
                        },
                );
            } catch (e) {
                if (!(e instanceof SignInRequiredError))
                    showToast({ kind: "error", text: "Couldn't dismiss that. Try again." });
            }
        });

    const undo = (unitId: string, name: string) =>
        withBusy(unitId, async () => {
            try {
                await api.undoCheckOut(unitId);
                setData(
                    (d) =>
                        d && {
                            ...d,
                            recentlyCheckedOut: d.recentlyCheckedOut.filter((r) => r.unitId !== unitId),
                        },
                );
                showToast({ kind: "undone", name });
            } catch (e) {
                if (!(e instanceof SignInRequiredError))
                    showToast({ kind: "error", text: "Couldn't undo that. Try again." });
            }
        });

    const onTouchStart = (e: React.TouchEvent) => {
        pullStart.current = window.scrollY <= 0 ? e.touches[0].clientY : null;
    };
    const onTouchMove = (e: React.TouchEvent) => {
        if (pullStart.current !== null)
            setPull(Math.max(0, Math.min(120, e.touches[0].clientY - pullStart.current)));
    };
    const onTouchEnd = () => {
        if (pull >= PULL_TO_REFRESH) void load();
        pullStart.current = null;
        setPull(0);
    };

    const nowDate = new Date(now);
    return (
        <div
            className={styles.screen}
            onTouchStart={onTouchStart}
            onTouchMove={onTouchMove}
            onTouchEnd={onTouchEnd}
        >
            <div className={styles.pull} style={{ height: pull }} aria-hidden={pull === 0}>
                {pull > 0 && (pull >= PULL_TO_REFRESH ? "Release to refresh" : "Pull to refresh")}
            </div>
            <header className={styles.header}>
                <a
                    href="/"
                    className={styles.back}
                    aria-label="Back to stock"
                    onClick={(e) => {
                        e.preventDefault();
                        navigate("/");
                    }}
                >
                    <span className={styles.backIcon} aria-hidden="true">
                        <svg
                            width="22"
                            height="22"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2.2"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                        >
                            <path d="M15 5l-7 7 7 7" />
                        </svg>
                    </span>
                    Stock
                </a>
                <div className={styles.checked}>
                    {data ? `Last checked ${relativeTime(data.generatedAt, nowDate)}` : "Checking…"}
                </div>
                <h1 className={styles.title}>Scanners</h1>
            </header>

            <div className={styles.content}>
                {error && (
                    <p className={styles.notice} role="alert">
                        {error}
                    </p>
                )}
                {data && (
                    <>
                        <div className={styles.cards}>
                            {data.devices.map((d) => (
                                <ScannerCard key={d.deviceId} device={d} now={nowDate} />
                            ))}
                        </div>

                        <div className={styles.card}>
                            <div className={styles.cardText}>
                                <div className={styles.piName}>Raspberry Pi</div>
                                <div className={styles.muted}>
                                    {data.hub
                                        ? `Reporting in every ${interval(data.hub.heartbeatIntervalSeconds)} · last heard ${relativeTime(data.hub.lastSeenAt, nowDate)}`
                                        : "Not heard from yet"}
                                </div>
                            </div>
                            {data.hub ? (
                                <span
                                    className={`${styles.pill} ${data.hub.online ? styles.ok : styles.bad}`}
                                >
                                    {data.hub.online ? "Online" : "Offline"}
                                </span>
                            ) : (
                                <span className={`${styles.pill} ${styles.unknown}`}>Unknown</span>
                            )}
                        </div>

                        <ThisPhoneCard api={api} />

                        <Section
                            id="no-match-in"
                            title="Check-ins with no match"
                            count={`${data.noMatchCheckIns.length} to identify`}
                            help="These barcodes were scanned in but we could not find the product. Identify them to give them a name."
                            empty="Nothing to identify."
                        >
                            {data.noMatchCheckIns.map((n) => (
                                <li key={n.scanId ?? n.productId} className={styles.row}>
                                    <div className={styles.rowText}>
                                        <span className={styles.rowTitle}>
                                            {n.status === "storeLabel"
                                                ? "Store label"
                                                : groupBarcode(n.barcode)}
                                        </span>
                                        <span className={styles.muted}>
                                            Check-in, {dayTime(n.scannedAt, nowDate)}
                                        </span>
                                    </div>
                                    <button
                                        type="button"
                                        className={styles.primary}
                                        onClick={() =>
                                            navigate(
                                                n.status === "storeLabel" && n.scanId
                                                    ? assignPath(n.scanId)
                                                    : namePath(n.productId!),
                                            )
                                        }
                                    >
                                        <svg
                                            width="18"
                                            height="18"
                                            viewBox="0 0 24 24"
                                            fill="none"
                                            stroke="currentColor"
                                            strokeWidth="2"
                                            strokeLinecap="round"
                                            strokeLinejoin="round"
                                            aria-hidden="true"
                                        >
                                            <path d="M4 8.5A1.5 1.5 0 0 1 5.5 7H8l1.5-2h5L16 7h2.5A1.5 1.5 0 0 1 20 8.5v9a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 17.5z" />
                                            <circle cx="12" cy="13" r="3.2" />
                                        </svg>
                                        {n.status === "storeLabel" ? "Pick" : "Identify"}
                                    </button>
                                </li>
                            ))}
                        </Section>

                        <Section
                            id="no-match-out"
                            title="Check-outs with no match"
                            count={`${data.unmatchedCheckOuts.length} to review`}
                            help="You scanned these out but nothing with that barcode was in stock. Usually it was bought before the stock-take."
                            empty="Nothing to review."
                        >
                            {data.unmatchedCheckOuts.map((u) => (
                                <li key={u.scanId} className={styles.row}>
                                    <div className={styles.rowText}>
                                        <span className={styles.rowTitle}>
                                            {u.storeLabel
                                                ? "Store label"
                                                : (u.displayName ??
                                                  `Unknown item ${groupBarcode(u.barcode)}`)}
                                        </span>
                                        <span className={styles.muted}>
                                            {u.storeLabel
                                                ? `Check it out from the app · scanned out ${dayTime(u.scannedAt, nowDate)}`
                                                : `Scanned out ${dayTime(u.scannedAt, nowDate)}`}
                                        </span>
                                    </div>
                                    <button
                                        type="button"
                                        className={styles.secondary}
                                        disabled={busy.has(u.scanId)}
                                        onClick={() => void dismiss(u.scanId)}
                                    >
                                        Dismiss
                                    </button>
                                </li>
                            ))}
                        </Section>

                        <Section
                            id="recent-out"
                            title="Recently checked out"
                            count={`${data.recentlyCheckedOut.length} in 48 hours`}
                            help="Scanned out the wrong thing? Undo puts it back in stock."
                            empty="Nothing checked out in the last 48 hours."
                        >
                            {data.recentlyCheckedOut.map((r) => (
                                <li key={r.unitId} className={styles.row}>
                                    <div className={styles.rowText}>
                                        <span className={styles.rowTitle}>{r.displayName}</span>
                                        <span className={styles.muted}>
                                            {r.source === "ui" ? "Checked out in the app" : "Scanned out"}{" "}
                                            {dayTime(r.checkedOutAt, nowDate)}
                                        </span>
                                    </div>
                                    <button
                                        type="button"
                                        className={styles.secondary}
                                        disabled={busy.has(r.unitId)}
                                        aria-label={`Undo check-out of ${r.displayName}`}
                                        onClick={() => void undo(r.unitId, r.displayName)}
                                    >
                                        Undo
                                    </button>
                                </li>
                            ))}
                        </Section>
                    </>
                )}
            </div>

            {toast && (
                <div className={styles.toast} role="status" aria-label="Notification">
                    {toast.kind === "undone" ? `${toast.name} is back in stock` : toast.text}
                </div>
            )}
        </div>
    );
}

function ScannerCard({ device, now }: { device: Device; now: Date }) {
    const name = device.role === "in" ? "Check-in scanner" : "Check-out scanner";
    const unplugged = device.present === false;
    const line = unplugged
        ? device.absentSince
            ? `Unplugged since ${timeDay(device.absentSince, now)}`
            : "Unplugged"
        : device.lastScanAt
          ? `Last scan ${feedTime(device.lastScanAt, now)}`
          : "No scans yet";
    return (
        <div className={styles.card}>
            <div className={styles.icon} aria-hidden="true">
                <svg
                    width="24"
                    height="24"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                >
                    <path
                        d={
                            device.role === "in"
                                ? "M12 4v10M8 10l4 4 4-4M5 19h14"
                                : "M12 20V10M8 14l4-4 4 4M5 5h14"
                        }
                    />
                </svg>
            </div>
            <div className={styles.cardText}>
                <div className={styles.cardName}>{name}</div>
                <div className={unplugged ? styles.alert : styles.muted}>{line}</div>
            </div>
            {device.present === null ? (
                <span className={`${styles.pill} ${styles.unknown}`}>Unknown</span>
            ) : device.present ? (
                <span className={`${styles.pill} ${styles.ok}`}>Connected</span>
            ) : (
                <span className={`${styles.pill} ${styles.bad}`}>Not connected</span>
            )}
        </div>
    );
}

function Section(props: {
    id: string;
    title: string;
    count: string;
    help: string;
    empty: string;
    children: React.ReactNode[];
}) {
    return (
        <section className={styles.section} aria-labelledby={props.id}>
            <h2 id={props.id} className={styles.sectionTitle}>
                <span>{props.title}</span>
                <span className={styles.sectionCount}>{props.count}</span>
            </h2>
            <div className={styles.help}>{props.help}</div>
            {props.children.length > 0 ? (
                <ul className={styles.list}>{props.children}</ul>
            ) : (
                <p className={styles.empty}>{props.empty}</p>
            )}
        </section>
    );
}
