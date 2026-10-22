import { useEffect, useRef, useState } from "react";
import { SignInRequiredError, type ApiClient, type ExpiredUnit } from "./api";
import { daysOver, expiredUnitMeta } from "./format";
import styles from "./CheckSheet.module.css";

/** Whether the page was opened from the "Still got this?" push (#94): /?check=expired. */
export function checkFromUrl(search: string = window.location.search): boolean {
    return new URLSearchParams(search).get("check") === "expired";
}

type Answer = "here" | "gone";

/**
 * "Still got these?" (#94, project/CheckSheet.dc.html): the expired units, over the stock list, each with Still here and
 * Gone. An answered row collapses to what happened, with Undo. Not now until everything's answered, then Done.
 */
export function CheckSheet({ api, onClose, now }: { api: ApiClient; onClose: () => void; now?: Date }) {
    const [units, setUnits] = useState<ExpiredUnit[] | null>(null);
    const [failed, setFailed] = useState(false);
    const [answers, setAnswers] = useState<Record<string, Answer>>({});
    const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());
    const [error, setError] = useState<string | null>(null);
    const closeRef = useRef(onClose);
    closeRef.current = onClose;

    useEffect(() => {
        let live = true;
        Promise.resolve()
            .then(() => api.expiredUnits())
            .then((answer) => live && setUnits(answer.units))
            .catch((e: unknown) => {
                if (live && !(e instanceof SignInRequiredError)) setFailed(true);
            });
        return () => {
            live = false;
        };
    }, [api]);

    useEffect(() => {
        const onKey = (e: KeyboardEvent) => e.key === "Escape" && closeRef.current();
        document.addEventListener("keydown", onKey);
        return () => document.removeEventListener("keydown", onKey);
    }, []);

    const run = async (unit: ExpiredUnit, call: () => Promise<unknown>, answer: Answer | undefined) => {
        if (busy.has(unit.unitId)) return;
        setBusy((b) => new Set(b).add(unit.unitId));
        setError(null);
        try {
            await call();
            setAnswers((a) => {
                const next = { ...a };
                if (answer) next[unit.unitId] = answer;
                else delete next[unit.unitId];
                return next;
            });
        } catch (e) {
            if (!(e instanceof SignInRequiredError)) setError("Couldn't save that. Try again.");
        } finally {
            setBusy((b) => {
                const next = new Set(b);
                next.delete(unit.unitId);
                return next;
            });
        }
    };

    const here = (u: ExpiredUnit) => run(u, () => api.stillHere(u.unitId), "here");
    const gone = (u: ExpiredUnit) => run(u, () => api.unitGone(u.unitId), "gone");
    const undo = (u: ExpiredUnit) =>
        run(
            u,
            () => (answers[u.unitId] === "gone" ? api.undoCheckOut(u.unitId) : api.undoStillHere(u.unitId)),
            undefined,
        );

    const left = (units ?? []).filter((u) => !answers[u.unitId]).length;
    const finished = units !== null && left === 0;
    const subtitle =
        units === null
            ? failed
                ? "Couldn't load what's expired. Try again later."
                : "Loading…"
            : units.length === 0
              ? "Nothing to check right now"
              : units.length === 1
                ? "1 expired item is still in stock."
                : `${units.length} expired items are still in stock.`;

    return (
        <div className={styles.overlay}>
            <div className={styles.scrim} aria-hidden="true" onClick={onClose} />
            <div role="dialog" aria-modal="true" aria-labelledby="check-title" className={styles.sheet}>
                <div className={styles.handle} aria-hidden="true" />
                <div className={styles.titleBlock}>
                    <h2 id="check-title" className={styles.title}>
                        Still got these?
                    </h2>
                    <div className={styles.subtitle} role={units === null ? "status" : undefined}>
                        {subtitle}
                    </div>
                </div>

                {units && units.length > 0 && (
                    <ul className={styles.rows}>
                        {units.map((u) => {
                            const answer = answers[u.unitId];
                            const isBusy = busy.has(u.unitId);
                            return (
                                <li key={u.unitId} className={styles.row}>
                                    <div className={styles.rowHead}>
                                        <div className={styles.rowText}>
                                            <span
                                                className={
                                                    answer === "gone"
                                                        ? `${styles.name} ${styles.struck}`
                                                        : styles.name
                                                }
                                            >
                                                {u.name}
                                            </span>
                                            <span className={styles.meta}>{expiredUnitMeta(u, now)}</span>
                                        </div>
                                        <span className={styles.badge}>{daysOver(u.daysOver)}</span>
                                    </div>
                                    {answer ? (
                                        <div className={styles.answered}>
                                            <span
                                                className={
                                                    answer === "gone"
                                                        ? `${styles.state} ${styles.stateGone}`
                                                        : styles.state
                                                }
                                            >
                                                <svg
                                                    width="16"
                                                    height="16"
                                                    viewBox="0 0 24 24"
                                                    fill="none"
                                                    stroke="currentColor"
                                                    strokeWidth="2.6"
                                                    strokeLinecap="round"
                                                    strokeLinejoin="round"
                                                    aria-hidden="true"
                                                >
                                                    <path d="M5 12.5l4.5 4.5L19 7.5" />
                                                </svg>
                                                {answer === "gone"
                                                    ? "Gone: taken out of stock"
                                                    : "Still here: we'll ask again in 2 days"}
                                            </span>
                                            <button
                                                type="button"
                                                className={styles.undo}
                                                disabled={isBusy}
                                                aria-label={`Undo ${u.name}`}
                                                onClick={() => void undo(u)}
                                            >
                                                Undo
                                            </button>
                                        </div>
                                    ) : (
                                        <div className={styles.buttons}>
                                            <button
                                                type="button"
                                                className={styles.stillHere}
                                                disabled={isBusy}
                                                aria-label={`${u.name}: still here`}
                                                onClick={() => void here(u)}
                                            >
                                                Still here
                                            </button>
                                            <button
                                                type="button"
                                                className={styles.gone}
                                                disabled={isBusy}
                                                aria-label={`${u.name}: gone`}
                                                onClick={() => void gone(u)}
                                            >
                                                Gone
                                            </button>
                                        </div>
                                    )}
                                </li>
                            );
                        })}
                    </ul>
                )}

                {error && (
                    <p className={styles.error} role="alert">
                        {error}
                    </p>
                )}

                <button
                    type="button"
                    className={finished ? `${styles.close} ${styles.done}` : styles.close}
                    onClick={onClose}
                >
                    {finished ? "Done" : "Not now"}
                </button>
            </div>
        </div>
    );
}
