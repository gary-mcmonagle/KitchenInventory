import { useEffect, useState } from "react";
import { SignInRequiredError, type ApiClient } from "./api";
import styles from "./App.module.css";
import { NameScreen } from "./NameScreen";
import { matchNamePath, usePath } from "./router";
import { StockScreen } from "./StockScreen";

/** The signed-in app: the stock list (with a muted footer for verification), or naming a product. */
export function Shell({ api, buildSha }: { api: ApiClient; buildSha: string }) {
    const path = usePath();
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

    const naming = matchNamePath(path);
    return (
        <main className={styles.page}>
            {naming ? (
                <NameScreen key={naming} api={api} productId={naming} />
            ) : (
                <>
                    <StockScreen api={api} />
                    <footer className={styles.footer}>{footer}</footer>
                </>
            )}
        </main>
    );
}
