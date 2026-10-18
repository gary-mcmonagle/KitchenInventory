import { useCallback, useEffect, useRef, useState } from "react";
import {
    ApiError,
    NoAccessError,
    SignInRequiredError,
    type ApiClient,
    type Product,
    type ProductUnit,
} from "./api";
import {
    boughtDate,
    groupBarcode,
    initial,
    keepsAbout,
    longDate,
    metaLine,
    shortDate,
    untilSentence,
    type ExpiryTone,
} from "./format";
import { editPath, navigate } from "./router";
import { TOAST_MS } from "./StockScreen";
import styles from "./Item.module.css";

/** The shelf lives offered under "Change shelf life", in days (#53). */
export const SHELF_LIFE_CHOICES: Array<{ days: number; label: string }> = [
    { days: 3, label: "3 days" },
    { days: 5, label: "5 days" },
    { days: 7, label: "1 week" },
    { days: 14, label: "2 weeks" },
    { days: 30, label: "1 month" },
    { days: 90, label: "3 months" },
    { days: 180, label: "6 months" },
    { days: 365, label: "1 year" },
    { days: 730, label: "2 years" },
];

type Toast = { kind: "checkedOut"; unitId: string } | { kind: "info"; text: string };

const tone = (days: number): ExpiryTone => (days < 0 ? "expired" : days <= 3 ? "soon" : "later");

/** Item detail (#53, project/Item.dc.html): a known product's units, fixing an estimate, and editing its details. */
export function ItemScreen({ api, productId }: { api: ApiClient; productId: string }) {
    const [product, setProduct] = useState<Product | null>(null);
    const [loadError, setLoadError] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);
    const [toast, setToast] = useState<Toast | null>(null);
    const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    const live = useRef(true);

    const showToast = useCallback((next: Toast) => {
        clearTimeout(toastTimer.current);
        setToast(next);
        toastTimer.current = setTimeout(() => setToast(null), TOAST_MS);
    }, []);

    const load = useCallback(async () => {
        try {
            const next = await api.product(productId);
            if (!live.current) return;
            if (next.status !== "known") {
                navigate("/"); // unknown items are named from the stock list
                return;
            }
            setProduct(next);
        } catch (error) {
            if (!live.current || error instanceof SignInRequiredError) return;
            setLoadError(
                error instanceof NoAccessError
                    ? "You're signed in, but this account can't use Cupboard Tracker."
                    : error instanceof ApiError && error.status === 404
                      ? "We couldn't find that product."
                      : "Couldn't load this product. Try again.",
            );
        }
    }, [api, productId]);

    useEffect(() => {
        live.current = true;
        void load();
        return () => {
            live.current = false;
            clearTimeout(toastTimer.current);
        };
    }, [load]);

    const back = () => navigate("/");

    const checkOut = async (unit: ProductUnit) => {
        if (busy) return;
        setBusy(true);
        try {
            const result = await api.checkOutUnit(unit.unitId);
            if (result.remainingCount === 0) {
                navigate("/"); // the last one: back to the stock list
                return;
            }
            showToast({ kind: "checkedOut", unitId: result.unitId });
            await load();
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            showToast({
                kind: "info",
                text:
                    error instanceof ApiError && error.status === 409
                        ? "That one's already been checked out."
                        : "Couldn't check that out. Try again.",
            });
            await load();
        } finally {
            if (live.current) setBusy(false);
        }
    };

    const undo = async (unitId: string) => {
        clearTimeout(toastTimer.current);
        setToast(null);
        try {
            await api.undoCheckOut(unitId);
            await load();
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            showToast({ kind: "info", text: "Couldn't undo that. Try again." });
        }
    };

    return (
        <div className={styles.screen}>
            <div className={styles.header}>
                <button type="button" className={styles.back} aria-label="Back to stock" onClick={back}>
                    <svg
                        width="24"
                        height="24"
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        strokeWidth="2.2"
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        aria-hidden="true"
                    >
                        <path d="M15 5l-7 7 7 7" />
                    </svg>
                </button>
                <span className={styles.headerLabel}>Stock</span>
            </div>

            <div className={styles.content}>
                {loadError && (
                    <p className={styles.notice} role="alert">
                        {loadError}
                    </p>
                )}
                {!product && !loadError && <p className={styles.muted}>Loading…</p>}
                {product && (
                    <Detail
                        api={api}
                        product={product}
                        busy={busy}
                        onCheckOut={(u) => void checkOut(u)}
                        onSaved={(text) => {
                            showToast({ kind: "info", text });
                            void load();
                        }}
                    />
                )}
            </div>

            {toast && (
                <div className={styles.toast} role="status">
                    <span>
                        {toast.kind === "checkedOut"
                            ? `Checked out ${product?.displayName ?? ""}`
                            : toast.text}
                    </span>
                    {toast.kind === "checkedOut" && (
                        <button type="button" className={styles.undo} onClick={() => void undo(toast.unitId)}>
                            Undo
                        </button>
                    )}
                </div>
            )}
        </div>
    );
}

function Detail({
    api,
    product,
    busy,
    onCheckOut,
    onSaved,
}: {
    api: ApiClient;
    product: Product;
    busy: boolean;
    onCheckOut: (unit: ProductUnit) => void;
    onSaved: (message: string) => void;
}) {
    const meta = metaLine(product.brand, product.size, 1);
    const soonest = product.units[0];
    return (
        <>
            <div className={styles.heading}>
                <div className={styles.tile} aria-hidden="true">
                    {initial(product.displayName)}
                </div>
                <div className={styles.headingText}>
                    <h1 className={styles.name}>{product.displayName}</h1>
                    {meta && <div className={styles.meta}>{meta}</div>}
                    <a
                        className={styles.link}
                        href={editPath(product.productId)}
                        onClick={(e) => {
                            e.preventDefault();
                            navigate(editPath(product.productId));
                        }}
                    >
                        Edit details
                    </a>
                </div>
            </div>

            <UseByCard product={product} unit={soonest} />

            {product.units.length > 0 && (
                <section className={styles.card} aria-labelledby="units-title">
                    <h2 id="units-title" className={styles.cardTitle}>
                        In the cupboard
                    </h2>
                    <ul className={styles.units}>
                        {product.units.map((unit, i) => (
                            <li key={unit.unitId} className={styles.unit}>
                                <div className={styles.unitText}>
                                    <span className={styles.unitName}>Unit {i + 1}</span>
                                    <span className={styles.unitMeta}>{unitLine(unit)}</span>
                                </div>
                                <button
                                    type="button"
                                    className={styles.checkOut}
                                    disabled={busy}
                                    onClick={() => onCheckOut(unit)}
                                    aria-label={`Check out unit ${i + 1}`}
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
                            </li>
                        ))}
                    </ul>
                </section>
            )}

            {product.units.length > 0 && (
                <FixEstimate
                    key={product.units.map((u) => u.unitId).join()}
                    api={api}
                    product={product}
                    onSaved={onSaved}
                />
            )}

            {product.barcode && <div className={styles.muted}>Barcode {groupBarcode(product.barcode)}</div>}
        </>
    );
}

/** "Bought 13 Oct · est. 18 Oct", marking dates set by hand. */
export function unitLine(unit: ProductUnit): string {
    const bought = `Bought ${boughtDate(unit.checkedInAt)}`;
    if (!unit.estimatedExpiryDate) return `${bought} · no estimate yet`;
    const est = `est. ${shortDate(unit.estimatedExpiryDate)}`;
    return `${bought} · ${est}${unit.expirySource === "manual" ? " (set by you)" : ""}`;
}

function UseByCard({ product, unit }: { product: Product; unit: ProductUnit | undefined }) {
    if (!unit?.estimatedExpiryDate || unit.daysUntilExpiry === null) {
        return (
            <div className={`${styles.useBy} ${styles.neutral}`}>
                <div className={styles.useByLabel}>Estimated use by</div>
                <div className={styles.useByDate}>No estimate yet</div>
            </div>
        );
    }
    const keeps =
        product.shelfLifeDays !== null
            ? ` Bought ${boughtDate(unit.checkedInAt)}; ${product.displayName} usually keeps about ${keepsAbout(product.shelfLifeDays)}.`
            : ` Bought ${boughtDate(unit.checkedInAt)}.`;
    return (
        <div className={`${styles.useBy} ${styles[tone(unit.daysUntilExpiry)]}`}>
            <div className={styles.useByLabel}>Estimated use by</div>
            <div className={styles.useByDate}>{longDate(unit.estimatedExpiryDate)}</div>
            <div className={styles.useByText}>
                {untilSentence(unit.daysUntilExpiry)}
                {keeps}
            </div>
        </div>
    );
}

function FixEstimate({
    api,
    product,
    onSaved,
}: {
    api: ApiClient;
    product: Product;
    onSaved: (message: string) => void;
}) {
    const [unitId, setUnitId] = useState(product.units[0].unitId);
    const unitIndex = Math.max(
        0,
        product.units.findIndex((u) => u.unitId === unitId),
    );
    const unit = product.units[unitIndex];
    const [date, setDate] = useState(unit.estimatedExpiryDate ?? "");
    const [shelfOpen, setShelfOpen] = useState(false);
    const current = product.shelfLifeDays;
    const choices =
        current !== null && !SHELF_LIFE_CHOICES.some((c) => c.days === current)
            ? [...SHELF_LIFE_CHOICES, { days: current, label: keepsAbout(current) }].sort(
                  (a, b) => a.days - b.days,
              )
            : SHELF_LIFE_CHOICES;
    const [days, setDays] = useState(current ?? 7);
    const [error, setError] = useState<string | null>(null);
    const [saving, setSaving] = useState(false);

    const pickUnit = (id: string) => {
        setUnitId(id);
        setDate(product.units.find((u) => u.unitId === id)?.estimatedExpiryDate ?? "");
    };

    const run = async (action: () => Promise<unknown>, message: string) => {
        if (saving) return;
        setSaving(true);
        setError(null);
        try {
            await action();
            onSaved(message);
        } catch (e) {
            if (e instanceof SignInRequiredError) return;
            setError(
                e instanceof ApiError && e.status === 400
                    ? "That date doesn't look right."
                    : "Couldn't save. Try again.",
            );
        } finally {
            setSaving(false);
        }
    };

    const saveDate = () => {
        if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) {
            setError("Pick a date.");
            return;
        }
        void run(() => api.setUnitExpiry(unit.unitId, date), `Saved the date for unit ${unitIndex + 1}.`);
    };

    const saveShelfLife = () =>
        void run(async () => {
            await api.setShelfLife(product.productId, days);
            setShelfOpen(false);
        }, "Saved. It applies to ones you buy from now on.");

    return (
        <section className={styles.card} aria-labelledby="fix-title">
            <h2 id="fix-title" className={styles.cardTitle}>
                Estimate looks wrong?
            </h2>
            {product.units.length > 1 && (
                <div className={styles.field}>
                    <label htmlFor="fix-unit" className={styles.label}>
                        Unit
                    </label>
                    <select
                        id="fix-unit"
                        className={styles.input}
                        value={unitId}
                        onChange={(e) => pickUnit(e.target.value)}
                    >
                        {product.units.map((u, i) => (
                            <option key={u.unitId} value={u.unitId}>
                                Unit {i + 1}
                            </option>
                        ))}
                    </select>
                </div>
            )}
            <div className={styles.field}>
                <label htmlFor="fix-date" className={styles.label}>
                    Use by date for unit {unitIndex + 1}
                </label>
                <input
                    id="fix-date"
                    type="date"
                    className={styles.input}
                    value={date}
                    onChange={(e) => setDate(e.target.value)}
                />
            </div>
            <div className={styles.buttons}>
                <button type="button" className={styles.primary} onClick={saveDate} disabled={saving}>
                    Save date
                </button>
                <button
                    type="button"
                    className={styles.secondary}
                    onClick={() => setShelfOpen((open) => !open)}
                    aria-expanded={shelfOpen}
                >
                    Change shelf life
                </button>
            </div>
            {shelfOpen && (
                <div className={styles.shelf}>
                    <div className={styles.field}>
                        <label htmlFor="fix-shelf" className={styles.label}>
                            {product.displayName} usually keeps
                        </label>
                        <select
                            id="fix-shelf"
                            className={styles.input}
                            value={days}
                            onChange={(e) => setDays(Number(e.target.value))}
                        >
                            {choices.map((c) => (
                                <option key={c.days} value={c.days}>
                                    {c.label}
                                </option>
                            ))}
                        </select>
                    </div>
                    <button
                        type="button"
                        className={styles.primary}
                        onClick={saveShelfLife}
                        disabled={saving}
                    >
                        Save shelf life
                    </button>
                </div>
            )}
            {error && (
                <p className={styles.error} role="alert">
                    {error}
                </p>
            )}
            <div className={styles.help}>
                Saving a date changes this unit only. Changing the shelf life applies to {product.displayName}{" "}
                you buy from now on.
            </div>
        </section>
    );
}
