import { useEffect, useLayoutEffect, useState } from "react";
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
    savedScroll,
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
    const onStock = path === "/" || !matchKnownPath(path);
    // Screens reached from the stock list (spike #99 prototype): the list stays mounted underneath, hidden, so going
    // back keeps its data, open aisles, search, Use first and live updates, with no reload.
    const overStock =
        !!(naming || editing || item || assigning) || path === ADD_PATH || path === SCANNERS_PATH;
    useLayoutEffect(() => {
        window.history.scrollRestoration = "manual";
        window.scrollTo(0, onStock ? savedScroll("/") : 0);
    }, [onStock, path]);
    return (
        <ShoppingListProvider api={api}>
            <main className={styles.page}>
                {(onStock || overStock) && (
                    <div hidden={!onStock} className={styles.stockLayer}>
                        <StockScreen api={api} />
                        <footer className={styles.footer}>{footer}</footer>
                        <BottomNav current="stock" />
                    </div>
                )}
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
                ) : null}
            </main>
        </ShoppingListProvider>
    );
}

/** Every path with its own screen; anything else shows the stock list. */
function matchKnownPath(path: string): boolean {
    return (
        !!(matchNamePath(path) || matchEditPath(path) || matchItemPath(path) || matchAssignPath(path)) ||
        [ADD_PATH, LIST_PATH, RECIPES_PATH, SCANNERS_PATH].includes(path)
    );
}
