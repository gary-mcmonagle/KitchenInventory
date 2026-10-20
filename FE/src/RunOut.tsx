import { useEffect, useRef, useState } from "react";
import {
    SignInRequiredError,
    type ApiClient,
    type RunOutChoice,
    type RunOutChoices,
    type RunOutStatus,
} from "./api";
import { listWhen } from "./shoppingList";
import { useOptionalShoppingList } from "./useShoppingList";
import styles from "./RunOut.module.css";

// "Add to the shopping list when we run out" (#89): the item detail setting (project/ItemRunOut.dc.html) and the
// "Add to the shopping list?" sheet after a check-out (project/RunOutSheet.dc.html).

/** Lower case for the middle of a sentence ("Counts every semi-skimmed milk"), keeping words like "Dijon". */
const inSentence = (name: string) => name.charAt(0).toLowerCase() + name.slice(1);

/**
 * The checkbox card under the product heading: on for the generic name, so every brand counts. Saves at once and says
 * so in a toast. Hidden while the product has no generic name yet (there's nothing to hold the setting).
 */
export function RunOutCard({
    api,
    productId,
    inStockCount,
    onSaved,
}: {
    api: ApiClient;
    productId: string;
    /** This product's own count: when it changes (a check-out here), the total across the name is fetched again. */
    inStockCount: number;
    onSaved: (message: string) => void;
}) {
    const [status, setStatus] = useState<RunOutStatus | null>(null);
    const [saving, setSaving] = useState(false);

    useEffect(() => {
        let live = true;
        // Through a promise, so even a synchronous failure only hides the card (item detail still works).
        Promise.resolve()
            .then(() => api.runOutStatus(productId))
            .then((answer) => live && setStatus(answer))
            .catch((error: unknown) => {
                if (!(error instanceof SignInRequiredError))
                    console.warn("Couldn't load the run-out setting", error);
            });
        return () => {
            live = false;
        };
    }, [api, productId, inStockCount]);

    if (!status?.canSet) return null;

    const flip = async () => {
        const next = !status.addWhenOut;
        setStatus({ ...status, addWhenOut: next });
        setSaving(true);
        try {
            await api.setAddWhenOut(status.genericNameId, next);
            onSaved(
                next ? `Will add ${status.name} when it runs out` : `Won't add ${status.name} automatically`,
            );
        } catch (error) {
            setStatus({ ...status, addWhenOut: !next });
            if (!(error instanceof SignInRequiredError)) onSaved("Couldn't save that. Try again.");
        } finally {
            setSaving(false);
        }
    };

    return (
        <label className={status.addWhenOut ? `${styles.setting} ${styles.settingOn}` : styles.setting}>
            <span className={styles.checkWrap}>
                <input
                    type="checkbox"
                    className={styles.check}
                    checked={status.addWhenOut}
                    disabled={saving}
                    onChange={() => void flip()}
                />
            </span>
            <span className={styles.settingText}>
                <span className={styles.settingTitle}>
                    Add {status.name} to the shopping list when we run out
                </span>
                <span className={styles.settingHelp}>
                    Counts every {inSentence(status.name)}, whatever the brand. {status.inStock} in stock now.
                </span>
            </span>
        </label>
    );
}

/** What the sheet is for: a tapped stock change push (its batch), or a tapped "Checked out" toast (one product). */
export type RunOutSource = { batchId: string } | { productId: string };

interface Row extends RunOutChoice {
    added?: boolean;
}

/**
 * "Add to the shopping list?" after a check-out. Step 1: a row per removed generic item with how many are left, and
 * Add (or "On the list"). Step 2, after an Add, when the setting isn't on yet: "Add X automatically whenever we run
 * out?". Closes by itself if there's nothing to offer (e.g. the update has expired, or nothing was removed).
 */
export function RunOutSheet({
    api,
    source,
    onClose,
    now,
}: {
    api: ApiClient;
    source: RunOutSource;
    onClose: () => void;
    now?: Date;
}) {
    const list = useOptionalShoppingList();
    const [choices, setChoices] = useState<RunOutChoices | null>(null);
    const [rows, setRows] = useState<Row[]>([]);
    const [asking, setAsking] = useState<Row | null>(null);
    const [note, setNote] = useState<string | null>(null);
    const closeRef = useRef(onClose);
    closeRef.current = onClose;
    const sourceKey = "batchId" in source ? `b:${source.batchId}` : `p:${source.productId}`;

    useEffect(() => {
        let live = true;
        const load =
            "batchId" in source ? api.runOutForBatch(source.batchId) : api.runOutForProduct(source.productId);
        load.then(
            (answer) => {
                if (!live) return;
                if (answer.items.length === 0) {
                    closeRef.current();
                    return;
                }
                setChoices(answer);
                setRows(answer.items);
            },
            (error: unknown) => {
                if (!live) return;
                if (!(error instanceof SignInRequiredError))
                    console.warn("Couldn't load what was checked out", error);
                closeRef.current();
            },
        );
        return () => {
            live = false;
        };
        // eslint-disable-next-line react-hooks/exhaustive-deps -- once per source
    }, [api, sourceKey]);

    useEffect(() => {
        const onKey = (e: KeyboardEvent) => e.key === "Escape" && closeRef.current();
        document.addEventListener("keydown", onKey);
        return () => document.removeEventListener("keydown", onKey);
    }, []);

    if (!choices) return null;

    const onList = (row: Row) =>
        row.onList || !!list?.open.some((i) => i.name.trim().toLowerCase() === row.name.trim().toLowerCase());

    const add = (row: Row) => {
        list?.add(row.name, row.productId, "checkOutPrompt");
        setRows((current) =>
            current.map((r) =>
                r.genericNameId === row.genericNameId ? { ...r, onList: true, added: true } : r,
            ),
        );
        setNote(null);
        if (row.canSet && !row.addWhenOut) setAsking(row);
    };

    const always = async (row: Row) => {
        setAsking(null);
        try {
            await api.setAddWhenOut(row.genericNameId, true);
            setRows((current) =>
                current.map((r) => (r.genericNameId === row.genericNameId ? { ...r, addWhenOut: true } : r)),
            );
            setNote(`Will add ${row.name} automatically when it runs out.`);
        } catch (error) {
            if (!(error instanceof SignInRequiredError))
                setNote("Couldn't save that. Try again from the item.");
        }
    };

    const by = choices.checkedOutBy;
    const when = choices.at ? listWhen(choices.at, now ?? new Date()) : null;
    const who =
        by === "the scanner" ? "Checked out at the scanner" : by ? `Checked out by ${by}` : "Checked out";
    const heading = "batchId" in source ? (when ? `${who} · ${when}` : who) : "Just checked out";
    const anyAdded = rows.some((r) => r.added);

    return (
        <div className={styles.overlay}>
            <div className={styles.scrim} aria-hidden="true" onClick={onClose} />
            <div role="dialog" aria-modal="true" aria-labelledby="run-out-title" className={styles.sheet}>
                <div className={styles.handle} aria-hidden="true" />
                {asking ? (
                    <>
                        <div className={styles.added}>
                            <TickIcon size={20} />
                            Added {asking.name} to the list
                        </div>
                        <div className={styles.titleBlock}>
                            <h2 id="run-out-title" className={styles.askTitle}>
                                Add {asking.name} automatically whenever we run out?
                            </h2>
                            <div className={styles.askHelp}>
                                Counts every {inSentence(asking.name)}, whatever the brand. You can change
                                this on the item any time.
                            </div>
                        </div>
                        <div className={styles.askButtons}>
                            <button
                                type="button"
                                className={styles.primary}
                                onClick={() => void always(asking)}
                            >
                                Yes, always
                            </button>
                            <button
                                type="button"
                                className={styles.secondary}
                                onClick={() => setAsking(null)}
                            >
                                Just this time
                            </button>
                        </div>
                    </>
                ) : (
                    <>
                        <div className={styles.titleBlock}>
                            <div className={styles.kicker}>{heading}</div>
                            <h2 id="run-out-title" className={styles.title}>
                                Add to the shopping list?
                            </h2>
                        </div>
                        <ul className={styles.rows}>
                            {rows.map((row) => (
                                <li key={row.genericNameId} className={styles.row}>
                                    <div className={styles.rowText}>
                                        <span className={styles.rowName}>{row.name}</span>
                                        <span className={row.left === 0 ? styles.noneLeft : styles.left}>
                                            {row.left === 0 ? "None left" : `${row.left} left`}
                                        </span>
                                    </div>
                                    {onList(row) ? (
                                        <span className={styles.onList}>
                                            <TickIcon size={18} />
                                            On the list
                                        </span>
                                    ) : (
                                        <button
                                            type="button"
                                            className={styles.add}
                                            aria-label={`Add ${row.name} to the shopping list`}
                                            onClick={() => add(row)}
                                            disabled={!list}
                                        >
                                            <svg
                                                width="16"
                                                height="16"
                                                viewBox="0 0 24 24"
                                                fill="none"
                                                stroke="currentColor"
                                                strokeWidth="2.6"
                                                strokeLinecap="round"
                                                aria-hidden="true"
                                            >
                                                <path d="M12 5v14M5 12h14" />
                                            </svg>
                                            Add
                                        </button>
                                    )}
                                </li>
                            ))}
                        </ul>
                        {note && <div className={styles.note}>{note}</div>}
                        <button type="button" className={styles.close} onClick={onClose}>
                            {anyAdded ? "Done" : "Not now"}
                        </button>
                    </>
                )}
            </div>
        </div>
    );
}

function TickIcon({ size }: { size: number }) {
    return (
        <svg
            width={size}
            height={size}
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2.4"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
        >
            <path d="M5 12.5l4.5 4.5L19 7.5" />
        </svg>
    );
}
