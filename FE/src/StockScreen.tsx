import { useCallback, useEffect, useRef, useState, type TouchEvent } from "react";
import { NoAccessError, SignInRequiredError, type ApiClient, type Stock, type StockProduct } from "./api";
import { groupBarcode, initial, itemsLabel, metaLine, scannedLabel, todayLine } from "./format";
import styles from "./Stock.module.css";

const PULL_TO_REFRESH = 70; // px

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
                            <KnownRow key={p.productId} product={p} />
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

function KnownRow({ product }: { product: StockProduct }) {
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
            <div className={styles.note}>{scannedLabel(product.latestCheckedInAt)}</div>
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
