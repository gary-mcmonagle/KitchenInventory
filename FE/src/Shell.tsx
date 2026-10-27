import { useEffect, useLayoutEffect, useState } from "react";
import { SignInRequiredError, type ApiClient } from "./api";
import styles from "./App.module.css";
import { AddScreen } from "./AddScreen";
import { AiModelsScreen } from "./AiModelsScreen";
import { BottomNav } from "./BottomNav";
import { ItemScreen } from "./ItemScreen";
import { ListScreen } from "./ListScreen";
import { ModelTestScreen } from "./ModelTestScreen";
import { NameScreen } from "./NameScreen";
import { refreshPushSubscription } from "./push";
import { RecipesScreen } from "./RecipesScreen";
import {
    ADD_PATH,
    AI_MODEL_ADD_PATH,
    AI_MODELS_PATH,
    matchAiModelTestPath,
    matchAssignPath,
    matchEditPath,
    matchItemPath,
    matchNamePath,
    LIST_PATH,
    cameFrom,
    RECIPES_PATH,
    savedScroll,
    LEGACY_SCANNERS_PATH,
    SETTINGS_PATH,
    usePath,
    useTabResets,
} from "./router";
import { SettingsScreen } from "./SettingsScreen";
import { StockScreen } from "./StockScreen";
import { ShoppingListProvider } from "./useShoppingList";

/** The signed-in app: the stock list (with a muted footer for verification), item detail, naming or editing a
 * product, adding items, saying what a store label was, or recipe ideas (#73). */
export function Shell({ api, buildSha }: { api: ApiClient; buildSha: string }) {
    const path = usePath();
    const resets = useTabResets(); // tapping the tab you're on remounts its screen (Gary, 2026-10-22)
    const [footer, setFooter] = useState(`UI ${buildSha}`);

    // /scanners became /settings (#122): send old links and bookmarks there, keeping where we came from (for the back link).
    useLayoutEffect(() => {
        if (window.location.pathname !== LEGACY_SCANNERS_PATH) return;
        window.history.replaceState(
            window.history.state,
            "",
            SETTINGS_PATH + window.location.search + window.location.hash,
        );
        window.dispatchEvent(new PopStateEvent("popstate"));
    }, [path]);

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
    const aiModelTest = matchAiModelTestPath(path);
    const onStock = path === "/" || !matchKnownPath(path);
    // Screens reached from the stock list: the list stays mounted underneath, hidden, so going back keeps its data,
    // scroll, open aisles, search, Use first and live updates, with no reload (#103, spike #99 option A). The same for
    // Hungry? under item detail opened from a recipe.
    const overStock =
        !!(naming || editing || item || assigning || aiModelTest) ||
        [ADD_PATH, SETTINGS_PATH, AI_MODELS_PATH, AI_MODEL_ADD_PATH].includes(path);
    const onRecipes = path === RECIPES_PATH;
    const overRecipes = !!item && cameFrom() === RECIPES_PATH;
    useLayoutEffect(() => {
        window.history.scrollRestoration = "manual";
        window.scrollTo(0, onStock ? savedScroll("/") : onRecipes ? savedScroll(RECIPES_PATH) : 0);
    }, [onStock, onRecipes, path]);
    return (
        <ShoppingListProvider api={api}>
            <main className={styles.page}>
                {(onStock || overStock) && (
                    <div hidden={!onStock} className={styles.layer}>
                        <StockScreen key={resets} api={api} visible={onStock} />
                        <footer className={styles.footer}>{footer}</footer>
                        <BottomNav current="stock" />
                    </div>
                )}
                {(onRecipes || overRecipes) && (
                    <div hidden={!onRecipes} className={styles.layer}>
                        <RecipesScreen key={resets} api={api} />
                        <BottomNav current="recipes" />
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
                        <ListScreen key={resets} api={api} />
                        <BottomNav current="list" />
                    </>
                ) : aiModelTest ? (
                    <ModelTestScreen key={aiModelTest} api={api} modelId={aiModelTest} />
                ) : path === AI_MODEL_ADD_PATH ? (
                    <ModelTestScreen api={api} modelId={null} />
                ) : path === AI_MODELS_PATH ? (
                    <>
                        <AiModelsScreen api={api} />
                        <BottomNav current="stock" />
                    </>
                ) : path === SETTINGS_PATH ? (
                    <>
                        <SettingsScreen api={api} />
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
        !!(
            matchNamePath(path) ||
            matchEditPath(path) ||
            matchItemPath(path) ||
            matchAssignPath(path) ||
            matchAiModelTestPath(path)
        ) ||
        [ADD_PATH, LIST_PATH, RECIPES_PATH, SETTINGS_PATH, AI_MODELS_PATH, AI_MODEL_ADD_PATH].includes(path)
    );
}
