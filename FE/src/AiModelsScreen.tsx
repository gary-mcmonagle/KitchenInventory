import { useCallback, useEffect, useRef, useState } from "react";
import {
    ApiError,
    NoAccessError,
    SignInRequiredError,
    type AiJob,
    type AiModel,
    type AiModels,
    type ApiClient,
} from "./api";
import {
    activeModel,
    changedLine,
    costPhrase,
    JOB_TITLE,
    JOB_WORDS,
    JOBS,
    modelMeta,
    modelNote,
    modelStatus,
    previousLabel,
} from "./aiModels";
import styles from "./AiModels.module.css";
import { AI_MODEL_ADD_PATH, aiModelTestPath, goBack, navigate, SETTINGS_PATH } from "./router";

type Sheet = { job: AiJob; step: "pick" } | { job: AiJob; step: "confirm"; choice: string };
type Toast = { text: string; undo?: () => void };

const TOAST_MS = 8000;
const REFRESH_WHILE_TESTING_MS = 4000;

/** The API answers a change with the settings; the "left this hour" count only comes with the GET, so keep the last. */
const merge = (before: AiModels | null, next: AiModels): AiModels => ({
    ...next,
    testRuns: next.testRuns ?? before?.testRuns ?? null,
});

/**
 * AI models (#123, project/AIModels.dc.html), at /settings/ai-models: **In use** (a card per job, Change, Go back to…)
 * and **Models** (Add a model, a card per model with its test results, Run tests and Remove). A switch asks first,
 * with the estimated cost against the current model, and has Undo. Only a model that passed the job's tests can be
 * chosen (the API refuses the rest too). Nothing about temperature or where a model runs.
 */
export function AiModelsScreen({
    api,
    pollMs = REFRESH_WHILE_TESTING_MS,
}: {
    api: ApiClient;
    pollMs?: number;
}) {
    const [data, setData] = useState<AiModels | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [sheet, setSheet] = useState<Sheet | null>(null);
    const [toast, setToast] = useState<Toast | null>(null);
    const [busy, setBusy] = useState(false);
    const [removing, setRemoving] = useState<string | null>(null);
    const live = useRef(true);
    const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    const removeTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

    const load = useCallback(async () => {
        try {
            const next = await api.aiModels();
            if (!live.current) return;
            setData((before) => merge(before, next));
            setError(null);
        } catch (e) {
            if (!live.current || e instanceof SignInRequiredError) return;
            setError(
                e instanceof NoAccessError
                    ? "You're signed in, but this account can't use Cupboard Tracker."
                    : "Couldn't load the AI models.",
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
            clearTimeout(removeTimer.current);
        };
    }, [load]);

    // A model being tested (here or on another phone) shows "Testing…": look again until it's done.
    const anyTesting = !!data?.models.some((m) => m.testing);
    useEffect(() => {
        if (!anyTesting) return;
        const timer = setInterval(() => void load(), pollMs);
        return () => clearInterval(timer);
    }, [anyTesting, load, pollMs]);

    const showToast = useCallback((next: Toast) => {
        clearTimeout(toastTimer.current);
        setToast(next);
        toastTimer.current = setTimeout(() => setToast(null), TOAST_MS);
    }, []);

    const problem = (e: unknown, fallback: string) => {
        if (e instanceof SignInRequiredError) return;
        const body = e instanceof ApiError ? (e.body as { error?: unknown } | undefined) : undefined;
        showToast({ text: typeof body?.error === "string" ? body.error : fallback });
    };

    const apply = (next: AiModels) => setData((before) => merge(before, next));

    /** Goes back to the job's previous model (or its default); doing it again undoes it. */
    const revert = async (job: AiJob) => {
        if (busy) return;
        setBusy(true);
        try {
            const next = await api.revertAiModel(job);
            if (!live.current) return;
            apply(next);
            showToast({
                text: `${JOB_TITLE[job]} now use ${activeModel(next, job).label}`,
                undo: () => void revert(job),
            });
        } catch (e) {
            problem(e, "Couldn't change that. Try again.");
        } finally {
            setBusy(false);
        }
    };

    const switchTo = async (job: AiJob, modelId: string) => {
        if (busy) return;
        setBusy(true);
        try {
            const next = await api.setActiveAiModel(job, modelId);
            if (!live.current) return;
            apply(next);
            setSheet(null);
            showToast({
                text: `${JOB_TITLE[job]} now use ${activeModel(next, job).label}`,
                undo: () => void revert(job),
            });
        } catch (e) {
            setSheet(null);
            problem(e, "Couldn't change that. Try again.");
        } finally {
            setBusy(false);
        }
    };

    const remove = async (model: AiModel) => {
        if (removing !== model.id) {
            // A first tap asks; a second, within a few seconds, removes.
            setRemoving(model.id);
            clearTimeout(removeTimer.current);
            removeTimer.current = setTimeout(() => setRemoving(null), 4000);
            return;
        }
        clearTimeout(removeTimer.current);
        setRemoving(null);
        setBusy(true);
        try {
            apply(await api.removeAiModel(model.id));
            showToast({ text: `${model.label} removed` });
        } catch (e) {
            problem(e, "Couldn't remove that. Try again.");
        } finally {
            setBusy(false);
        }
    };

    const inUse = new Set(
        data ? JOBS.map((j) => data.active[j]?.modelId).filter((id): id is string => !!id) : [],
    );
    const defaultsInUse = new Set(
        data ? JOBS.map((j) => activeModel(data, j).model?.id).filter((id): id is string => !!id) : [],
    );

    return (
        <div className={styles.screen}>
            <header className={styles.header}>
                <a
                    href={SETTINGS_PATH}
                    className={styles.back}
                    aria-label="Back to settings"
                    onClick={(e) => {
                        e.preventDefault();
                        goBack(SETTINGS_PATH);
                    }}
                >
                    <BackIcon />
                    Settings
                </a>
                <h1 className={styles.title}>AI models</h1>
                <div className={styles.subtitle}>
                    Which model each job uses. Changes take effect within a minute.
                </div>
            </header>

            <div className={styles.content}>
                {error && (
                    <div className={styles.noticeRow}>
                        <p className={styles.notice} role="alert">
                            {error}
                        </p>
                        <button type="button" className={styles.link} onClick={() => void load()}>
                            Try again
                        </button>
                    </div>
                )}
                {!data && !error && (
                    <>
                        <div className={styles.skeleton} aria-hidden="true" />
                        <div className={styles.skeleton} aria-hidden="true" />
                    </>
                )}
                {data && (
                    <>
                        <section className={styles.group} aria-labelledby="in-use-title">
                            <h2 id="in-use-title" className={styles.groupTitle}>
                                In use
                            </h2>
                            {JOBS.map((job) => {
                                const active = activeModel(data, job);
                                const back = previousLabel(data, job);
                                return (
                                    <div
                                        key={job}
                                        className={styles.card}
                                        role="group"
                                        aria-label={JOB_TITLE[job]}
                                    >
                                        <div className={styles.cardRow}>
                                            <div className={styles.cardText}>
                                                <span className={styles.jobName}>{JOB_TITLE[job]}</span>
                                                <span className={styles.modelName}>{active.label}</span>
                                                <span className={styles.small}>
                                                    <code className={styles.mono}>{active.deployment}</code> ·{" "}
                                                    {changedLine(data, job)}
                                                </span>
                                            </div>
                                            <button
                                                type="button"
                                                className={styles.outline}
                                                aria-label={`Change the model for ${JOB_WORDS[job]}`}
                                                onClick={() => setSheet({ job, step: "pick" })}
                                            >
                                                Change
                                            </button>
                                        </div>
                                        {back && (
                                            <button
                                                type="button"
                                                className={styles.link}
                                                disabled={busy}
                                                onClick={() => void revert(job)}
                                            >
                                                Go back to {back}
                                            </button>
                                        )}
                                    </div>
                                );
                            })}
                        </section>

                        <section className={styles.group} aria-labelledby="models-title">
                            <h2 id="models-title" className={styles.groupTitle}>
                                <span>Models</span>
                                <span className={styles.groupNote}>{data.models.length} added</span>
                            </h2>
                            <a
                                href={AI_MODEL_ADD_PATH}
                                className={styles.big}
                                onClick={(e) => {
                                    e.preventDefault();
                                    navigate(AI_MODEL_ADD_PATH);
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
                                Add a model
                            </a>
                            {data.models.map((m) => {
                                const status = modelStatus(m);
                                const used = inUse.has(m.id) || defaultsInUse.has(m.id);
                                return (
                                    <div key={m.id} className={styles.card} role="group" aria-label={m.label}>
                                        <div className={styles.cardRow} style={{ alignItems: "flex-start" }}>
                                            <div className={styles.cardText}>
                                                <span className={styles.modelNameSmall}>{m.label}</span>
                                                <code className={`${styles.mono} ${styles.small}`}>
                                                    {m.deployment}
                                                </code>
                                            </div>
                                            <span className={`${styles.pill} ${TONE[status.tone]}`}>
                                                {status.text}
                                            </span>
                                        </div>
                                        {m.tests?.status === "done" && (
                                            <div className={styles.chips}>
                                                {JOBS.map((job) => {
                                                    const ok = m.jobs.includes(job);
                                                    return (
                                                        <span
                                                            key={job}
                                                            className={
                                                                ok
                                                                    ? styles.chip
                                                                    : `${styles.chip} ${styles.chipNo}`
                                                            }
                                                        >
                                                            {ok ? "✓" : "✕"} {JOB_TITLE[job]}
                                                        </span>
                                                    );
                                                })}
                                            </div>
                                        )}
                                        <div className={styles.note}>{modelNote(m)}</div>
                                        <div className={styles.meta}>{modelMeta(m)}</div>
                                        <div className={styles.actions}>
                                            <a
                                                href={aiModelTestPath(m.id)}
                                                className={styles.secondary}
                                                aria-label={`Run tests for ${m.label}`}
                                                onClick={(e) => {
                                                    e.preventDefault();
                                                    navigate(aiModelTestPath(m.id));
                                                }}
                                            >
                                                {m.testing ? "See the tests" : "Run tests"}
                                            </a>
                                            {!used && (
                                                <button
                                                    type="button"
                                                    className={styles.danger}
                                                    disabled={busy}
                                                    aria-label={
                                                        removing === m.id
                                                            ? `Tap again to remove ${m.label}`
                                                            : `Remove ${m.label}`
                                                    }
                                                    onClick={() => void remove(m)}
                                                >
                                                    {removing === m.id ? "Tap again" : "Remove"}
                                                </button>
                                            )}
                                        </div>
                                    </div>
                                );
                            })}
                        </section>
                    </>
                )}
            </div>

            {toast && (
                <div className={styles.toast} role="status">
                    <span className={styles.toastText}>{toast.text}</span>
                    {toast.undo && (
                        <button
                            type="button"
                            className={styles.toastUndo}
                            onClick={() => {
                                const undo = toast.undo;
                                setToast(null);
                                undo?.();
                            }}
                        >
                            Undo
                        </button>
                    )}
                </div>
            )}

            {data && sheet && (
                <ChangeSheet
                    data={data}
                    sheet={sheet}
                    busy={busy}
                    onClose={() => setSheet(null)}
                    onChoose={(choice) => setSheet({ job: sheet.job, step: "confirm", choice })}
                    onBack={() => setSheet({ job: sheet.job, step: "pick" })}
                    onSwitch={(modelId) => void switchTo(sheet.job, modelId)}
                />
            )}
        </div>
    );
}

const TONE = { ok: styles.ok, warn: styles.warn, fail: styles.fail, none: "" } as const;

function BackIcon() {
    return (
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
    );
}

/** Change: pick a model (those that didn't pass this job's tests are greyed out), then confirm with both costs. */
function ChangeSheet({
    data,
    sheet,
    busy,
    onClose,
    onChoose,
    onBack,
    onSwitch,
}: {
    data: AiModels;
    sheet: Sheet;
    busy: boolean;
    onClose: () => void;
    onChoose: (modelId: string) => void;
    onBack: () => void;
    onSwitch: (modelId: string) => void;
}) {
    const { job } = sheet;
    const current = activeModel(data, job);
    const closeRef = useRef(onClose);
    closeRef.current = onClose;
    const dialog = useRef<HTMLDivElement>(null);

    useEffect(() => {
        const onKey = (e: KeyboardEvent) => e.key === "Escape" && closeRef.current();
        document.addEventListener("keydown", onKey);
        return () => document.removeEventListener("keydown", onKey);
    }, []);

    // Focus moves into the sheet when it opens and at each step.
    useEffect(() => {
        dialog.current?.focus();
    }, [sheet.step]);

    const chosen = sheet.step === "confirm" ? data.models.find((m) => m.id === sheet.choice) : undefined;

    return (
        <div className={styles.overlay}>
            <div className={styles.scrim} aria-hidden="true" onClick={onClose} />
            <div
                ref={dialog}
                tabIndex={-1}
                role="dialog"
                aria-modal="true"
                aria-labelledby="sheet-title"
                className={styles.sheet}
            >
                <div className={styles.handle} aria-hidden="true" />
                {sheet.step === "pick" || !chosen ? (
                    <>
                        <h2 id="sheet-title" className={styles.sheetTitle}>
                            Model for {JOB_WORDS[job]}
                        </h2>
                        <div className={styles.subtitle}>
                            Only models that passed this job's tests can be chosen.
                        </div>
                        <div role="radiogroup" aria-labelledby="sheet-title" className={styles.options}>
                            {data.models.map((m) => {
                                const ok = m.jobs.includes(job);
                                const isCurrent = current.model?.id === m.id;
                                return (
                                    <button
                                        key={m.id}
                                        type="button"
                                        role="radio"
                                        aria-checked={isCurrent}
                                        aria-disabled={!ok}
                                        className={[
                                            styles.option,
                                            ok ? "" : styles.optionOff,
                                            isCurrent ? styles.optionCurrent : "",
                                        ].join(" ")}
                                        onClick={() => ok && !isCurrent && onChoose(m.id)}
                                    >
                                        <span className={styles.optionText}>
                                            <span className={styles.optionLabel}>{m.label}</span>
                                            <span className={styles.optionSub}>
                                                {ok
                                                    ? costSub(m, job)
                                                    : `Didn’t pass the ${JOB_WORDS[job]} tests`}
                                            </span>
                                        </span>
                                        {isCurrent && <span className={styles.inUse}>In use</span>}
                                    </button>
                                );
                            })}
                        </div>
                        <button type="button" className={styles.sheetButton} onClick={onClose}>
                            Cancel
                        </button>
                    </>
                ) : (
                    <>
                        <h2 id="sheet-title" className={styles.sheetTitle}>
                            Switch {JOB_WORDS[job]} to {chosen.label}?
                        </h2>
                        <div className={styles.compare}>
                            <div className={styles.compareRow}>
                                <span className={styles.small}>{chosen.label}</span>
                                <strong>{costPhrase(chosen, job)}</strong>
                            </div>
                            <div className={styles.compareRow}>
                                <span className={styles.small}>{current.label} (now)</span>
                                <span>{costPhrase(current.model ?? undefined, job)}</span>
                            </div>
                            <div className={styles.meta}>Estimated from its last test run.</div>
                        </div>
                        <div className={styles.note}>
                            Takes effect within a minute. You can go back at any time.
                        </div>
                        <div className={styles.buttons}>
                            <button type="button" className={styles.sheetButton} onClick={onBack}>
                                Back
                            </button>
                            <button
                                type="button"
                                className={`${styles.sheetButton} ${styles.sheetPrimary}`}
                                disabled={busy}
                                onClick={() => onSwitch(chosen.id)}
                            >
                                Switch
                            </button>
                        </div>
                    </>
                )}
            </div>
        </div>
    );
}

/** "About 0.4p a recipe", or "Passed" when the job has no estimate. */
function costSub(model: AiModel, job: AiJob): string {
    const phrase = costPhrase(model, job);
    return phrase.startsWith("about") ? `About ${phrase.slice(6)}` : "Passed";
}
