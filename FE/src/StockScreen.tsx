import { useCallback, useEffect, useRef, useState, type TouchEvent } from "react";
import {
    ApiError,
    NoAccessError,
    SignInRequiredError,
    type ApiClient,
    type Stock,
    type StockProduct,
} from "./api";
import { groupBarcode, initial, itemsLabel, metaLine, scannedLabel, todayLine } from "./format";
import { namePath, navigate } from "./router";
import styles from "./Stock.module.css";

const PULL_TO_REFRESH = 70; // px
export const TOAST_MS = 6000; // as in the mockup

type Toast =
    | { kind: "checkedOut"; unitId: string; product: StockProduct; name: string }
    | { kind: "error"; text: string };

/** The stock with one product's count set (the row is removed at 0, put back if missing), and the total recomputed. */
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
    return { ...stock, products, totalUnits: products.reduce((n, p) => n + p.count, 0) };
}

/** Back where the API would put it: named products A–Z ignoring case, before the unnamed ones. */
function insertSorted(products: StockProduct[], product: StockProduct): StockProduct[] {
    const at = products.findIndex(
        (p) =>
            p.status !== "known" ||
            p.displayName.localeCompare(product.displayName, "en", { sensitivity: "base" }) > 0,
    );
    return at < 0 ? [...products, product] : [...products.slice(0, at), product, ...products.slice(at)];
}

type Phase = "loading" | "ready" | "error" | "noAccess";

/** The stock list (#49): what's in the house now. Reloads on open, on returning to the app, and on pull-to-refresh. */
export function StockScreen({ api }: { api: ApiClient }) {
    const [stock, setStock] = useState<Stock | null>(null);
    const [phase, setPhase] = useState<Phase>("loading");
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

    const undo = async (done: Extract<Toast, { kind: "checkedOut" }>) => {
        clearTimeout(toastTimer.current);
        setToast(null);
        const before = stock?.products.find((p) => p.productId === done.product.productId)?.count ?? 0;
        setStock((s) => s && withCount(s, done.product, before + 1)); // optimistic
        try {
            const result = await api.undoCheckOut(done.unitId);
            setStock((s) => s && withCount(s, done.product, result.count));
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            setStock((s) => s && withCount(s, done.product, before)); // revert
            showToast({ kind: "error", text: "Couldn't undo that. Try again." });
        }
    };

    const load = useCallback(async () => {
        if (!navigator.onLine) {
            setOffline(true);
            return;
        }
        setRefreshing(true);
        try {
            const next = await api.stock();
            if (!live.current) return;
            setStock(next);
            setPhase("ready");
        } catch (error) {
            if (!live.current || error instanceof SignInRequiredError) return;
            setPhase(error instanceof NoAccessError ? "noAccess" : "error");
        } finally {
            if (live.current) setRefreshing(false);
        }
    }, [api]);

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

    const known = stock?.products.filter((p) => p.status === "known") ?? [];
    const unnamed = stock?.products.filter((p) => p.status !== "known") ?? [];

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
                <div className={styles.date}>{todayLine()}</div>
                <h1 className={styles.title}>Cupboard</h1>
                {stock && <div className={styles.summary}>{itemsLabel(stock.totalUnits)}</div>}
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
                    </div>
                )}

                {stock && stock.products.length === 0 && (
                    <p className={styles.empty}>
                        Nothing in stock yet.
                        <br />
                        Scan something in.
                    </p>
                )}

                {known.length > 0 && (
                    <Section title="In stock" count={known.length}>
                        {known.map((p) => (
                            <KnownRow
                                key={p.productId}
                                product={p}
                                busy={inFlight.has(p.productId)}
                                onCheckOut={() => void checkOut(p)}
                            />
                        ))}
                    </Section>
                )}

                {unnamed.length > 0 && (
                    <Section title="Waiting for a name" count={unnamed.length}>
                        {unnamed.map((p) => (
                            <UnnamedRow key={p.productId} product={p} />
                        ))}
                    </Section>
                )}
            </div>

            {toast && (
                <div className={styles.toast} role="status">
                    <span>{toast.kind === "checkedOut" ? `Checked out ${toast.name}` : toast.text}</span>
                    {toast.kind === "checkedOut" && (
                        <button type="button" className={styles.undo} onClick={() => void undo(toast)}>
                            Undo
                        </button>
                    )}
                </div>
            )}
        </div>
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
}: {
    product: StockProduct;
    busy: boolean;
    onCheckOut: () => void;
}) {
    const meta = metaLine(product.brand, product.size, product.count);
    return (
        <li className={styles.row}>
            <div className={styles.rowMain}>
                <div className={styles.tile} aria-hidden="true">
                    {initial(product.displayName)}
                </div>
                <div className={styles.text}>
                    <span className={styles.name}>{product.displayName}</span>
                    {meta && <span className={styles.meta}>{meta}</span>}
                </div>
            </div>
            <div className={styles.actions}>
                <button
                    type="button"
                    className={styles.checkOut}
                    onClick={onCheckOut}
                    disabled={busy}
                    aria-label={`Check out one ${product.displayName}`}
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
                        <path d="M5 12.5l4.5 4.5L19 7.5" />
                    </svg>
                    Check out
                </button>
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
                    <span className={styles.name}>Unknown item</span>
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
