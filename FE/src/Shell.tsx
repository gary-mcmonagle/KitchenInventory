import { useEffect, useState } from "react";
import { SignInRequiredError, type ApiClient } from "./api";
import styles from "./App.module.css";
import { StockScreen } from "./StockScreen";

/** The signed-in app: the stock list, and a muted footer showing who's signed in and the versions. */
export function Shell({ api, buildSha }: { api: ApiClient; buildSha: string }) {
    const [footer, setFooter] = useState(`UI ${buildSha}`);

    useEffect(() => {
        let live = true;
        Promise.all([api.me(), api.ping()])
            .then(
                ([me, ping]) =>
                    live && setFooter(`Signed in as ${me.name} · API ${ping.commit} · UI ${buildSha}`),
            )
            .catch((error: unknown) => {
                if (!(error instanceof SignInRequiredError))
                    console.warn("Couldn't load the footer details", error);
            });
        return () => {
            live = false;
        };
    }, [api, buildSha]);

    return (
        <main className={styles.page}>
            <StockScreen api={api} />
            <footer className={styles.footer}>{footer}</footer>
        </main>
    );
}
