import { useEffect, useState } from "react";
import { SignInRequiredError, type ApiClient } from "./api";
import styles from "./App.module.css";
import { AddScreen } from "./AddScreen";
import { BottomNav } from "./BottomNav";
import { ItemScreen } from "./ItemScreen";
import { ListScreen } from "./ListScreen";
import { NameScreen } from "./NameScreen";
import { refreshPushSubscription } from "./push";
import { RecipesScreen } from "./RecipesScreen";
import {
    ADD_PATH,
    matchAssignPath,
    matchEditPath,
    matchItemPath,
    matchNamePath,
    LIST_PATH,
    RECIPES_PATH,
    SCANNERS_PATH,
    usePath,
} from "./router";
import { ScannersScreen } from "./ScannersScreen";
import { StockScreen } from "./StockScreen";
import { ShoppingListProvider } from "./useShoppingList";

/** The signed-in app: the stock list (with a muted footer for verification), item detail, naming or editing a
 * product, adding items, saying what a store label was, or recipe ideas (#73). */
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

    // If notifications are on for this device, tell the API it's still in use (#77). Never asks for permission.
    useEffect(() => {
        refreshPushSubscription(api).catch((error: unknown) => {
            if (!(error instanceof SignInRequiredError))
                console.warn("Couldn't refresh this device's notifications", error);
        });
    }, [api]);

    const naming = matchNamePath(path);
    const editing = matchEditPath(path);
    const item = matchItemPath(path);
    const assigning = matchAssignPath(path);
    return (
        <ShoppingListProvider api={api}>
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
                ) : path === LIST_PATH ? (
                    <>
                        <ListScreen api={api} />
                        <BottomNav current="list" />
                    </>
                ) : path === RECIPES_PATH ? (
                    <>
                        <RecipesScreen api={api} />
                        <BottomNav current="recipes" />
                    </>
                ) : path === SCANNERS_PATH ? (
                    <>
                        <ScannersScreen api={api} />
                        <BottomNav current="stock" />
                    </>
                ) : (
                    <>
                        <StockScreen api={api} />
                        <footer className={styles.footer}>{footer}</footer>
                        <BottomNav current="stock" />
                    </>
                )}
            </main>
        </ShoppingListProvider>
    );
}
