import { useEffect, useState } from "react";
import { SignInRequiredError, type ApiClient } from "./api";
import styles from "./App.module.css";
import { AddScreen } from "./AddScreen";
import { BottomNav } from "./BottomNav";
import { ItemScreen } from "./ItemScreen";
import { NameScreen } from "./NameScreen";
import {
    ADD_PATH,
    matchAssignPath,
    matchEditPath,
    matchItemPath,
    matchNamePath,
    SCANNERS_PATH,
    usePath,
} from "./router";
import { ScannersScreen } from "./ScannersScreen";
import { StockScreen } from "./StockScreen";

/** The signed-in app: the stock list (with a muted footer for verification), item detail, naming or editing a
 * product, adding items, or saying what a store label was. */
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
    const editing = matchEditPath(path);
    const item = matchItemPath(path);
    const assigning = matchAssignPath(path);
    return (
        <main className={styles.page}>
            {naming ? (
                <NameScreen key={naming} api={api} productId={naming} />
            ) : editing ? (
                <NameScreen key={`edit-${editing}`} api={api} productId={editing} mode="edit" />
            ) : item ? (
                <ItemScreen key={item} api={api} productId={item} />
            ) : path === ADD_PATH ? (
                <AddScreen api={api} />
            ) : assigning ? (
                <AddScreen key={assigning} api={api} assignScanId={assigning} />
            ) : path === SCANNERS_PATH ? (
                <>
                    <ScannersScreen api={api} />
                    <BottomNav current="scanners" />
                </>
            ) : (
                <>
                    <StockScreen api={api} />
                    <footer className={styles.footer}>{footer}</footer>
                    <BottomNav current="stock" />
                </>
            )}
        </main>
    );
}
