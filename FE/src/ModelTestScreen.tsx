import { useCallback, useEffect, useRef, useState } from "react";
import {
    ApiError,
    SignInRequiredError,
    type AiModel,
    type AiModels,
    type ApiClient,
    type ModelCheck,
    type ModelTestRun,
} from "./api";
import {
    CHECK_NAMES,
    checksDone,
    checkText,
    checkTime,
    isSkipped,
    JOB_TITLE,
    JOBS,
    limitMessage,
    listWords,
    newModelId,
} from "./aiModels";
import styles from "./AiModels.module.css";
import { relativeTime } from "./format";
import { AI_MODELS_PATH, aiModelTestPath, goBack, replacePath } from "./router";

const POLL_MS = 2000;
const GIVE_UP_AFTER = 3; // failed polls in a row

type Props = { api: ApiClient; modelId: string | null; pollMs?: number };

/**
 * Add a model, and test it (#123, project/AIModelTest.dc.html). With no `modelId` (/settings/ai-models/add) it's the form:
 * a name to show and the deployment name as Foundry shows it. Adding registers the model (#120) and starts its tests
 * (#121). With a `modelId` (/settings/ai-models/<id>/test) it shows the ten checks as each one lands, polling the run
 * every 2 seconds, then a summary of the jobs the model can do. Leaving doesn't stop a run: the URL's `?run=` and the
 * model's own "testing" mark find it again.
 */
export function ModelTestScreen({ api, modelId, pollMs = POLL_MS }: Props) {
    return modelId ? <TestRun api={api} modelId={modelId} pollMs={pollMs} /> : <AddForm api={api} />;
}

function Header({ title, subline, back }: { title: string; subline?: React.ReactNode; back: () => void }) {
    return (
        <header className={styles.header}>
            <a
                href={AI_MODELS_PATH}
                className={styles.back}
                aria-label="Back to AI models"
                onClick={(e) => {
                    e.preventDefault();
                    back();
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
                AI models
            </a>
            <h1 className={styles.title}>{title}</h1>
            {subline && <div className={styles.subtitle}>{subline}</div>}
        </header>
    );
}

const errorText = (e: unknown): string | undefined => {
    const body = e instanceof ApiError ? (e.body as { error?: unknown } | undefined) : undefined;
    return typeof body?.error === "string" ? body.error : undefined;
};

function AddForm({ api }: { api: ApiClient }) {
    const [label, setLabel] = useState("");
    const [deployment, setDeployment] = useState("");
    const [data, setData] = useState<AiModels | null>(null);
    const [busy, setBusy] = useState(false);
    const [problem, setProblem] = useState<{ field: "label" | "deployment" | "form"; text: string } | null>(
        null,
    );

    useEffect(() => {
        let live = true;
        Promise.resolve()
            .then(() => api.aiModels())
            .then((next) => live && setData(next))
            .catch(() => undefined); // only the "runs left" line and the duplicate check depend on it
        return () => {
            live = false;
        };
    }, [api]);

    const left = data?.testRuns?.left ?? null;
    const perHour = data?.testRuns?.perHour ?? 5;
    const noneLeft = left === 0;

    const submit = async (event: React.FormEvent) => {
        event.preventDefault();
        if (busy) return;
        const name = label.trim();
        const dep = deployment.trim();
        if (!name) return setProblem({ field: "label", text: "Give it a name to show." });
        if (!dep) return setProblem({ field: "deployment", text: "Enter the deployment name from Foundry." });
        if (data?.models.some((m) => m.deployment.toLowerCase() === dep.toLowerCase()))
            return setProblem({ field: "deployment", text: "That deployment is already added." });
        setBusy(true);
        setProblem(null);
        try {
            const id = newModelId(name);
            await api.addAiModel(id, name, dep);
            replacePath(aiModelTestPath(id)); // the form gives way to the tests; the tests start on arrival
        } catch (e) {
            if (e instanceof SignInRequiredError) return;
            const text = errorText(e);
            setProblem(
                e instanceof ApiError && e.status === 409 && text?.includes("already registered")
                    ? { field: "deployment", text: "That deployment is already added." }
                    : e instanceof ApiError && e.status === 400 && text
                      ? { field: /deployment/i.test(text) ? "deployment" : "label", text }
                      : { field: "form", text: text ?? "Couldn't add that. Try again." },
            );
            setBusy(false);
        }
    };

    return (
        <div className={styles.screen}>
            <Header title="Add a model" back={() => goBack(AI_MODELS_PATH)} />
            <div className={styles.content}>
                <form className={styles.form} onSubmit={(e) => void submit(e)} noValidate>
                    <div className={styles.field}>
                        <label htmlFor="m-label" className={styles.label}>
                            Name to show
                        </label>
                        <input
                            id="m-label"
                            className={`${styles.input} ${problem?.field === "label" ? styles.inputBad : ""}`}
                            value={label}
                            placeholder="e.g. GPT-6 Luna"
                            maxLength={60}
                            onChange={(e) => setLabel(e.target.value)}
                            aria-invalid={problem?.field === "label"}
                            aria-describedby={problem?.field === "label" ? "m-label-problem" : undefined}
                        />
                        {problem?.field === "label" && (
                            <div id="m-label-problem" className={styles.problem} role="alert">
                                {problem.text}
                            </div>
                        )}
                    </div>
                    <div className={styles.field}>
                        <label htmlFor="m-dep" className={styles.label}>
                            Deployment name
                        </label>
                        <input
                            id="m-dep"
                            className={`${styles.input} ${styles.mono} ${problem?.field === "deployment" ? styles.inputBad : ""}`}
                            value={deployment}
                            placeholder="e.g. gpt-4o"
                            maxLength={64}
                            autoCapitalize="off"
                            autoCorrect="off"
                            spellCheck={false}
                            onChange={(e) => setDeployment(e.target.value)}
                            aria-invalid={problem?.field === "deployment"}
                            aria-describedby={
                                problem?.field === "deployment" ? "m-dep-help m-dep-problem" : "m-dep-help"
                            }
                        />
                        <div id="m-dep-help" className={styles.help}>
                            It must already be deployed. In Foundry, open Deployments and copy the{" "}
                            <strong>Name</strong> column.
                        </div>
                        {problem?.field === "deployment" && (
                            <div id="m-dep-problem" className={styles.problem} role="alert">
                                {problem.text}
                            </div>
                        )}
                    </div>
                    <div className={styles.aside}>
                        We'll run 10 quick checks with made-up stock (no household data). A run takes about a
                        minute and costs about 5 to 10p.
                        {left !== null && ` You have ${left} of ${perHour} test runs left this hour.`}
                    </div>
                    {noneLeft && (
                        <div className={styles.problem} role="alert">
                            {limitMessage(perHour, undefined)}
                        </div>
                    )}
                    {problem?.field === "form" && (
                        <div className={styles.problem} role="alert">
                            {problem.text}
                        </div>
                    )}
                    <button type="submit" className={styles.submit} disabled={busy || noneLeft}>
                        {busy ? "Adding…" : "Add and test"}
                    </button>
                </form>
            </div>
        </div>
    );
}

type View =
    | { kind: "loading" }
    | { kind: "missing" }
    | { kind: "error" }
    | { kind: "limit"; text: string }
    | { kind: "run"; run: ModelTestRun };

function TestRun({ api, modelId, pollMs }: { api: ApiClient; modelId: string; pollMs: number }) {
    const [view, setView] = useState<View>({ kind: "loading" });
    const [model, setModel] = useState<AiModel | null>(null);
    const [stale, setStale] = useState(false); // the last poll failed
    const live = useRef(true);
    const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    const perHour = useRef(5);

    const poll = useCallback(
        (runId: string, failures = 0) => {
            clearTimeout(timer.current);
            timer.current = setTimeout(async () => {
                try {
                    const run = await api.modelTestRun(runId);
                    if (!live.current) return;
                    setStale(false);
                    setView({ kind: "run", run });
                    if (run.status === "queued" || run.status === "running") poll(runId);
                    else
                        void api.aiModels().then(
                            (d) => live.current && setModel(d.models.find((m) => m.id === modelId) ?? null),
                            () => undefined,
                        );
                } catch (e) {
                    if (!live.current || e instanceof SignInRequiredError) return;
                    setStale(true);
                    if (failures + 1 < GIVE_UP_AFTER) poll(runId, failures + 1);
                    else setView({ kind: "error" });
                }
            }, pollMs);
        },
        [api, modelId, pollMs],
    );

    /** Starts a run (or picks up the one already going) and follows it. */
    const start = useCallback(async () => {
        setView({ kind: "loading" });
        try {
            const run = await api.startModelTest(modelId);
            if (!live.current) return;
            replacePath(`${aiModelTestPath(modelId)}?run=${encodeURIComponent(run.runId)}`);
            setView({ kind: "run", run });
            if (run.status === "queued" || run.status === "running") poll(run.runId);
        } catch (e) {
            if (!live.current || e instanceof SignInRequiredError) return;
            if (e instanceof ApiError && e.status === 429) {
                const body = e.body as { retryAt?: string } | undefined;
                setView({ kind: "limit", text: limitMessage(perHour.current, body?.retryAt) });
            } else if (e instanceof ApiError && e.status === 404) {
                setView({ kind: "missing" });
            } else {
                setView({ kind: "error" });
            }
        }
    }, [api, modelId, poll]);

    const open = useCallback(async () => {
        setView({ kind: "loading" });
        try {
            const data = await api.aiModels();
            if (!live.current) return;
            perHour.current = data.testRuns?.perHour ?? 5;
            const found = data.models.find((m) => m.id === modelId) ?? null;
            setModel(found);
            if (!found) return setView({ kind: "missing" });
            const wanted = new URLSearchParams(window.location.search).get("run") ?? found.testing;
            if (wanted) {
                try {
                    const run = await api.modelTestRun(wanted);
                    if (!live.current) return;
                    setView({ kind: "run", run });
                    if (run.status === "queued" || run.status === "running") poll(run.runId);
                    return;
                } catch (e) {
                    if (e instanceof SignInRequiredError) return;
                    if (!(e instanceof ApiError && e.status === 404)) throw e;
                    // The run has expired: show the model's saved results if it has any, else start afresh.
                    if (found.tests) return setView({ kind: "run", run: found.tests });
                }
            }
            await start();
        } catch (e) {
            if (live.current && !(e instanceof SignInRequiredError)) setView({ kind: "error" });
        }
    }, [api, modelId, poll, start]);

    useEffect(() => {
        live.current = true;
        void open();
        return () => {
            live.current = false;
            clearTimeout(timer.current);
        };
    }, [open]);

    const back = () => goBack(AI_MODELS_PATH);
    const run = view.kind === "run" ? view.run : null;
    const label = model?.label ?? run?.deployment ?? "Model";
    const deployment = model?.deployment ?? run?.deployment ?? "";
    const running = run !== null && (run.status === "queued" || run.status === "running");

    return (
        <div className={styles.screen}>
            <Header
                title={view.kind === "loading" || running ? `Testing ${label}` : label}
                back={back}
                subline={
                    run ? (
                        <>
                            <code className={styles.mono}>{deployment}</code> ·{" "}
                            {running
                                ? `started ${relativeTime(run.startedAt)}`
                                : `tested ${relativeTime(run.finishedAt ?? run.startedAt)} by ${run.startedByName}`}
                        </>
                    ) : deployment ? (
                        <code className={styles.mono}>{deployment}</code>
                    ) : undefined
                }
            />
            <div className={styles.content}>
                {view.kind === "loading" && (
                    <div role="status" className={styles.subtitle}>
                        Starting the tests…
                    </div>
                )}
                {view.kind === "missing" && (
                    <p className={styles.notice} role="alert">
                        That model isn't there any more.
                    </p>
                )}
                {view.kind === "limit" && (
                    <p className={styles.notice} role="alert">
                        {view.text}
                    </p>
                )}
                {view.kind === "error" && (
                    <div className={styles.noticeRow}>
                        <p className={styles.notice} role="alert">
                            Couldn't follow the tests.
                        </p>
                        <button type="button" className={styles.link} onClick={() => void open()}>
                            Try again
                        </button>
                    </div>
                )}
                {run && <Results run={run} stale={stale} />}
                {(view.kind === "limit" || (run && !running)) && (
                    <div className={styles.doneRow}>
                        <a
                            href={AI_MODELS_PATH}
                            className={styles.big}
                            onClick={(e) => {
                                e.preventDefault();
                                back();
                            }}
                        >
                            Back to AI models
                        </a>
                        {view.kind !== "limit" && (
                            <button type="button" className={styles.again} onClick={() => void start()}>
                                Run again
                            </button>
                        )}
                    </div>
                )}
            </div>
        </div>
    );
}

function Results({ run, stale }: { run: ModelTestRun; stale: boolean }) {
    const finished = run.status === "done";
    return (
        <>
            {finished && <Summary run={run} />}
            {run.status === "failed" && (
                <div className={`${styles.summary} ${styles.fail}`}>
                    <div className={styles.summaryTitle}>The test run stopped</div>
                    <div className={styles.summaryText}>
                        {run.error ?? "Something went wrong."} Run it again, or try later.
                    </div>
                </div>
            )}
            <section className={styles.group} aria-labelledby="checks-title">
                <h2 id="checks-title" className={styles.groupTitle}>
                    <span>Checks</span>
                    <span className={styles.groupNote}>
                        {finished
                            ? `${run.checks.length} of ${run.checks.length} done`
                            : `${checksDone(run)} of ${run.checks.length} done`}
                        {stale && " · reconnecting…"}
                    </span>
                </h2>
                <ol className={styles.checks} aria-live="polite">
                    {run.checks.map((c) => (
                        <CheckRow key={c.id} check={c} />
                    ))}
                </ol>
            </section>
        </>
    );
}

function Summary({ run }: { run: ModelTestRun }) {
    const ok = run.jobs.length > 0;
    const warnings = run.checks.filter((c) => c.state === "warn").length;
    const missing = JOBS.filter((j) => !run.jobs.includes(j));
    const warnText =
        warnings > 0
            ? `Passed with ${warnings === 1 ? "one warning" : `${warnings} warnings`}. `
            : "Passed. ";
    return (
        <div className={`${styles.summary} ${ok ? styles.ok : styles.fail}`}>
            <div className={styles.summaryTitle}>
                {ok
                    ? `Ready for ${run.jobs.length === JOBS.length ? "every job" : listWords(run.jobs)}`
                    : "This model can’t be used"}
            </div>
            <div className={styles.summaryText}>
                {ok
                    ? `${warnText}You can make it the model for ${listWords(run.jobs)} from AI models.${missing.length > 0 ? ` It can’t be used for ${listWords(missing)}.` : ""}`
                    : "It’s saved so you can check the name and run the tests again, or remove it."}
            </div>
            <div className={styles.chips}>
                {JOBS.map((j) => {
                    const yes = run.jobs.includes(j);
                    return (
                        <span
                            key={j}
                            className={styles.summaryChip}
                            style={{ textDecoration: yes ? "none" : "line-through" }}
                        >
                            {yes ? "✓" : "✕"} {JOB_TITLE[j]}
                        </span>
                    );
                })}
            </div>
        </div>
    );
}

const ICON: Record<string, { mark: string; tone: string; reason: string }> = {
    pass: { mark: "✓", tone: styles.iconPass, reason: "" },
    warn: { mark: "!", tone: styles.iconWarn, reason: styles.reasonWarn },
    fail: { mark: "✕", tone: styles.iconFail, reason: styles.reasonFail },
    skip: { mark: "–", tone: "", reason: styles.reasonOff },
    waiting: { mark: "", tone: "", reason: styles.reasonOff },
    running: { mark: "", tone: styles.iconPass, reason: "" },
};

function CheckRow({ check }: { check: ModelCheck }) {
    const key = isSkipped(check) ? "skip" : check.state;
    const look =
        key === "pass" && check.id === "cost" ? { mark: "£", tone: styles.iconInfo, reason: "" } : ICON[key];
    const state = {
        pass: "passed",
        warn: "passed with a warning",
        fail: "failed",
        skip: "skipped",
        waiting: "waiting",
        running: "running",
    }[key];
    const time = checkTime(check);
    return (
        <li className={styles.check}>
            <span className={`${styles.icon} ${look.tone}`} aria-hidden="true">
                {check.state === "running" ? <span className={styles.spinner} /> : look.mark}
            </span>
            <span className={styles.checkText}>
                <span className={styles.checkHead}>
                    <span
                        className={
                            key === "waiting" || key === "skip"
                                ? `${styles.checkName} ${styles.checkWaiting}`
                                : styles.checkName
                        }
                    >
                        {CHECK_NAMES[check.id] ?? check.id}
                        <span className={styles.srOnly}> ({state})</span>
                    </span>
                    {time && <span className={styles.checkTime}>{time}</span>}
                </span>
                <span className={`${styles.checkReason} ${look.reason}`}>{checkText(check)}</span>
            </span>
        </li>
    );
}
