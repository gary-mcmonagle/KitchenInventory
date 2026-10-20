import { useCallback, useEffect, useMemo, useRef, useState, type TouchEvent } from "react";
import {
    ApiError,
    errorCode,
    NoAccessError,
    SignInRequiredError,
    type ApiClient,
    type Stock,
    type StockProduct,
    type StockSection,
} from "./api";
import {
    boughtNote,
    expiryBadge,
    groupBarcode,
    itemsLabel,
    stockRowText,
    scannedLabel,
    stateNote,
    todayLine,
} from "./format";
import { AISLES, type AisleInfo } from "./aisles";
import { takeFlash } from "./flash";
import { rememberItemPreview } from "./itemPreview";
import { ProductTile } from "./ProductTile";
import { createSeenReporter } from "./push";
import { ADD_PATH, itemPath, namePath, navigate, SCANNERS_PATH } from "./router";
import { scannerLabel, scannerStatus, type ScannerStatus } from "./scannerStatus";
import { matchesSearch, searchWords } from "./stockSearch";
import styles from "./Stock.module.css";
import { ScanFeedView } from "./ScanFeedView";
import { signalRConnection, useScanFeed, type FeedConnection } from "./useScanFeed";

const PULL_TO_REFRESH = 70; // px
export const TOAST_MS = 6000; // as in the mockup

type Toast =
    | { kind: "checkedOut"; unitId: string; product: StockProduct; name: string }
    | { kind: "opened"; unitId: string; name: string }
    | { kind: "error"; text: string }
    | { kind: "info"; text: string };

/** The stock with one product's count set (the row is removed at 0, put back if missing), and the totals recomputed. */
export function withCount(stock: Stock, product: StockProduct, count: number): Stock {
    const others = stock.products.filter((p) => p.productId !== product.productId);
    let products = others;
    if (count > 0) {
        const index = stock.products.findIndex((p) => p.productId === product.productId);
        const updated = { ...product, count };
        products =
            index >= 0
                ? stock.products.map((p) => (p.productId === product.productId ? updated : p))
                : insertSorted(others, updated);
    }
    return {
        ...stock,
        products,
        totalUnits: products.reduce((n, p) => n + p.count, 0),
        useFirstCount: products.filter((p) => p.section === "useFirst").length,
    };
}

const SECTION_ORDER: StockSection[] = ["useFirst", "later", "waitingForName"];

/** The API's order: by section; within Use first and Later, soonest estimated expiry (none last), then name ignoring
 * case; Waiting for a name, newest first. */
function compareStock(a: StockProduct, b: StockProduct): number {
    const section = SECTION_ORDER.indexOf(a.section) - SECTION_ORDER.indexOf(b.section);
    if (section !== 0) return section;
    if (a.section === "waitingForName") return b.latestCheckedInAt.localeCompare(a.latestCheckedInAt);
    if (a.soonestEstimatedExpiryDate !== b.soonestEstimatedExpiryDate) {
        if (a.soonestEstimatedExpiryDate === null) return 1;
        if (b.soonestEstimatedExpiryDate === null) return -1;
        return a.soonestEstimatedExpiryDate.localeCompare(b.soonestEstimatedExpiryDate);
    }
    return a.displayName.localeCompare(b.displayName, "en", { sensitivity: "base" });
}

/** Back where the API would put it. */
function insertSorted(products: StockProduct[], product: StockProduct): StockProduct[] {
    const at = products.findIndex((p) => compareStock(p, product) > 0);
    return at < 0 ? [...products, product] : [...products.slice(0, at), product, ...products.slice(at)];
}

/** One aisle on the stock list (#71): its products, use first first, then the rest by soonest, then name. */
export interface AisleGroup {
    aisle: AisleInfo;
    products: StockProduct[];
    useFirstCount: number;
    anyExpired: boolean;
}

const bySoonest = (a: StockProduct, b: StockProduct) => {
    if (a.soonestEstimatedExpiryDate !== b.soonestEstimatedExpiryDate) {
        if (a.soonestEstimatedExpiryDate === null) return 1;
        if (b.soonestEstimatedExpiryDate === null) return -1;
        return a.soonestEstimatedExpiryDate.localeCompare(b.soonestEstimatedExpiryDate);
    }
    return a.displayName.localeCompare(b.displayName, "en", { sensitivity: "base" });
};

/** Named products by aisle, in shop order, empty aisles left out: the API's grouping (GET /api/stock `aisles`), worked
 * out here from the flat list so a check-out or undo can update the screen at once. */
export function groupByAisle(products: StockProduct[]): AisleGroup[] {
    const known = products.filter((p) => p.section !== "waitingForName");
    return AISLES.map((aisle) => {
        const inAisle = known.filter(
            (p) => (AISLES.some((a) => a.id === p.aisleId) ? p.aisleId : "other") === aisle.id,
        );
        const products = [
            ...inAisle.filter((p) => p.section === "useFirst").sort(bySoonest),
            ...inAisle.filter((p) => p.section !== "useFirst").sort(bySoonest),
        ];
        return {
            aisle,
            products,
            useFirstCount: products.filter((p) => p.section === "useFirst").length,
            anyExpired: products.some((p) => (p.daysUntilSoonest ?? 0) < 0),
        };
    }).filter((g) => g.products.length > 0);
}

const OPEN_KEY = "cupboard.aisles.open";

/** Gary's own open/closed choice per aisle, on this device (#71). Storage can fail: then the defaults apply. */
function readOpen(): Record<string, boolean> {
    try {
        const raw = window.localStorage.getItem(OPEN_KEY);
        return raw ? (JSON.parse(raw) as Record<string, boolean>) : {};
    } catch {
        return {};
    }
}

function writeOpen(open: Record<string, boolean>) {
    try {
        window.localStorage.setItem(OPEN_KEY, JSON.stringify(open));
    } catch {
        // Not remembered on this device; the screen still works.
    }
}

type Phase = "loading" | "ready" | "error" | "noAccess";

/** The stock list (#49, #52): what's in the house now, in Use first, Later and Waiting for a name. Reloads on open, on
 * returning to the app, and on pull-to-refresh. */
/** The search (#91), kept in memory while the app is open, so it survives a trip to item detail and back but not a
 * reload. Never stored on the device. */
let rememberedQuery = "";

/** For tests: start with an empty search, as after a reload. */
export const forgetStockSearch = () => {
    rememberedQuery = "";
};

export function StockScreen({
    api,
    feedConnection = signalRConnection,
    visible = true,
}: {
    api: ApiClient;
    /** False while it's kept mounted under a screen reached from it (#103). */
    visible?: boolean;
    /** The live scan feed's connection (#54); tests pass a fake. */
    feedConnection?: (api: ApiClient) => FeedConnection;
}) {
    const [stock, setStock] = useState<Stock | null>(null);
    const [status, setStatus] = useState<ScannerStatus | null>(null);
    const [phase, setPhase] = useState<Phase>("loading");
    const [loadError, setLoadError] = useState<string | null>(null);
    const [refreshing, setRefreshing] = useState(false);
    const [offline, setOffline] = useState(() => !navigator.onLine);
    const [pull, setPull] = useState(0);
    const pullStart = useRef<number | null>(null);
    const live = useRef(true);
    const [inFlight, setInFlight] = useState<ReadonlySet<string>>(new Set());
    const [toast, setToast] = useState<Toast | null>(null);
    const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

    const showToast = useCallback((next: Toast) => {
        clearTimeout(toastTimer.current);
        setToast(next);
        toastTimer.current = setTimeout(() => setToast(null), TOAST_MS);
    }, []);
    useEffect(() => () => clearTimeout(toastTimer.current), []);

    // "Added 3 items" after adding from the app (#58).
    useEffect(() => {
        if (!visible) return; // shown when the list is visible again, e.g. back from Add (#103)
        const message = takeFlash();
        if (message) showToast({ kind: "info", text: message });
    }, [showToast, visible]);

    const setBusy = (productId: string, busy: boolean) =>
        setInFlight((current) => {
            const next = new Set(current);
            if (busy) next.add(productId);
            else next.delete(productId);
            return next;
        });

    const checkOut = async (product: StockProduct) => {
        if (inFlight.has(product.productId)) return; // no double check-outs from quick taps
        setBusy(product.productId, true);
        setStock((s) => s && withCount(s, product, product.count - 1)); // optimistic
        try {
            const result = await api.checkOut(product.productId);
            setStock((s) => s && withCount(s, product, result.remainingCount));
            showToast({ kind: "checkedOut", unitId: result.unitId, product, name: product.displayName });
            void load({ quiet: true }); // the soonest unit went, so its badge may change
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            setStock((s) => s && withCount(s, product, product.count)); // revert
            showToast({
                kind: "error",
                text:
                    error instanceof ApiError && error.status === 409
                        ? `There's no ${product.displayName} left to check out.`
                        : "Couldn't check that out. Try again.",
            });
            if (error instanceof ApiError && error.status === 409) void load();
        } finally {
            setBusy(product.productId, false);
        }
    };

    /** Opened on a row (#65): the unit used next is opened today; its date and pill update from the API. */
    const openOne = async (product: StockProduct) => {
        if (inFlight.has(product.productId)) return;
        setBusy(product.productId, true);
        const withOpened = (n: number) => (s: Stock | null) =>
            s && {
                ...s,
                products: s.products.map((p) =>
                    p.productId === product.productId ? { ...p, openedCount: n } : p,
                ),
            };
        setStock(withOpened(1)); // optimistic: greys the button out at once
        try {
            const result = await api.openNext(product.productId);
            showToast({ kind: "opened", unitId: result.unit.unitId, name: product.displayName });
            void load({ quiet: true }); // its date moves in, and maybe into Use first
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            setStock(withOpened(product.openedCount)); // revert
            showToast({
                kind: "error",
                text:
                    error instanceof ApiError && error.status === 409
                        ? `${product.displayName}: one's already open, or they're all in the freezer.`
                        : "Couldn't save that. Try again.",
            });
            void load({ quiet: true });
        } finally {
            setBusy(product.productId, false);
        }
    };

    const undoOpened = async (done: Extract<Toast, { kind: "opened" }>) => {
        clearTimeout(toastTimer.current);
        setToast(null);
        try {
            await api.undoStateChange(done.unitId);
            void load({ quiet: true });
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            showToast({ kind: "error", text: "Couldn't undo that. Try again." });
        }
    };

    const undo = async (done: Extract<Toast, { kind: "checkedOut" }>) => {
        clearTimeout(toastTimer.current);
        setToast(null);
        const before = stock?.products.find((p) => p.productId === done.product.productId)?.count ?? 0;
        setStock((s) => s && withCount(s, done.product, before + 1)); // optimistic
        try {
            const result = await api.undoCheckOut(done.unitId);
            setStock((s) => s && withCount(s, done.product, result.count));
            void load({ quiet: true });
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            setStock((s) => s && withCount(s, done.product, before)); // revert
            showToast({ kind: "error", text: "Couldn't undo that. Try again." });
        }
    };

    const load = useCallback(
        async ({ quiet = false }: { quiet?: boolean } = {}) => {
            if (!navigator.onLine) {
                setOffline(true);
                return;
            }
            if (!quiet) setRefreshing(true);
            // The Scanners button's dot (#87) refreshes with the stock list; it never holds the list up.
            Promise.resolve()
                .then(() => api.scanners())
                .then((s) => live.current && setStatus(scannerStatus(s)))
                .catch(() => undefined);
            try {
                const next = await api.stock();
                if (!live.current) return;
                setStock(next);
                setPhase("ready");
            } catch (error) {
                if (!live.current || error instanceof SignInRequiredError || quiet) return;
                setLoadError(errorCode(error));
                setPhase(error instanceof NoAccessError ? "noAccess" : "error");
            } finally {
                if (live.current) setRefreshing(false);
            }
        },
        [api],
    );

    useEffect(() => {
        live.current = true;
        void load();
        const onVisible = () => document.visibilityState === "visible" && void load();
        const onOnline = () => {
            setOffline(false);
            void load();
        };
        const onOffline = () => setOffline(true);
        document.addEventListener("visibilitychange", onVisible);
        window.addEventListener("online", onOnline);
        window.addEventListener("offline", onOffline);
        return () => {
            live.current = false;
            document.removeEventListener("visibilitychange", onVisible);
            window.removeEventListener("online", onOnline);
            window.removeEventListener("offline", onOffline);
        };
    }, [load]);

    // The live scan feed (#54): toasts, and the stock list refetches itself when a scan changes stock.
    const markSeen = useMemo(() => createSeenReporter(api), [api]);
    const scanFeed = useScanFeed(api, () => void load({ quiet: true }), feedConnection, markSeen);

    const onTouchStart = (e: TouchEvent) => {
        pullStart.current = window.scrollY <= 0 ? e.touches[0].clientY : null;
    };
    const onTouchMove = (e: TouchEvent) => {
        if (pullStart.current !== null)
            setPull(Math.max(0, Math.min(120, e.touches[0].clientY - pullStart.current)));
    };
    const onTouchEnd = () => {
        if (pull >= PULL_TO_REFRESH) void load();
        pullStart.current = null;
        setPull(0);
    };

    const [openState, setOpenState] = useState<Record<string, boolean>>(readOpen);
    const [useFirstOnly, setUseFirstOnly] = useState(false); // never remembered: always off on open
    const [query, setQueryState] = useState(rememberedQuery);
    const setQuery = (text: string) => {
        rememberedQuery = text;
        setQueryState(text);
    };
    const words = searchWords(query);
    const searching = words.length > 0;
    const toggleAisle = (group: AisleGroup) =>
        setOpenState((current) => {
            const next = {
                ...current,
                [group.aisle.id]: !(current[group.aisle.id] ?? true),
            };
            writeOpen(next);
            return next;
        });

    const all = stock?.products ?? [];
    const found = searching ? all.filter((p) => matchesSearch(p, words)) : all;
    const groups = groupByAisle(useFirstOnly ? found.filter((p) => p.section === "useFirst") : found);
    const waiting = useFirstOnly ? [] : found.filter((p) => p.section === "waitingForName");
    const renderKnown = (p: StockProduct) => (
        <KnownRow
            key={p.productId}
            product={p}
            busy={inFlight.has(p.productId)}
            onCheckOut={() => void checkOut(p)}
            onOpen={() => void openOne(p)}
        />
    );

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
                <div className={styles.headerRow}>
                    <div className={styles.headerText}>
                        <div className={styles.date}>{todayLine()}</div>
                        <h1 className={styles.title}>Cupboard</h1>
                        {stock && (
                            <div className={styles.summary}>
                                {`${itemsLabel(stock.totalUnits)} · ${stock.useFirstCount} to use first`}
                            </div>
                        )}
                    </div>
                    {/* Scanners and Add live here since the three-tab nav (#87). */}
                    <a
                        href={SCANNERS_PATH}
                        className={styles.scannersButton}
                        aria-label={scannerLabel(status)}
                        onClick={(e) => {
                            e.preventDefault();
                            navigate(SCANNERS_PATH);
                        }}
                    >
                        <svg
                            width="22"
                            height="22"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                            aria-hidden="true"
                        >
                            <path d="M4 7v10M7 7v10M10 7v10M14 7v10M17 7v10M20 7v10" />
                        </svg>
                        {status && status.level !== "none" && (
                            <span
                                className={`${styles.statusDot} ${status.level === "problem" ? styles.dotProblem : styles.dotAttention}`}
                                data-level={status.level}
                                aria-hidden="true"
                            />
                        )}
                    </a>
                    <a
                        href={ADD_PATH}
                        className={styles.addButton}
                        onClick={(e) => {
                            e.preventDefault();
                            navigate(ADD_PATH);
                        }}
                    >
                        <svg
                            width="18"
                            height="18"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2.4"
                            strokeLinecap="round"
                            aria-hidden="true"
                        >
                            <path d="M12 5v14M5 12h14" />
                        </svg>
                        Add
                    </a>
                </div>
                {stock && stock.products.length > 0 && (
                    <div className={styles.searchBox}>
                        <svg
                            width="20"
                            height="20"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2"
                            strokeLinecap="round"
                            aria-hidden="true"
                        >
                            <circle cx="11" cy="11" r="7" />
                            <path d="M20 20l-3.5-3.5" />
                        </svg>
                        <input
                            type="search"
                            enterKeyHint="search"
                            aria-label="Search stock"
                            className={styles.searchInput}
                            placeholder="Search your stock"
                            autoComplete="off"
                            value={query}
                            onChange={(e) => setQuery(e.target.value)}
                            onKeyDown={(e) => {
                                if (e.key === "Escape") setQuery("");
                                if (e.key === "Enter") e.currentTarget.blur(); // just closes the keyboard
                            }}
                        />
                        {query && (
                            <button
                                type="button"
                                aria-label="Clear search"
                                className={styles.searchClear}
                                onClick={() => setQuery("")}
                            >
                                <svg
                                    width="18"
                                    height="18"
                                    viewBox="0 0 24 24"
                                    fill="none"
                                    stroke="currentColor"
                                    strokeWidth="2.2"
                                    strokeLinecap="round"
                                    aria-hidden="true"
                                >
                                    <path d="M6 6l12 12M18 6L6 18" />
                                </svg>
                            </button>
                        )}
                    </div>
                )}
                <div className={styles.disclaimer}>
                    Dates are estimates: the day it was bought plus how long that kind of food usually keeps.
                </div>
                <ScanFeedView
                    feed={scanFeed.feed}
                    now={scanFeed.now}
                    onDismiss={(t) => void scanFeed.dismiss(t)}
                />
            </header>

            <div className={styles.content} aria-busy={refreshing}>
                {refreshing && phase === "ready" && (
                    <div className={styles.refreshing} role="status">
                        Refreshing…
                    </div>
                )}
                {offline && (
                    <p className={styles.notice} role="status">
                        {stock
                            ? "You're offline. This is the last list loaded."
                            : "You're offline. Connect to see your stock."}
                    </p>
                )}

                {phase === "noAccess" && (
                    <p className={styles.notice} role="alert">
                        You're signed in, but this account can't use Cupboard Tracker.
                    </p>
                )}

                {phase === "loading" && !offline && <Skeleton />}

                {phase === "error" && !stock && (
                    <div className={styles.errorBox} role="alert">
                        <p className={styles.errorText}>Couldn't load your stock.</p>
                        <button type="button" className={styles.retry} onClick={() => void load()}>
                            Retry
                        </button>
                        <button
                            type="button"
                            className={styles.retry}
                            onClick={() => void api.signInAgain().catch(() => undefined)}
                        >
                            Sign in again
                        </button>
                        {loadError && <p className={styles.errorCode}>{loadError}</p>}
                    </div>
                )}

                {stock && stock.products.length === 0 && (
                    <p className={styles.empty}>
                        Nothing in stock yet.
                        <br />
                        Scan something in.
                    </p>
                )}

                {stock && stock.products.length > 0 && (
                    <div className={styles.chips}>
                        <button
                            type="button"
                            className={styles.chip}
                            aria-pressed={useFirstOnly}
                            onClick={() => setUseFirstOnly((on) => !on)}
                        >
                            {`Use first · ${stock.useFirstCount}`}
                        </button>
                    </div>
                )}

                {searching &&
                stock &&
                stock.products.length > 0 &&
                groups.length === 0 &&
                waiting.length === 0 ? (
                    <p className={styles.noMatches}>
                        Nothing matches that.
                        <br />
                        Try another word or filter.
                    </p>
                ) : (
                    useFirstOnly &&
                    groups.length === 0 && <p className={styles.empty}>Nothing needs using up right now.</p>
                )}

                {groups.map((group) => (
                    <AisleSection
                        key={group.aisle.id}
                        group={group}
                        // Open unless closed on this device (Gary, 2026-10-18); a filter or search opens every aisle it shows,
                        // without changing the remembered state (#91).
                        open={useFirstOnly || searching || (openState[group.aisle.id] ?? true)}
                        canCollapse={!useFirstOnly && !searching}
                        onToggle={() => toggleAisle(group)}
                    >
                        {group.products.map(renderKnown)}
                    </AisleSection>
                ))}

                {waiting.length > 0 && (
                    <Section title="Waiting for a name" count={waiting.length}>
                        {waiting.map((p) => (
                            <UnnamedRow key={p.productId} product={p} />
                        ))}
                    </Section>
                )}
            </div>

            {toast && (
                <div className={styles.toast} role="status">
                    <span>
                        {toast.kind === "checkedOut"
                            ? `Checked out ${toast.name}`
                            : toast.kind === "opened"
                              ? `Marked ${toast.name} as opened`
                              : toast.text}
                    </span>
                    {toast.kind === "checkedOut" && (
                        <button type="button" className={styles.undo} onClick={() => void undo(toast)}>
                            Undo
                        </button>
                    )}
                    {toast.kind === "opened" && (
                        <button type="button" className={styles.undo} onClick={() => void undoOpened(toast)}>
                            Undo
                        </button>
                    )}
                </div>
            )}
        </div>
    );
}

/** A collapsible aisle (#71): a full-width header button with the name, the count, an amber (or red) "n to use first"
 * pill and a chevron. */
function AisleSection({
    group,
    open,
    canCollapse,
    onToggle,
    children,
}: {
    group: AisleGroup;
    open: boolean;
    canCollapse: boolean;
    onToggle: () => void;
    children: React.ReactNode;
}) {
    const listId = `aisle-${group.aisle.id}`;
    return (
        <section className={styles.section} aria-label={group.aisle.name}>
            <button
                type="button"
                className={styles.aisleHeader}
                aria-expanded={open}
                aria-controls={listId}
                disabled={!canCollapse}
                onClick={onToggle}
            >
                <span className={styles.aisleName}>{group.aisle.name}</span>
                {group.useFirstCount > 0 && (
                    <span
                        className={`${styles.aislePill} ${group.anyExpired ? styles.aislePillExpired : ""}`}
                    >
                        {`${group.useFirstCount} to use first`}
                    </span>
                )}
                <span className={styles.sectionCount}>{itemsLabel(group.products.length)}</span>
                <svg
                    className={styles.chevron}
                    width="18"
                    height="18"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2.2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    aria-hidden="true"
                >
                    <path d="M6 9l6 6 6-6" />
                </svg>
            </button>
            {open && (
                <ul id={listId} className={styles.list}>
                    {children}
                </ul>
            )}
        </section>
    );
}

function Section({ title, count, children }: { title: string; count: number; children: React.ReactNode }) {
    const id = `section-${title.replace(/\s+/g, "-").toLowerCase()}`;
    return (
        <section className={styles.section} aria-labelledby={id}>
            <h2 id={id} className={styles.sectionTitle}>
                <span>{title}</span>
                <span className={styles.sectionCount}>{itemsLabel(count)}</span>
            </h2>
            <ul className={styles.list}>{children}</ul>
        </section>
    );
}

function KnownRow({
    product,
    busy,
    onCheckOut,
    onOpen,
}: {
    product: StockProduct;
    busy: boolean;
    onCheckOut: () => void;
    onOpen: () => void;
}) {
    // Greyed out once one's open (finish it first), or when they're all in the freezer (#65).
    const alreadyOpen = product.openedCount > 0;
    const canOpen = !alreadyOpen && product.frozenCount < product.count;
    const { title, meta } = stockRowText(product);
    const badge =
        product.daysUntilSoonest !== null && product.soonestEstimatedExpiryDate !== null
            ? expiryBadge(product.daysUntilSoonest, product.soonestEstimatedExpiryDate)
            : null;
    // An estimate from opening, freezing or defrosting says so (#65): working back to a bought date would be wrong.
    const note =
        product.soonestState && product.soonestStateOn
            ? stateNote(product.soonestState, product.soonestStateOn)
            : product.soonestEstimatedExpiryDate !== null && product.shelfLifeDays !== null
              ? boughtNote(product.soonestEstimatedExpiryDate, product.shelfLifeDays)
              : null;
    return (
        <li className={styles.row}>
            <div className={styles.rowMain}>
                <ProductTile
                    className={styles.tile}
                    name={title}
                    icon={product.icon}
                    aisleId={product.aisleId}
                />
                <div className={styles.text}>
                    <a
                        className={`${styles.name} ${styles.nameLink}`}
                        href={itemPath(product.productId)}
                        onClick={(e) => {
                            e.preventDefault();
                            rememberItemPreview(product); // item detail opens with what the row knows (#100)
                            navigate(itemPath(product.productId));
                        }}
                    >
                        {title}
                    </a>
                    {meta && <span className={styles.meta}>{meta}</span>}
                    {(product.openedCount > 0 || product.frozenCount > 0) && (
                        <span className={styles.pills}>
                            {product.openedCount > 0 && (
                                <span className={`${styles.pill} ${styles.opened}`}>
                                    {product.openedCount === 1 ? "Opened" : `${product.openedCount} opened`}
                                </span>
                            )}
                            {product.frozenCount > 0 && (
                                <span
                                    className={`${styles.pill} ${styles.frozen}`}
                                >{`${product.frozenCount} frozen`}</span>
                            )}
                        </span>
                    )}
                </div>
                {badge && <span className={`${styles.badge} ${styles[badge.tone]}`}>{badge.text}</span>}
            </div>
            <div className={styles.actions}>
                {note && <span className={styles.note}>{note}</span>}
                <div className={styles.rowButtons}>
                    <button
                        type="button"
                        className={styles.checkOut}
                        onClick={onOpen}
                        disabled={busy || !canOpen}
                        aria-label={
                            alreadyOpen
                                ? `One ${product.displayName} is already open`
                                : `Mark one ${product.displayName} as opened`
                        }
                    >
                        <svg
                            width="18"
                            height="18"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2.2"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                            aria-hidden="true"
                        >
                            <path d="M7 9h10v10a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2z" />
                            <path d="M5.5 6.5L17 3.5" />
                        </svg>
                        {/* "Open" while it can be done; "Opened" once one is (Gary, 2026-10-18). */}
                        {alreadyOpen ? "Opened" : "Open"}
                    </button>
                    <button
                        type="button"
                        className={styles.checkOut}
                        onClick={onCheckOut}
                        disabled={busy}
                        aria-label={`Remove one ${product.displayName}`}
                    >
                        <svg
                            width="18"
                            height="18"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2.2"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                            aria-hidden="true"
                        >
                            <path d="M4 7h16" />
                            <path d="M10 11v6M14 11v6" />
                            <path d="M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12" />
                            <path d="M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2" />
                        </svg>
                        Remove
                    </button>
                </div>
            </div>
        </li>
    );
}

function UnnamedRow({ product }: { product: StockProduct }) {
    const meta = [
        product.barcode ? `Barcode ${groupBarcode(product.barcode)}` : null,
        product.count > 1 ? `×${product.count}` : null,
    ]
        .filter(Boolean)
        .join(" · ");
    return (
        <li className={styles.row}>
            <div className={styles.rowMain}>
                <div className={`${styles.tile} ${styles.tilePending}`} aria-hidden="true">
                    ?
                </div>
                <div className={styles.text}>
                    <a
                        className={`${styles.name} ${styles.nameLink}`}
                        href={namePath(product.productId)}
                        onClick={(e) => {
                            e.preventDefault();
                            navigate(namePath(product.productId));
                        }}
                    >
                        Unknown item
                    </a>
                    {meta && <span className={styles.meta}>{meta}</span>}
                </div>
                <span className={styles.badge}>No name yet</span>
            </div>
            <div className={styles.actions}>
                <span className={styles.note}>{scannedLabel(product.latestCheckedInAt)}</span>
                <button
                    type="button"
                    className={styles.nameIt}
                    onClick={() => navigate(namePath(product.productId))}
                >
                    Name it
                </button>
            </div>
        </li>
    );
}

function Skeleton() {
    return (
        <ul className={styles.list} role="status" aria-label="Loading your stock">
            {[0, 1, 2].map((i) => (
                <li key={i} className={`${styles.row} ${styles.skeleton}`}>
                    <div className={styles.rowMain}>
                        <div className={`${styles.tile} ${styles.skeletonBlock}`} />
                        <div className={styles.text}>
                            <span className={`${styles.skeletonBlock} ${styles.skeletonLine}`} />
                            <span className={`${styles.skeletonBlock} ${styles.skeletonLineShort}`} />
                        </div>
                    </div>
                </li>
            ))}
        </ul>
    );
}
