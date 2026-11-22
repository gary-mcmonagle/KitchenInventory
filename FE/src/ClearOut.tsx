import { useEffect, useId, useMemo, useRef, useState } from "react";
import { SignInRequiredError, type ApiClient, type Stock, type StockProduct } from "./api";
import { AISLES } from "./aisles";
import { clearOutBadge, stockRowText } from "./format";
import styles from "./ClearOut.module.css";

/** How long the Undo toast stays (the mockup: about 9 seconds). */
export const CLEAR_OUT_TOAST_MS = 9000;

type Filter = "all" | "expired" | "soon" | string; // or an aisle id

const aisleIdOf = (p: StockProduct) => (AISLES.some((a) => a.id === p.aisleId) ? p.aisleId : "other");

/** Soonest estimate first (none last), then name: what you most likely want gone is at the top. */
function bySoonest(a: StockProduct, b: StockProduct): number {
    if (a.soonestEstimatedExpiryDate !== b.soonestEstimatedExpiryDate) {
        if (a.soonestEstimatedExpiryDate === null) return 1;
        if (b.soonestEstimatedExpiryDate === null) return -1;
        return a.soonestEstimatedExpiryDate.localeCompare(b.soonestEstimatedExpiryDate);
    }
    return a.displayName.localeCompare(b.displayName, "en", { sensitivity: "base" });
}

const isExpired = (p: StockProduct) => p.daysUntilSoonest !== null && p.daysUntilSoonest < 0;
const isSoon = (p: StockProduct) =>
    p.daysUntilSoonest !== null && p.daysUntilSoonest >= 0 && p.daysUntilSoonest <= 2;

const matches = (p: StockProduct, filter: Filter) =>
    filter === "all" ||
    (filter === "expired" ? isExpired(p) : filter === "soon" ? isSoon(p) : aisleIdOf(p) === filter);

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

interface Done {
    /** The units this request checked out: what Undo puts back. */
    unitIds: string[];
    text: string;
}

/**
 * Clear out (#132): tick what's gone and check it all out in one go. A mode over the stock list: one tap picks every
 * unit of a product, a stepper picks fewer (soonest-expiring first), nothing changes until the sheet is confirmed, and
 * Undo puts the whole batch back. It follows the live `stock`, so a product that goes meanwhile drops out of the
 * selection quietly.
 */
export function ClearOut({
    api,
    stock,
    onClose,
    onChanged,
}: {
    api: ApiClient;
    stock: Stock;
    onClose: () => void;
    /** Stock changed (checked out or undone): the list reads it again. */
    onChanged: () => void;
}) {
    const [filter, setFilter] = useState<Filter>("all");
    const [picked, setPicked] = useState<Record<string, number>>({});
    const [confirming, setConfirming] = useState(false);
    const [busy, setBusy] = useState(false);
    const [done, setDone] = useState<Done | null>(null);
    const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    const titleId = useId();
    useEffect(() => () => clearTimeout(toastTimer.current), []);

    const products = useMemo(
        () => stock.products.filter((p) => p.section !== "waitingForName" && p.count > 0).sort(bySoonest),
        [stock],
    );

    const chips = useMemo(() => {
        const list: Array<{ id: Filter; label: string; count: number }> = [
            { id: "all", label: "All", count: products.length },
            { id: "expired", label: "Expired", count: products.filter(isExpired).length },
            { id: "soon", label: "Use soon", count: products.filter(isSoon).length },
        ];
        for (const aisle of AISLES) {
            list.push({
                id: aisle.id,
                label: aisle.name,
                count: products.filter((p) => aisleIdOf(p) === aisle.id).length,
            });
        }
        return list.filter((c) => c.count > 0 || c.id === "all");
    }, [products]);

    // A filter that has run empty (everything in it went) falls back to All.
    const active: Filter = chips.some((c) => c.id === filter) ? filter : "all";
    const shown = products.filter((p) => matches(p, active));

    // What's selected, now: products that are gone drop out, and a count never goes above what's there.
    const selection = products
        .filter((p) => (picked[p.productId] ?? 0) > 0)
        .map((p) => ({ product: p, count: Math.min(picked[p.productId], p.count) }));
    const total = selection.reduce((n, s) => n + s.count, 0);
    const allShownPicked = shown.length > 0 && shown.every((p) => picked[p.productId] >= p.count);

    const setCount = (p: StockProduct, count: number) =>
        setPicked((current) => {
            const next = { ...current };
            if (count <= 0) delete next[p.productId];
            else next[p.productId] = Math.min(count, p.count);
            return next;
        });

    const toggleShown = () =>
        setPicked((current) => {
            const next = { ...current };
            for (const p of shown) {
                if (allShownPicked) delete next[p.productId];
                else next[p.productId] = p.count;
            }
            return next;
        });

    const showDone = (next: Done) => {
        clearTimeout(toastTimer.current);
        setDone(next);
        toastTimer.current = setTimeout(() => setDone(null), CLEAR_OUT_TOAST_MS);
    };

    /** The unit ids to take: for each product, the first n of its units in check-out order (soonest expiring first). */
    const unitsToTake = async (): Promise<{ ids: string[]; missed: number }> => {
        const ids: string[] = [];
        let missed = 0;
        const found = await Promise.allSettled(selection.map((s) => api.product(s.product.productId)));
        found.forEach((result, i) => {
            const { count } = selection[i];
            if (result.status === "rejected") {
                if (result.reason instanceof SignInRequiredError) throw result.reason;
                missed += count;
                return;
            }
            const units = result.value.units.slice(0, count);
            ids.push(...units.map((u) => u.unitId));
            missed += count - units.length;
        });
        return { ids, missed };
    };

    const confirm = async () => {
        if (busy || total === 0) return;
        setBusy(true);
        try {
            const { ids, missed } = await unitsToTake();
            const result = ids.length > 0 ? await api.checkOutUnits(ids) : null;
            const unitIds = (result?.units ?? [])
                .filter((u) => u.outcome === "checkedOut")
                .map((u) => u.unitId);
            const failed = (result?.failed ?? 0) + missed;
            setConfirming(false);
            setPicked({});
            showDone({
                unitIds,
                text:
                    unitIds.length > 0
                        ? `${plural(unitIds.length, "item")} checked out${failed > 0 ? `, ${failed} couldn't be` : ""}`
                        : failed > 0
                          ? `Couldn't check ${failed === 1 ? "that" : "those"} out. Try again.`
                          : "Nothing to check out: already gone.",
            });
            onChanged();
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            setConfirming(false);
            showDone({ unitIds: [], text: "Couldn't check those out. Try again." });
            onChanged();
        } finally {
            setBusy(false);
        }
    };

    const undo = async () => {
        if (!done || done.unitIds.length === 0) return;
        const { unitIds } = done;
        clearTimeout(toastTimer.current);
        setDone(null);
        const results = await Promise.allSettled(unitIds.map((id) => api.undoCheckOut(id)));
        const left = results.filter((r) => r.status === "rejected").length;
        if (left > 0) showDone({ unitIds: [], text: `Couldn't put ${left} back. Try again.` });
        onChanged();
    };

    return (
        <div className={styles.screen}>
            <header className={styles.top}>
                <div>
                    <h1 className={styles.title}>Clear out</h1>
                    <p className={styles.lead}>Tick what's gone, then check it all out in one go.</p>
                </div>
                <button type="button" className={styles.done} onClick={onClose}>
                    Done
                </button>
            </header>

            <div className={styles.chips} role="group" aria-label="Show">
                {chips.map((c) => (
                    <button
                        key={c.id}
                        type="button"
                        className={styles.chip}
                        aria-pressed={active === c.id}
                        onClick={() => setFilter(c.id)}
                    >
                        {c.label} <span className={styles.chipCount}>{c.count}</span>
                    </button>
                ))}
            </div>

            <div className={styles.bar}>
                <span className={styles.shown}>{`${plural(shown.length, "product")} shown`}</span>
                {shown.length > 0 && (
                    <button type="button" className={styles.link} onClick={toggleShown}>
                        {allShownPicked ? "Clear selection" : "Select all shown"}
                    </button>
                )}
            </div>

            {shown.length === 0 ? (
                <div className={styles.empty}>
                    <b>{products.length === 0 ? "The cupboard is clear." : "Nothing here"}</b>
                    {products.length === 0 ? "" : "No items match this filter."}
                </div>
            ) : (
                <ul className={styles.list}>
                    {shown.map((p) => {
                        const count = Math.min(picked[p.productId] ?? 0, p.count);
                        const badge = clearOutBadge(p.daysUntilSoonest);
                        const { title, meta } = stockRowText({ ...p, count: 1 });
                        return (
                            <li
                                key={p.productId}
                                className={`${styles.row} ${count > 0 ? styles.rowSelected : ""}`}
                            >
                                <button
                                    type="button"
                                    className={styles.pick}
                                    aria-pressed={count > 0}
                                    onClick={() => setCount(p, count > 0 ? 0 : p.count)}
                                >
                                    <span className={styles.box} aria-hidden="true">
                                        <i className={styles.tick}>
                                            <svg viewBox="0 0 16 16">
                                                <path d="M3 8.5l3.2 3L13 4.5" />
                                            </svg>
                                        </i>
                                    </span>
                                    <span className={styles.text}>
                                        <span className={styles.name}>{title}</span>
                                        <span className={styles.meta}>
                                            {[meta, `${p.count} in stock`].filter(Boolean).join(" · ")}
                                        </span>
                                    </span>
                                </button>
                                {p.count > 1 && count > 0 ? (
                                    <div className={styles.step}>
                                        <button
                                            type="button"
                                            aria-label={`One fewer ${title}`}
                                            disabled={count <= 1}
                                            onClick={() => setCount(p, count - 1)}
                                        >
                                            −
                                        </button>
                                        <span>{`${count} of ${p.count}`}</span>
                                        <button
                                            type="button"
                                            aria-label={`One more ${title}`}
                                            disabled={count >= p.count}
                                            onClick={() => setCount(p, count + 1)}
                                        >
                                            +
                                        </button>
                                    </div>
                                ) : (
                                    badge && (
                                        <span
                                            className={`${styles.badge} ${
                                                badge.tone === "expired"
                                                    ? styles.badgeExpired
                                                    : styles.badgeSoon
                                            }`}
                                        >
                                            {badge.text}
                                        </span>
                                    )
                                )}
                            </li>
                        );
                    })}
                </ul>
            )}

            <div className={styles.action}>
                <button
                    type="button"
                    className={styles.primary}
                    disabled={total === 0}
                    onClick={() => setConfirming(true)}
                >
                    {total === 0 ? "Select items to check out" : `Check out ${plural(total, "item")}`}
                </button>
            </div>

            {confirming && total > 0 && (
                <div className={styles.overlay}>
                    <div
                        className={styles.scrim}
                        aria-hidden="true"
                        onClick={() => !busy && setConfirming(false)}
                    />
                    <div className={styles.sheet} role="dialog" aria-modal="true" aria-labelledby={titleId}>
                        <h2
                            id={titleId}
                            className={styles.sheetTitle}
                        >{`Check out ${plural(total, "item")}`}</h2>
                        <p className={styles.sheetSub}>
                            These leave your stock now. Undo is available afterwards.
                        </p>
                        <ul className={styles.summary}>
                            {selection.map(({ product, count }) => (
                                <li key={product.productId}>
                                    <span>{stockRowText({ ...product, count: 1 }).title}</span>
                                    <span>{`×${count}`}</span>
                                </li>
                            ))}
                        </ul>
                        <button
                            type="button"
                            className={styles.primary}
                            disabled={busy}
                            onClick={() => void confirm()}
                        >
                            {busy ? "Checking out…" : `Check out ${plural(total, "item")}`}
                        </button>
                        <button
                            type="button"
                            className={styles.cancel}
                            disabled={busy}
                            onClick={() => setConfirming(false)}
                        >
                            Cancel
                        </button>
                    </div>
                </div>
            )}

            {done && (
                <div className={styles.toast} role="status">
                    <span>{done.text}</span>
                    {done.unitIds.length > 0 && (
                        <button type="button" onClick={() => void undo()}>
                            Undo
                        </button>
                    )}
                </div>
            )}
        </div>
    );
}
