import { useEffect, useId, useMemo, useRef, useState } from "react";
import { ApiError, SignInRequiredError, type ApiClient, type UsualsItem, type UsualsJob } from "./api";
import { AISLES } from "./aisles";
import styles from "./Usuals.module.css";

/** How often the sheet asks how the job is getting on, and when it gives up (the job has a budget of about a minute). */
export const USUALS_POLL_MS = 2000;
export const USUALS_GIVE_UP_MS = 150_000;

const STEPS: Array<{ id: UsualsJob["step"]; label: string }> = [
    { id: "history", label: "Reading your shopping history" },
    { id: "deciding", label: "Working out what's due" },
    { id: "list", label: "Leaving out what's already on your list" },
];

const FAILED_TEXT = "That took too long or something went wrong. Nothing was added to your list.";
const LIMITED_TEXT = "That's a lot of usuals for one hour. Try again a bit later.";

type Phase =
    | { kind: "working"; step: UsualsJob["step"] }
    | { kind: "ready"; job: UsualsJob }
    | { kind: "failed"; text: string };

const keyOf = (item: UsualsItem) => item.name.trim().toLowerCase();
const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

/** The green "Add usuals" button in the list's tool row (#134). */
export function UsualsButton({ onClick, disabled }: { onClick: () => void; disabled?: boolean }) {
    return (
        <button type="button" className={styles.usualsButton} disabled={disabled} onClick={onClick}>
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
                <path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1" />
            </svg>
            Add usuals
        </button>
    );
}

/**
 * "Add usuals" (#134): starts a job, shows its progress, then **the selection**: every suggestion grouped by aisle, "Due" ticked
 * and "Maybe" not, with no limit on how many. Nothing goes on the list until the add button. Stop (or Cancel) abandons the job.
 */
export function UsualsSheet({
    api,
    onAdd,
    onClose,
    pollMs = USUALS_POLL_MS,
}: {
    api: ApiClient;
    /** Puts the ticked items on the list. */
    onAdd: (items: UsualsItem[]) => void;
    onClose: () => void;
    pollMs?: number;
}) {
    const [phase, setPhase] = useState<Phase>({ kind: "working", step: "history" });
    const [attempt, setAttempt] = useState(0);
    const [ticked, setTicked] = useState<Record<string, boolean>>({});
    const titleId = useId();
    const sheet = useRef<HTMLDivElement>(null);

    useEffect(() => {
        let live = true;
        let timer: ReturnType<typeof setTimeout> | undefined;
        const started = Date.now();
        const finish = (next: Phase) => live && setPhase(next);
        setPhase({ kind: "working", step: "history" });
        void (async () => {
            try {
                let job = await api.startUsuals();
                while (live) {
                    if (job.status === "ready") {
                        setTicked(Object.fromEntries(job.items.map((i) => [keyOf(i), i.band === "due"])));
                        finish({ kind: "ready", job });
                        return;
                    }
                    if (job.status === "failed" || Date.now() - started > USUALS_GIVE_UP_MS) {
                        finish({ kind: "failed", text: FAILED_TEXT });
                        return;
                    }
                    finish({ kind: "working", step: job.step });
                    await new Promise<void>((resolve) => {
                        timer = setTimeout(resolve, pollMs);
                    });
                    if (!live) return;
                    job = await api.usualsJob(job.jobId);
                }
            } catch (error) {
                if (error instanceof SignInRequiredError) return;
                finish({
                    kind: "failed",
                    text: error instanceof ApiError && error.status === 429 ? LIMITED_TEXT : FAILED_TEXT,
                });
            }
        })();
        return () => {
            live = false;
            clearTimeout(timer);
        };
    }, [api, attempt, pollMs]);

    useEffect(() => sheet.current?.focus(), [phase.kind]);

    const items = useMemo(() => (phase.kind === "ready" ? phase.job.items : []), [phase]);
    const groups = useMemo(() => {
        const known = new Set(AISLES.map((a) => a.id));
        return AISLES.map((aisle) => ({
            aisle,
            items: items.filter((i) => (known.has(i.aisle) ? i.aisle : "other") === aisle.id),
        })).filter((g) => g.items.length > 0);
    }, [items]);
    const due = items.filter((i) => i.band === "due");
    const picked = items.filter((i) => ticked[keyOf(i)]);
    const dueOnly = picked.length === due.length && due.every((i) => ticked[keyOf(i)]);

    const toggleDue = () =>
        setTicked(Object.fromEntries(items.map((i) => [keyOf(i), dueOnly ? false : i.band === "due"])));

    const tall = phase.kind === "ready" && items.length > 0;

    return (
        <div className={styles.overlay}>
            <div className={styles.scrim} aria-hidden="true" onClick={onClose} />
            <div
                ref={sheet}
                className={`${styles.sheet} ${tall ? styles.tall : ""}`}
                role="dialog"
                aria-modal="true"
                aria-labelledby={titleId}
                tabIndex={-1}
                onKeyDown={(e) => e.key === "Escape" && onClose()}
            >
                <div className={styles.handle} aria-hidden="true" />

                {phase.kind === "working" && (
                    <>
                        <div>
                            <h2 id={titleId} className={styles.title}>
                                Working out what's due
                            </h2>
                            <p className={styles.lead}>
                                Looking at what you usually buy. This can take up to a minute, and you can
                                keep using the app.
                            </p>
                        </div>
                        <ol className={styles.steps}>
                            {STEPS.map((step, index) => {
                                const at = STEPS.findIndex((s) => s.id === phase.step);
                                const state = index < at ? "done" : index === at ? "active" : "waiting";
                                return (
                                    <li
                                        key={step.id}
                                        className={`${styles.step} ${state === "active" ? styles.stepActive : ""}`}
                                    >
                                        {state === "done" ? (
                                            <span
                                                className={`${styles.stepIcon} ${styles.stepDone}`}
                                                aria-hidden="true"
                                            >
                                                <svg
                                                    width="14"
                                                    height="14"
                                                    viewBox="0 0 16 16"
                                                    fill="none"
                                                    stroke="#ffffff"
                                                    strokeWidth="3"
                                                    strokeLinecap="round"
                                                    strokeLinejoin="round"
                                                >
                                                    <path d="M3 8.5l3.2 3L13 4.5" />
                                                </svg>
                                            </span>
                                        ) : state === "active" ? (
                                            <span
                                                className={`${styles.stepIcon} ${styles.spinner}`}
                                                role="status"
                                                aria-label="Working"
                                            />
                                        ) : (
                                            <span
                                                className={`${styles.stepIcon} ${styles.stepWaiting}`}
                                                aria-hidden="true"
                                            />
                                        )}
                                        <span>{step.label}</span>
                                    </li>
                                );
                            })}
                        </ol>
                        <button type="button" className={styles.secondary} onClick={onClose}>
                            Stop
                        </button>
                    </>
                )}

                {phase.kind === "failed" && (
                    <>
                        <div className={styles.message}>
                            <span className={`${styles.badgeIcon} ${styles.badIcon}`} aria-hidden="true">
                                <svg
                                    width="28"
                                    height="28"
                                    viewBox="0 0 24 24"
                                    fill="none"
                                    stroke="currentColor"
                                    strokeWidth="2.2"
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                >
                                    <path d="M12 7v6M12 17h.01" />
                                </svg>
                            </span>
                            <h2 id={titleId} className={styles.title}>
                                Couldn't work out your usuals
                            </h2>
                            <p className={styles.body}>{phase.text}</p>
                        </div>
                        <button
                            type="button"
                            className={styles.primary}
                            onClick={() => setAttempt((n) => n + 1)}
                        >
                            Try again
                        </button>
                        <button type="button" className={styles.quiet} onClick={onClose}>
                            Close
                        </button>
                    </>
                )}

                {phase.kind === "ready" && items.length === 0 && (
                    <>
                        <div className={styles.message}>
                            <span className={`${styles.badgeIcon} ${styles.okIcon}`} aria-hidden="true">
                                <svg
                                    width="28"
                                    height="28"
                                    viewBox="0 0 24 24"
                                    fill="none"
                                    stroke="currentColor"
                                    strokeWidth="2.2"
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                >
                                    <path d="M5 12.5l4.2 4.2L19 7" />
                                </svg>
                            </span>
                            <h2 id={titleId} className={styles.title}>
                                Nothing looks due
                            </h2>
                            <p className={styles.body}>
                                Everything you usually buy is either on the list already or was bought
                                recently. Check again in a few days.
                            </p>
                        </div>
                        <button type="button" className={styles.primary} onClick={onClose}>
                            Done
                        </button>
                    </>
                )}

                {phase.kind === "ready" && items.length > 0 && (
                    <>
                        <div className={styles.head}>
                            <div>
                                <h2 id={titleId} className={styles.title}>
                                    Your usuals
                                </h2>
                                <p className={styles.lead}>
                                    What you usually buy that looks due. Tick what to add.
                                </p>
                            </div>
                            {phase.job.method === "pattern" && (
                                <div role="note" className={styles.note}>
                                    Based on your buying pattern only.
                                </div>
                            )}
                            <div className={styles.bar}>
                                <span
                                    className={styles.count}
                                >{`${due.length} due · ${items.length - due.length} maybe`}</span>
                                <button type="button" className={styles.link} onClick={toggleDue}>
                                    {dueOnly ? "Clear all" : "Select all due"}
                                </button>
                            </div>
                        </div>
                        <div className={styles.scroll}>
                            {groups.map((g) => (
                                <section key={g.aisle.id} aria-label={g.aisle.name}>
                                    <div className={styles.groupTitle}>
                                        <span>{g.aisle.name}</span>
                                        <span className={styles.groupCount}>{g.items.length}</span>
                                    </div>
                                    <ul className={styles.rows}>
                                        {g.items.map((item) => {
                                            const on = ticked[keyOf(item)] === true;
                                            return (
                                                <li key={keyOf(item)}>
                                                    <button
                                                        type="button"
                                                        className={`${styles.row} ${on ? styles.rowOn : ""}`}
                                                        aria-pressed={on}
                                                        onClick={() =>
                                                            setTicked((t) => ({ ...t, [keyOf(item)]: !on }))
                                                        }
                                                    >
                                                        <span className={styles.tick} aria-hidden="true">
                                                            <svg
                                                                width="16"
                                                                height="16"
                                                                viewBox="0 0 16 16"
                                                                fill="none"
                                                                stroke="#ffffff"
                                                                strokeWidth="3"
                                                                strokeLinecap="round"
                                                                strokeLinejoin="round"
                                                            >
                                                                <path d="M3 8.5l3.2 3L13 4.5" />
                                                            </svg>
                                                        </span>
                                                        <span className={styles.text}>
                                                            <span className={styles.name}>{item.name}</span>
                                                            <span className={styles.why}>{item.why}</span>
                                                        </span>
                                                        <span
                                                            className={`${styles.pill} ${
                                                                item.band === "due"
                                                                    ? styles.pillDue
                                                                    : styles.pillMaybe
                                                            }`}
                                                        >
                                                            {item.band === "due" ? "Due" : "Maybe"}
                                                        </span>
                                                    </button>
                                                </li>
                                            );
                                        })}
                                    </ul>
                                </section>
                            ))}
                        </div>
                        <div className={styles.foot}>
                            <button
                                type="button"
                                className={styles.primary}
                                disabled={picked.length === 0}
                                onClick={() => {
                                    onAdd(picked);
                                    onClose();
                                }}
                            >
                                {picked.length === 0
                                    ? "Tick something to add"
                                    : `Add ${plural(picked.length, "item")} to the list`}
                            </button>
                            <button type="button" className={styles.quiet} onClick={onClose}>
                                Cancel
                            </button>
                        </div>
                    </>
                )}
            </div>
        </div>
    );
}
