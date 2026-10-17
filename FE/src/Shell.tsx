import { useEffect, useState } from "react";
import { NoAccessError, SignInRequiredError, type ApiClient } from "./api";
import styles from "./App.module.css";

type Status =
    | { kind: "loading" }
    | { kind: "ready"; name: string; commit: string }
    | { kind: "noAccess"; message: string }
    | { kind: "error"; message: string };

const today = () =>
    new Date().toLocaleDateString("en-GB", { weekday: "long", day: "numeric", month: "long" });

/** The signed-in app: header, the (empty for now) Stock list, and a muted footer for verification. */
export function Shell({ api, buildSha }: { api: ApiClient; buildSha: string }) {
    const [status, setStatus] = useState<Status>({ kind: "loading" });

    useEffect(() => {
        let live = true;
        Promise.all([api.me(), api.ping()])
            .then(([me, ping]) => live && setStatus({ kind: "ready", name: me.name, commit: ping.commit }))
            .catch((error: unknown) => {
                if (!live || error instanceof SignInRequiredError) return;
                setStatus(
                    error instanceof NoAccessError
                        ? { kind: "noAccess", message: error.message }
                        : {
                              kind: "error",
                              message: "Couldn't reach Cupboard Tracker. Try again in a moment.",
                          },
                );
            });
        return () => {
            live = false;
        };
    }, [api]);

    return (
        <main className={styles.page}>
            <header className={styles.header}>
                <div className={styles.date}>{today()}</div>
                <h1 className={styles.title}>Cupboard</h1>
            </header>

            <section className={styles.content} aria-labelledby="stock-heading">
                <h2 id="stock-heading" className={styles.sectionTitle}>
                    Stock list
                </h2>
                {status.kind === "noAccess" ? (
                    <p className={styles.notice} role="alert">
                        {status.message}
                    </p>
                ) : status.kind === "error" ? (
                    <p className={styles.notice} role="alert">
                        {status.message}
                    </p>
                ) : (
                    <p className={styles.empty}>Your stock will appear here.</p>
                )}
            </section>

            <footer className={styles.footer}>
                {status.kind === "ready"
                    ? `Signed in as ${status.name} · API ${status.commit} · UI ${buildSha}`
                    : `UI ${buildSha}`}
            </footer>
        </main>
    );
}
