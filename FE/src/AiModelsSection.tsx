import { useCallback, useEffect, useState } from "react";
import { SignInRequiredError, type AiModels, type ApiClient } from "./api";
import { activeModel, JOB_TITLE, JOBS } from "./aiModels";
import styles from "./AiModels.module.css";
import { AI_MODELS_PATH, navigate } from "./router";

/**
 * "AI models" on the Settings screen (#123, project/Settings.dc.html): which model each job uses, and a button to the
 * AI models page. Read only; changing a model is on that page.
 */
export function AiModelsSection({ api }: { api: Pick<ApiClient, "aiModels"> }) {
    const [data, setData] = useState<AiModels | null>(null);
    const [failed, setFailed] = useState(false);

    const load = useCallback(() => {
        let live = true;
        setFailed(false);
        Promise.resolve()
            .then(() => api.aiModels())
            .then((next) => live && setData(next))
            .catch((e: unknown) => {
                if (live && !(e instanceof SignInRequiredError)) setFailed(true);
            });
        return () => {
            live = false;
        };
    }, [api]);

    useEffect(() => load(), [load]);

    return (
        <section className={styles.group} aria-labelledby="ai-title">
            <h2 id="ai-title" style={{ margin: 0, fontSize: 20, lineHeight: 1.2, fontWeight: 700 }}>
                AI models
            </h2>
            {failed && (
                <div className={styles.noticeRow}>
                    <p className={styles.notice} role="alert">
                        Couldn't load the AI models.
                    </p>
                    <button type="button" className={styles.link} onClick={() => load()}>
                        Try again
                    </button>
                </div>
            )}
            {!failed && !data && <div className={styles.skeleton} aria-hidden="true" />}
            {data && (
                <div className={styles.sectionCard}>
                    {JOBS.map((job) => {
                        const active = activeModel(data, job);
                        return (
                            <div key={job} className={styles.jobRow}>
                                <span className={styles.jobName}>{JOB_TITLE[job]}</span>
                                <span className={styles.jobValue}>
                                    {active.label}{" "}
                                    {active.label !== active.deployment && (
                                        <span className={styles.small}>· {active.deployment}</span>
                                    )}
                                </span>
                            </div>
                        );
                    })}
                    <a
                        href={AI_MODELS_PATH}
                        className={styles.manage}
                        onClick={(e) => {
                            e.preventDefault();
                            navigate(AI_MODELS_PATH);
                        }}
                    >
                        <span>Manage AI models</span>
                        <span className={styles.manageCount}>
                            {data.models.length} {data.models.length === 1 ? "model" : "models"}
                            <svg
                                width="20"
                                height="20"
                                viewBox="0 0 24 24"
                                fill="none"
                                stroke="currentColor"
                                strokeWidth="2.2"
                                strokeLinecap="round"
                                strokeLinejoin="round"
                                aria-hidden="true"
                            >
                                <path d="M9 5l7 7-7 7" />
                            </svg>
                        </span>
                    </a>
                </div>
            )}
        </section>
    );
}
