import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
    ApiError,
    SignInRequiredError,
    type AdjustmentSummary,
    type ApiClient,
    type Appliance,
    type QuickIdea,
    type Recipe,
    type RecipeMeal,
    type RecipeMode,
    type RecipeRequest,
    type SaveRecipeBody,
    type SavedRecipe,
    type StockProduct,
} from "./api";
import { expiryBadge } from "./format";
import { itemPath, navigate } from "./router";
import { AdjustSheet, adjustmentLine, type AdjustChoices } from "./RecipeAdjust";
import styles from "./Recipes.module.css";
import {
    SaveIdeaButton,
    SavePill,
    SavedBox,
    SavedRecipeView,
    ideaToSave,
    recipeToSave,
    savedToRecipe,
    savedToSave,
} from "./SavedRecipes";
import savedStyles from "./SavedRecipes.module.css";
import { newId } from "./shoppingList";
import { TOAST_MS } from "./StockScreen";

const MEALS: Array<{ id: RecipeMeal; label: string }> = [
    { id: "breakfast", label: "Breakfast" },
    { id: "lunch", label: "Lunch" },
    { id: "dinner", label: "Dinner" },
    { id: "snack", label: "Snack" },
];

const MODES: Array<{ id: RecipeMode; label: string; sub: string }> = [
    { id: "cook", label: "Cook something", sub: "A proper recipe" },
    { id: "readyNow", label: "Ready now", sub: "Toaster, microwave, kettle" },
];

/** The last mode chosen on this device (#88). Storage can be missing or throw (private mode): then "cook". */
export const MODE_KEY = "hungry.mode";

function storedMode(): RecipeMode {
    try {
        return localStorage.getItem(MODE_KEY) === "readyNow" ? "readyNow" : "cook";
    } catch {
        return "cook";
    }
}

function rememberMode(mode: RecipeMode) {
    try {
        localStorage.setItem(MODE_KEY, mode);
    } catch {
        // Not remembered; nothing else changes.
    }
}

export const MAX_COMMENTS = 300;
const COUNT_FROM = 250;
const UNAVAILABLE = "Couldn't come up with a recipe just now. Try again.";
const ADJUST_UNAVAILABLE = "Couldn't update the recipe just now. Try again.";

/** The API's friendly message (e.g. over the hourly limit), when it sent one. */
function errorMessage(error: unknown): string | null {
    return error instanceof ApiError &&
        typeof error.body === "object" &&
        error.body !== null &&
        "error" in error.body &&
        typeof error.body.error === "string"
        ? error.body.error
        : null;
}

/** The meal for the time of day in the UK (#88): before 11:00 breakfast, to 14:30 lunch, to 17:00 a snack, then
 * dinner. */
export function defaultMeal(now: Date = new Date()): RecipeMeal {
    const [hour, minute] = new Intl.DateTimeFormat("en-GB", {
        hour: "2-digit",
        minute: "2-digit",
        hourCycle: "h23",
        timeZone: "Europe/London",
    })
        .format(now)
        .split(":")
        .map(Number);
    const minutes = hour * 60 + minute;
    return minutes < 11 * 60
        ? "breakfast"
        : minutes < 14 * 60 + 30
          ? "lunch"
          : minutes < 17 * 60
            ? "snack"
            : "dinner";
}

const mealLabel = (meal: RecipeMeal) => MEALS.find((m) => m.id === meal)!.label;

/** "Hummus · Est. expired", with the badge's tone. */
function badge(name: string, days: number | null, date: string | null): { text: string; tone: string } {
    if (days === null || date === null) return { text: name, tone: styles.soon };
    const b = expiryBadge(days, date);
    return { text: `${name} · ${b.text}`, tone: b.tone === "expired" ? styles.expired : styles.soon };
}

interface Choices {
    mode: RecipeMode;
    meal: RecipeMeal;
    prefer: boolean;
    picked: string[];
    comments: string;
}

type Phase =
    | { kind: "form" }
    | { kind: "loading" }
    | { kind: "recipe"; recipe: Recipe }
    /** Ready now ideas (#88); each card adjusts on its own (#108). `round` tells a late answer it's out of date. */
    | { kind: "ideas"; meal: RecipeMeal; cards: IdeaCard[]; round: number }
    | { kind: "error"; message: string; retry: RecipeRequest }
    /** A saved recipe (#105); `asRecipe` is it as the recipe Adjust works on, `saved` false after Remove. */
    | { kind: "saved"; recipe: SavedRecipe; asRecipe: Recipe; saved: boolean };

/** One Ready now card (#108): the idea now, the versions before it (each Undo steps back once), what the last change
 * was, and whether it's updating or failed (with the choices, for Try again). */
type IdeaCard = {
    idea: QuickIdea;
    history: Array<{ idea: QuickIdea; summary: AdjustmentSummary | null }>;
    summary: AdjustmentSummary | null;
    status: "idle" | "updating" | { message: string; choices: AdjustChoices };
};

const IDEA_UNAVAILABLE = "Couldn't update this idea just now. Try again.";

/** An idea as the Adjust sheet sees it: its uses as the ingredients (so Remove lists them), its how as the step. */
export function ideaAsRecipe(idea: QuickIdea, meal: RecipeMeal): Recipe {
    return {
        title: idea.title,
        summary: idea.how,
        meal,
        servings: 1,
        totalMinutes: idea.minutes,
        ingredients: idea.uses.map((u) => ({
            name: u.displayName,
            amount: "",
            productId: u.productId,
            useFirst: u.useFirst,
        })),
        steps: [idea.how],
        usesUp: [],
        alsoNeeded: idea.alsoNeeded ? [idea.alsoNeeded] : [],
        couldNotFitIn: [],
    };
}

/** What's on screen that can be saved: a recipe, or a Ready now idea. */
type Saveable = Recipe | QuickIdea;

/**
 * Hungry? (#73, #88): the form (project/Hungry.dc.html), then a recipe (project/Recipe.dc.html) or Ready now ideas
 * (project/ReadyNow.dc.html). Built from the stock on the server; the choices, comments, results and titles shown live
 * only here, so leaving the screen starts again with an empty form (only the mode is remembered on the device).
 */
export function RecipesScreen({ api, now }: { api: ApiClient; now?: Date }) {
    const [stock, setStock] = useState<StockProduct[] | null>(null);
    const [stockFailed, setStockFailed] = useState(false);
    const [choices, setChoices] = useState<Choices>(() => ({
        mode: storedMode(),
        meal: defaultMeal(now),
        prefer: false,
        picked: [],
        comments: "",
    }));
    const [phase, setPhase] = useState<Phase>({ kind: "form" });
    const [shownTitles, setShownTitles] = useState<string[]>([]);
    // Adjust (#104): earlier versions of the recipe (each Undo steps back once), what the last change was, and the sheet.
    const [history, setHistory] = useState<Array<{ recipe: Recipe; summary: AdjustmentSummary | null }>>([]);
    const [summary, setSummary] = useState<AdjustmentSummary | null>(null);
    const [adjust, setAdjust] = useState<
        | { kind: "sheet" }
        | { kind: "updating" }
        | { kind: "error"; message: string; choices: AdjustChoices }
        | null
    >(null);
    const resetAdjust = () => {
        setHistory([]);
        setSummary(null);
        setAdjust(null);
    };
    const top = useRef<HTMLDivElement>(null);

    // Saved recipes (#105): the household's list, which shown recipes and ideas are saved (by object, while in memory),
    // whether the Saved box is open (the form dims), and the toast with Undo.
    const [saved, setSaved] = useState<SavedRecipe[] | null>(null);
    const [savedFailed, setSavedFailed] = useState(false);
    const [savedIds, setSavedIds] = useState<Map<Saveable, string>>(() => new Map());
    const [boxOpen, setBoxOpen] = useState(false);
    const [toast, setToast] = useState<{ text: string; undo?: () => void } | null>(null);
    const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    const showToast = (text: string, undo?: () => void) => {
        clearTimeout(toastTimer.current);
        setToast({ text, undo });
        toastTimer.current = setTimeout(() => setToast(null), TOAST_MS);
    };
    useEffect(() => () => clearTimeout(toastTimer.current), []);

    const loadSaved = useCallback(() => {
        api.savedRecipes()
            .then((s) => {
                setSaved(s.savedRecipes);
                setSavedFailed(false);
            })
            .catch((error: unknown) => {
                if (error instanceof SignInRequiredError) return;
                console.warn("Couldn't load the saved recipes", error);
                setSavedFailed(true);
            });
    }, [api]);
    useEffect(loadSaved, [loadSaved]);
    const onBoxOpen = useCallback(
        (open: boolean) => {
            setBoxOpen(open);
            if (open) loadSaved(); // someone else at home may have saved one
        },
        [loadSaved],
    );

    const remember = (item: Saveable, id: string | null) =>
        setSavedIds((m) => {
            const next = new Map(m);
            if (id) next.set(item, id);
            else next.delete(item);
            return next;
        });
    const upsert = (recipe: SavedRecipe) =>
        setSaved((list) => [recipe, ...(list ?? []).filter((r) => r.id !== recipe.id)]);
    const drop = (id: string) => setSaved((list) => (list ?? []).filter((r) => r.id !== id));

    /** Unsaves by id; resolves false when it couldn't be. */
    const unsave = async (id: string): Promise<boolean> => {
        try {
            await api.deleteSavedRecipe(id);
        } catch (error) {
            if (!(error instanceof ApiError && error.status === 404)) {
                if (!(error instanceof SignInRequiredError)) showToast("Couldn't remove that. Try again.");
                return false;
            }
        }
        drop(id);
        return true;
    };

    /** Saves a recipe or idea as shown: a snapshot (no Foundry call), with Undo. */
    const save = async (item: Saveable, body: SaveRecipeBody) => {
        const id = newId();
        remember(item, id);
        try {
            upsert(await api.saveRecipe(id, body));
            showToast("Saved to Hungry?", () => {
                setToast(null);
                void unsaveItem(item, id, body, false);
            });
        } catch (error) {
            remember(item, null);
            if (!(error instanceof SignInRequiredError)) showToast("Couldn't save that. Try again.");
        }
    };

    const unsaveItem = async (item: Saveable, id: string, body: SaveRecipeBody, offerUndo: boolean) => {
        remember(item, null);
        if (!(await unsave(id))) {
            remember(item, id);
        } else if (offerUndo) {
            showToast("Removed from saved", () => {
                setToast(null);
                void save(item, body);
            });
        }
    };

    const toggleSave = (item: Saveable, body: SaveRecipeBody) => {
        const existing = savedIds.get(item);
        void (existing ? unsaveItem(item, existing, body, true) : save(item, body));
    };

    const openSaved = (recipe: SavedRecipe) => {
        resetAdjust();
        const asRecipe = savedToRecipe(recipe);
        remember(asRecipe, recipe.id); // adjusting it and then undoing comes back to the saved version
        setPhase({ kind: "saved", recipe, asRecipe, saved: true });
    };

    /** Remove from saved, or Save again (the same id and snapshot, so it's also Undo). */
    const toggleSavedView = async (recipe: SavedRecipe, isSaved: boolean) => {
        const setSavedFlag = (value: boolean) =>
            setPhase((p) => (p.kind === "saved" && p.recipe.id === recipe.id ? { ...p, saved: value } : p));
        if (isSaved) {
            setSavedFlag(false);
            if (await unsave(recipe.id)) {
                showToast("Removed from saved", () => {
                    setToast(null);
                    void toggleSavedView(recipe, false);
                });
            } else {
                setSavedFlag(true);
            }
            return;
        }
        setSavedFlag(true);
        try {
            const back = await api.saveRecipe(recipe.id, savedToSave(recipe));
            upsert(back);
            setPhase((p) => (p.kind === "saved" && p.recipe.id === recipe.id ? { ...p, recipe: back } : p));
        } catch (error) {
            setSavedFlag(false);
            if (!(error instanceof SignInRequiredError)) showToast("Couldn't save that. Try again.");
        }
    };

    const updateSaved = async (recipe: SavedRecipe, change: { title?: string; note?: string }) => {
        try {
            const updated = await api.updateSavedRecipe(recipe.id, change);
            setSaved((list) => (list ?? []).map((r) => (r.id === updated.id ? updated : r)));
            setPhase((p) =>
                p.kind === "saved" && p.recipe.id === updated.id ? { ...p, recipe: updated } : p,
            );
            return true;
        } catch (error) {
            return error instanceof SignInRequiredError;
        }
    };

    const loadStock = () => {
        setStockFailed(false);
        api.stock()
            .then((s) => {
                const known = s.products.filter((p) => p.status === "known");
                setStock(known);
                setChoices((c) => ({ ...c, prefer: known.some((p) => p.useFirst) }));
            })
            .catch((error: unknown) => {
                if (error instanceof SignInRequiredError) return;
                console.warn("Couldn't load the stock for recipes", error);
                setStockFailed(true);
            });
    };
    useEffect(loadStock, [api]);

    useEffect(() => {
        top.current?.scrollIntoView?.({ block: "start" });
    }, [phase.kind]);

    const ask = async (request: RecipeRequest) => {
        resetAdjust(); // a new idea: adjustments aren't carried over
        setPhase({ kind: "loading" });
        try {
            const answer = await api.suggestRecipe(request);
            const titles = answer.recipe ? [answer.recipe.title] : (answer.ideas ?? []).map((i) => i.title);
            if (!answer.available || titles.length === 0) {
                setPhase({ kind: "error", message: answer.message ?? UNAVAILABLE, retry: request });
                return;
            }
            setShownTitles([...request.avoidTitles, ...titles].filter((t, i, all) => all.indexOf(t) === i));
            setPhase(
                answer.recipe
                    ? { kind: "recipe", recipe: answer.recipe }
                    : {
                          kind: "ideas",
                          meal: answer.meal ?? request.meal,
                          cards: answer.ideas!.map((idea) => ({
                              idea,
                              history: [],
                              summary: null,
                              status: "idle",
                          })),
                          round: ++ideaRound.current,
                      },
            );
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            setPhase({ kind: "error", message: errorMessage(error) ?? UNAVAILABLE, retry: request });
        }
    };

    // Adjusting a Ready now idea (#108): one card at a time in the sheet; each card updates on its own.
    const ideaRound = useRef(0);
    const [ideaSheet, setIdeaSheet] = useState<{ round: number; index: number; recipe: Recipe } | null>(null);
    const updateCard = (round: number, index: number, change: (card: IdeaCard) => IdeaCard) =>
        setPhase((p) =>
            p.kind === "ideas" && p.round === round
                ? { ...p, cards: p.cards.map((c, i) => (i === index ? change(c) : c)) }
                : p,
        );

    const runIdeaAdjust = async (
        round: number,
        index: number,
        card: IdeaCard,
        meal: RecipeMeal,
        choices: AdjustChoices,
    ) => {
        updateCard(round, index, (c) => ({ ...c, status: "updating" }));
        const failed = (message: string) =>
            updateCard(round, index, (c) => ({ ...c, status: { message, choices } }));
        try {
            const { idea } = card;
            const answer = await api.adjustIdea({
                idea: {
                    title: idea.title,
                    how: idea.how,
                    appliance: idea.appliance,
                    minutes: idea.minutes,
                    uses: idea.uses.map((u) => ({ productId: u.productId, displayName: u.displayName })),
                    alsoNeeded: idea.alsoNeeded,
                },
                meal,
                add: { productIds: choices.productIds, typed: choices.typed },
                remove: choices.remove,
                tweak: choices.tweak,
            });
            if (!answer.available || !answer.idea) {
                failed(answer.message ?? IDEA_UNAVAILABLE);
                return;
            }
            const adjusted = answer.idea;
            setShownTitles((t) => (t.includes(adjusted.title) ? t : [...t, adjusted.title])); // More ideas avoids it too
            updateCard(round, index, (c) => ({
                idea: adjusted,
                history: [...c.history, { idea: c.idea, summary: c.summary }],
                summary: answer.adjustmentSummary ?? null,
                status: "idle",
            }));
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            failed(errorMessage(error) ?? IDEA_UNAVAILABLE);
        }
    };

    const undoIdea = (round: number, index: number) =>
        updateCard(round, index, (c) => {
            const previous = c.history[c.history.length - 1];
            return previous
                ? {
                      idea: previous.idea,
                      summary: previous.summary,
                      history: c.history.slice(0, -1),
                      status: "idle",
                  }
                : c;
        });

    const runAdjust = async (recipe: Recipe, choices: AdjustChoices) => {
        setAdjust({ kind: "updating" });
        try {
            const answer = await api.adjustRecipe({
                recipe,
                add: { productIds: choices.productIds, typed: choices.typed },
                remove: choices.remove,
                tweak: choices.tweak,
            });
            if (!answer.available || !answer.recipe) {
                setAdjust({ kind: "error", message: answer.message ?? ADJUST_UNAVAILABLE, choices });
                return;
            }
            setHistory((h) => [...h, { recipe, summary }]);
            setSummary(answer.adjustmentSummary ?? null);
            setPhase({ kind: "recipe", recipe: answer.recipe });
            setAdjust(null);
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            setAdjust({ kind: "error", message: errorMessage(error) ?? ADJUST_UNAVAILABLE, choices });
        }
    };

    const undo = () => {
        const previous = history[history.length - 1];
        if (!previous) return;
        setHistory((h) => h.slice(0, -1));
        setSummary(previous.summary);
        setPhase({ kind: "recipe", recipe: previous.recipe });
        setAdjust(null);
    };

    const request = (avoidTitles: string[]): RecipeRequest => ({
        mode: choices.mode,
        meal: choices.meal,
        preferUseFirst: choices.prefer,
        includeProductIds: choices.picked,
        comments: choices.comments.trim() || null,
        avoidTitles,
    });

    return (
        <div className={styles.screen} ref={top}>
            {phase.kind === "recipe" ? (
                <>
                    <RecipeView
                        recipe={phase.recipe}
                        previous={history[history.length - 1]?.recipe ?? null}
                        summary={summary}
                        updating={adjust?.kind === "updating"}
                        adjustError={adjust?.kind === "error" ? adjust.message : null}
                        onRetryAdjust={() =>
                            adjust?.kind === "error" && void runAdjust(phase.recipe, adjust.choices)
                        }
                        onAdjust={() => setAdjust({ kind: "sheet" })}
                        saved={savedIds.has(phase.recipe)}
                        onToggleSave={() => toggleSave(phase.recipe, recipeToSave(phase.recipe))}
                        onUndo={history.length > 0 ? undo : null}
                        onAnother={() => void ask(request(shownTitles))}
                        onStartAgain={() => {
                            resetAdjust();
                            setPhase({ kind: "form" });
                        }}
                    />
                    {adjust?.kind === "sheet" && (
                        <AdjustSheet
                            api={api}
                            recipe={phase.recipe}
                            stock={stock ?? []}
                            onCancel={() => setAdjust(null)}
                            onUpdate={(choices) => void runAdjust(phase.recipe, choices)}
                        />
                    )}
                </>
            ) : phase.kind === "ideas" ? (
                <>
                    <IdeasView
                        meal={phase.meal}
                        cards={phase.cards}
                        preferUseFirst={choices.prefer}
                        isSaved={(idea) => savedIds.has(idea)}
                        onToggleSave={(idea) => toggleSave(idea, ideaToSave(idea, phase.meal))}
                        onAdjust={(index) =>
                            setIdeaSheet({
                                round: phase.round,
                                index,
                                recipe: ideaAsRecipe(phase.cards[index].idea, phase.meal),
                            })
                        }
                        onRetry={(index) => {
                            const card = phase.cards[index];
                            if (typeof card.status === "object")
                                void runIdeaAdjust(phase.round, index, card, phase.meal, card.status.choices);
                        }}
                        onUndo={(index) => undoIdea(phase.round, index)}
                        onMore={() => void ask(request(shownTitles))}
                        onStartAgain={() => setPhase({ kind: "form" })}
                    />
                    {ideaSheet?.round === phase.round && (
                        <AdjustSheet
                            api={api}
                            kind="idea"
                            recipe={ideaSheet.recipe}
                            stock={stock ?? []}
                            onCancel={() => setIdeaSheet(null)}
                            onUpdate={(choices) => {
                                setIdeaSheet(null);
                                void runIdeaAdjust(
                                    phase.round,
                                    ideaSheet.index,
                                    phase.cards[ideaSheet.index],
                                    phase.meal,
                                    choices,
                                );
                            }}
                        />
                    )}
                </>
            ) : phase.kind === "saved" ? (
                <>
                    <SavedRecipeView
                        recipe={phase.recipe}
                        saved={phase.saved}
                        now={now}
                        updating={adjust?.kind === "updating"}
                        adjustError={adjust?.kind === "error" ? adjust.message : null}
                        onRetryAdjust={() =>
                            adjust?.kind === "error" && void runAdjust(phase.asRecipe, adjust.choices)
                        }
                        onBack={() => {
                            resetAdjust();
                            setPhase({ kind: "form" });
                        }}
                        onToggleSaved={() => void toggleSavedView(phase.recipe, phase.saved)}
                        onUpdate={(change) => updateSaved(phase.recipe, change)}
                        onAdjust={phase.recipe.kind === "recipe" ? () => setAdjust({ kind: "sheet" }) : null}
                    />
                    {adjust?.kind === "sheet" && (
                        <AdjustSheet
                            api={api}
                            recipe={phase.asRecipe}
                            stock={stock ?? []}
                            onCancel={() => setAdjust(null)}
                            onUpdate={(choices) => void runAdjust(phase.asRecipe, choices)}
                        />
                    )}
                </>
            ) : (
                <>
                    <header className={styles.header}>
                        <h1 className={styles.title}>Hungry?</h1>
                        <div className={styles.subtitle}>Ideas built from what&apos;s in your cupboard.</div>
                    </header>
                    {phase.kind === "loading" ? (
                        <div className={styles.loading} role="status">
                            <span className={styles.spinner} aria-hidden="true" />
                            Thinking up something with what you&apos;ve got…
                        </div>
                    ) : (
                        <div className={styles.content}>
                            <SavedBox
                                saved={saved}
                                failed={savedFailed}
                                now={now}
                                onOpenChange={onBoxOpen}
                                onChoose={openSaved}
                            />
                            <div
                                className={
                                    boxOpen
                                        ? `${savedStyles.formArea} ${savedStyles.dimmed}`
                                        : savedStyles.formArea
                                }
                            >
                                {phase.kind === "error" && (
                                    <div className={styles.errorCard} role="alert">
                                        <span>{phase.message}</span>
                                        <button
                                            type="button"
                                            className={styles.tryAgain}
                                            onClick={() => void ask(phase.retry)}
                                        >
                                            Try again
                                        </button>
                                    </div>
                                )}
                                {stockFailed ? (
                                    <div className={styles.errorCard} role="alert">
                                        <span>Couldn&apos;t load what&apos;s in stock.</span>
                                        <button type="button" className={styles.tryAgain} onClick={loadStock}>
                                            Try again
                                        </button>
                                    </div>
                                ) : (
                                    <RecipeForm
                                        stock={stock}
                                        choices={choices}
                                        onChange={(update) =>
                                            setChoices((c) => {
                                                const next = update(c);
                                                if (next.mode !== c.mode) rememberMode(next.mode);
                                                return next;
                                            })
                                        }
                                        onSubmit={() => {
                                            setShownTitles([]);
                                            void ask(request([]));
                                        }}
                                    />
                                )}
                            </div>
                        </div>
                    )}
                </>
            )}
            {toast && (
                <div className={savedStyles.toast} role="status">
                    <span>{toast.text}</span>
                    {toast.undo && (
                        <button type="button" className={savedStyles.undo} onClick={toast.undo}>
                            Undo
                        </button>
                    )}
                </div>
            )}
        </div>
    );
}

function RecipeForm({
    stock,
    choices,
    onChange,
    onSubmit,
}: {
    stock: StockProduct[] | null;
    choices: Choices;
    onChange: (update: (c: Choices) => Choices) => void;
    onSubmit: () => void;
}) {
    const useFirst = (stock ?? []).filter((p) => p.useFirst);
    const empty = stock !== null && stock.length === 0;
    const ready = choices.mode === "readyNow";
    const things = `${useFirst.length} ${useFirst.length === 1 ? "thing" : "things"}`;
    return (
        <form
            className={styles.form}
            onSubmit={(e) => {
                e.preventDefault();
                onSubmit();
            }}
        >
            <div role="radiogroup" aria-label="What kind of food" className={styles.modes}>
                {MODES.map((m) => (
                    <button
                        key={m.id}
                        type="button"
                        role="radio"
                        aria-checked={choices.mode === m.id}
                        className={styles.mode}
                        onClick={() => onChange((c) => ({ ...c, mode: m.id }))}
                    >
                        <span className={styles.modeLabel}>{m.label}</span>
                        <span className={styles.modeSub}>{m.sub}</span>
                    </button>
                ))}
            </div>

            <div className={styles.group}>
                <div id="meal-label" className={styles.label}>
                    Meal
                </div>
                <div role="radiogroup" aria-labelledby="meal-label" className={styles.meals}>
                    {MEALS.map((m) => (
                        <button
                            key={m.id}
                            type="button"
                            role="radio"
                            aria-checked={choices.meal === m.id}
                            className={styles.meal}
                            onClick={() => onChange((c) => ({ ...c, meal: m.id }))}
                        >
                            {m.label}
                        </button>
                    ))}
                </div>
            </div>

            <div className={styles.preferCard}>
                <label className={styles.preferLabel}>
                    <input
                        type="checkbox"
                        className={styles.checkbox}
                        checked={choices.prefer && useFirst.length > 0}
                        disabled={useFirst.length === 0}
                        onChange={(e) => onChange((c) => ({ ...c, prefer: e.target.checked }))}
                    />
                    <span className={styles.preferText}>Prefer ingredients that need using</span>
                </label>
                <div className={styles.helper}>
                    {useFirst.length === 0
                        ? "Nothing needs using up right now"
                        : ready
                          ? `Ideas using the ${things} to use first come first.`
                          : `Builds the recipe around the ${things} to use first.`}
                </div>
                {useFirst.length > 0 && (
                    <div className={styles.pills}>
                        {useFirst.map((p) => {
                            const b = badge(p.displayName, p.daysUntilSoonest, p.soonestEstimatedExpiryDate);
                            return (
                                <span key={p.productId} className={`${styles.pill} ${b.tone}`}>
                                    {b.text}
                                </span>
                            );
                        })}
                    </div>
                )}
            </div>

            <IngredientPicker
                stock={stock ?? []}
                picked={choices.picked}
                onChange={(picked) => onChange((c) => ({ ...c, picked }))}
            />

            <div className={styles.group}>
                <div className={styles.labelRow}>
                    <label htmlFor="recipe-comments" className={styles.label}>
                        Additional comments
                    </label>
                    <span className={styles.optional}>Optional</span>
                </div>
                <textarea
                    id="recipe-comments"
                    className={styles.comments}
                    rows={3}
                    maxLength={MAX_COMMENTS}
                    placeholder={
                        ready
                            ? "e.g. something sweet, no bread"
                            : "e.g. something spicy, no oven, ready in 20 minutes"
                    }
                    value={choices.comments}
                    onChange={(e) => {
                        const comments = e.target.value.slice(0, MAX_COMMENTS);
                        onChange((c) => ({ ...c, comments }));
                    }}
                />
                {choices.comments.length > COUNT_FROM && (
                    <div className={styles.count} aria-live="polite">
                        {choices.comments.length} / {MAX_COMMENTS}
                    </div>
                )}
            </div>

            {empty && <p className={styles.note}>There&apos;s nothing in stock to cook with yet.</p>}
            <button type="submit" className={styles.primary} disabled={stock === null || empty}>
                {ready ? "Show me ideas" : "Get a recipe"}
            </button>
        </form>
    );
}

/** "Include ingredients": a searchable multi-select of the known products in stock, picked ones as chips. */
function IngredientPicker({
    stock,
    picked,
    onChange,
}: {
    stock: StockProduct[];
    picked: string[];
    onChange: (picked: string[]) => void;
}) {
    const [open, setOpen] = useState(false);
    const [query, setQuery] = useState("");
    const box = useRef<HTMLDivElement>(null);

    useEffect(() => {
        if (!open) return;
        const outside = (e: PointerEvent) => {
            if (box.current && !box.current.contains(e.target as Node)) setOpen(false);
        };
        document.addEventListener("pointerdown", outside);
        return () => document.removeEventListener("pointerdown", outside);
    }, [open]);

    const sorted = useMemo(
        () =>
            [...stock].sort((a, b) =>
                a.displayName.localeCompare(b.displayName, "en-GB", { sensitivity: "base" }),
            ),
        [stock],
    );
    const q = query.trim().toLowerCase();
    const options = sorted.filter((p) => !q || p.displayName.toLowerCase().includes(q));
    const chips = picked.map((id) => stock.find((p) => p.productId === id)).filter((p) => p !== undefined);
    const toggle = (id: string) =>
        onChange(picked.includes(id) ? picked.filter((x) => x !== id) : [...picked, id]);

    return (
        <div className={styles.group}>
            <div className={styles.labelRow}>
                <label htmlFor="ingredient-search" className={styles.label}>
                    Include ingredients
                </label>
                <span className={styles.optional}>Optional</span>
            </div>
            {chips.length > 0 && (
                <div className={styles.chips}>
                    {chips.map((p) => (
                        <span key={p.productId} className={styles.chip}>
                            {p.displayName}
                            <button
                                type="button"
                                className={styles.chipRemove}
                                aria-label={`Remove ${p.displayName}`}
                                onClick={() => toggle(p.productId)}
                            >
                                <svg
                                    width="16"
                                    height="16"
                                    viewBox="0 0 24 24"
                                    fill="none"
                                    stroke="currentColor"
                                    strokeWidth="2.4"
                                    strokeLinecap="round"
                                    aria-hidden="true"
                                >
                                    <path d="M6 6l12 12M18 6L6 18" />
                                </svg>
                            </button>
                        </span>
                    ))}
                </div>
            )}
            <div className={styles.picker} ref={box}>
                <div className={styles.search}>
                    <svg
                        width="20"
                        height="20"
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        strokeWidth="2"
                        strokeLinecap="round"
                        aria-hidden="true"
                    >
                        <circle cx="11" cy="11" r="7" />
                        <path d="M20 20l-3.5-3.5" />
                    </svg>
                    <input
                        id="ingredient-search"
                        type="search"
                        role="combobox"
                        aria-expanded={open}
                        aria-controls="ingredient-list"
                        autoComplete="off"
                        className={styles.searchInput}
                        placeholder="Add something you want in it"
                        value={query}
                        onFocus={() => setOpen(true)}
                        onChange={(e) => {
                            setQuery(e.target.value);
                            setOpen(true);
                        }}
                        onKeyDown={(e) => {
                            if (e.key === "Escape") setOpen(false);
                        }}
                    />
                </div>
                {open && (
                    <ul
                        id="ingredient-list"
                        role="listbox"
                        aria-multiselectable="true"
                        aria-label="Ingredients in stock"
                        className={styles.list}
                        onKeyDown={(e) => {
                            if (e.key === "Escape") setOpen(false);
                        }}
                    >
                        {options.length === 0 && (
                            <li className={styles.noMatch}>Nothing in stock matches.</li>
                        )}
                        {options.map((p) => {
                            const selected = picked.includes(p.productId);
                            return (
                                <li
                                    key={p.productId}
                                    role="option"
                                    aria-selected={selected}
                                    className={styles.option}
                                >
                                    <button
                                        type="button"
                                        className={styles.optionButton}
                                        onClick={() => toggle(p.productId)}
                                    >
                                        <span className={styles.box} aria-hidden="true">
                                            {selected && (
                                                <svg
                                                    width="14"
                                                    height="14"
                                                    viewBox="0 0 24 24"
                                                    fill="none"
                                                    stroke="currentColor"
                                                    strokeWidth="3"
                                                    strokeLinecap="round"
                                                    strokeLinejoin="round"
                                                >
                                                    <path d="M5 12.5l4.5 4.5L19 7.5" />
                                                </svg>
                                            )}
                                        </span>
                                        <span className={styles.optionName}>{p.displayName}</span>
                                        {p.useFirst && <span className={styles.useFirstTag}>Use first</span>}
                                    </button>
                                </li>
                            );
                        })}
                    </ul>
                )}
            </div>
        </div>
    );
}

function RecipeView({
    recipe,
    previous = null,
    summary = null,
    updating = false,
    adjustError = null,
    onRetryAdjust,
    onAdjust,
    onUndo = null,
    saved = false,
    onToggleSave,
    onAnother,
    onStartAgain,
}: {
    recipe: Recipe;
    /** Saved recipes (#105): the Save pill, filled when this version is saved. */
    saved?: boolean;
    onToggleSave?: () => void;
    /** The version before the last adjustment: its new ingredients get the "Added" tag. */
    previous?: Recipe | null;
    summary?: AdjustmentSummary | null;
    updating?: boolean;
    adjustError?: string | null;
    onRetryAdjust?: () => void;
    onAdjust?: () => void;
    onUndo?: (() => void) | null;
    onAnother: () => void;
    onStartAgain: () => void;
}) {
    const before = new Set((previous?.ingredients ?? []).map((i) => i.name.trim().toLowerCase()));
    const isNew = (name: string) => previous !== null && !before.has(name.trim().toLowerCase());
    const open = (productId: string) => (e: React.MouseEvent) => {
        e.preventDefault();
        navigate(itemPath(productId));
    };
    return (
        <>
            <div
                className={updating ? `${styles.recipe} ${styles.updating}` : styles.recipe}
                aria-busy={updating}
            >
                {updating && (
                    <div className={styles.updatingNote} role="status">
                        <span className={styles.smallSpinner} aria-hidden="true" />
                        Updating your recipe…
                    </div>
                )}
                {adjustError && (
                    <div className={styles.errorCard} role="alert">
                        <span>{adjustError}</span>
                        <button type="button" className={styles.tryAgain} onClick={onRetryAdjust}>
                            Try again
                        </button>
                    </div>
                )}
                <div className={styles.recipeHead}>
                    <div className={styles.kickerRow}>
                        <div className={styles.kicker}>
                            {mealLabel(recipe.meal)} · Serves {recipe.servings} · {recipe.totalMinutes} min
                        </div>
                        {onToggleSave && <SavePill saved={saved} onClick={onToggleSave} />}
                    </div>
                    {summary && onUndo && (
                        <div className={styles.adjusted}>
                            <span className={styles.adjustedText}>{adjustmentLine(summary)}</span>
                            <button type="button" className={styles.undoAdjust} onClick={onUndo}>
                                Undo adjustment
                            </button>
                        </div>
                    )}
                    <h1 className={styles.recipeTitle}>{recipe.title}</h1>
                    {recipe.summary && <div className={styles.summary}>{recipe.summary}</div>}
                </div>

                {recipe.usesUp.length > 0 && (
                    <section className={styles.usesUp} aria-label="Uses up">
                        <div className={styles.label}>Uses up</div>
                        <div className={styles.pills}>
                            {recipe.usesUp.map((u) => {
                                const b = badge(
                                    u.displayName,
                                    u.daysUntilSoonest,
                                    u.soonestEstimatedExpiryDate,
                                );
                                return (
                                    <span
                                        key={u.productId}
                                        className={`${styles.pill} ${styles.pillLarge} ${b.tone}`}
                                    >
                                        {b.text}
                                    </span>
                                );
                            })}
                        </div>
                    </section>
                )}

                <section className={styles.card}>
                    <h2 className={styles.label}>Ingredients</h2>
                    <ul className={styles.ingredients}>
                        {recipe.ingredients.map((i, index) => (
                            <li key={`${i.productId ?? "staple"}-${index}`} className={styles.ingredient}>
                                <span
                                    className={i.useFirst ? `${styles.dot} ${styles.dotOn}` : styles.dot}
                                    aria-hidden="true"
                                />
                                <span className={styles.amount}>{i.amount}</span>
                                {i.productId ? (
                                    <a
                                        href={itemPath(i.productId)}
                                        className={styles.ingredientLink}
                                        onClick={open(i.productId)}
                                    >
                                        {i.name}
                                        {i.useFirst && <span className={styles.srOnly}> (use first)</span>}
                                    </a>
                                ) : (
                                    <span className={styles.ingredientName}>{i.name}</span>
                                )}
                                {isNew(i.name) && <span className={styles.addedTag}>Added</span>}
                            </li>
                        ))}
                    </ul>
                    {recipe.couldNotFitIn.length > 0 && (
                        <p className={styles.notFitted}>
                            Couldn&apos;t fit in: {recipe.couldNotFitIn.join(", ")}
                        </p>
                    )}
                    {(recipe.couldNotAdd ?? []).length > 0 && (
                        <p className={styles.notFitted}>
                            Couldn&apos;t add: {recipe.couldNotAdd!.join(", ")}
                        </p>
                    )}
                    {(recipe.couldNotRemove ?? []).length > 0 && (
                        <p className={styles.notFitted}>
                            Couldn&apos;t remove: {recipe.couldNotRemove!.join(", ")}
                        </p>
                    )}
                    {recipe.alsoNeeded.length > 0 && (
                        <div className={styles.alsoNeeded}>
                            <div className={styles.alsoTitle}>You&apos;ll also need</div>
                            <div className={styles.alsoItems}>{recipe.alsoNeeded.join(" · ")}</div>
                        </div>
                    )}
                </section>

                <section className={styles.card}>
                    <h2 className={styles.label}>Method</h2>
                    <ol className={styles.steps}>
                        {recipe.steps.map((step, index) => (
                            <li key={index} className={styles.step}>
                                <span className={styles.stepNumber} aria-hidden="true">
                                    {index + 1}
                                </span>
                                <span>{step}</span>
                            </li>
                        ))}
                    </ol>
                </section>
            </div>
            <div className={onAdjust ? `${styles.actions} ${styles.actionsWithAdjust}` : styles.actions}>
                {onAdjust && (
                    <button type="button" className={styles.adjust} onClick={onAdjust} disabled={updating}>
                        <svg
                            width="18"
                            height="18"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2.2"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                            aria-hidden="true"
                        >
                            <path d="M4 7h10M18 7h2M4 17h4M12 17h8" />
                            <circle cx="16" cy="7" r="2" />
                            <circle cx="10" cy="17" r="2" />
                        </svg>
                        Adjust
                    </button>
                )}
                <div className={styles.actionRow}>
                    <button type="button" className={styles.primary} onClick={onAnother} disabled={updating}>
                        Another one
                    </button>
                    <button
                        type="button"
                        className={styles.secondary}
                        onClick={onStartAgain}
                        disabled={updating}
                    >
                        Start again
                    </button>
                </div>
            </div>
        </>
    );
}

const APPLIANCES: Record<Appliance, { label: string; icon: React.ReactNode }> = {
    toaster: {
        label: "Toaster",
        icon: (
            <>
                <path d="M4 10a4 4 0 0 1 4-4h8a4 4 0 0 1 4 4v8H4z" />
                <path d="M9 6V4M15 6V4M4 14h16" />
            </>
        ),
    },
    microwave: {
        label: "Microwave",
        icon: (
            <>
                <rect x="3" y="5" width="18" height="14" rx="2" />
                <rect x="6" y="8" width="9" height="8" rx="1" />
                <path d="M18 9v.01M18 12v.01" />
            </>
        ),
    },
    kettle: {
        label: "Kettle",
        icon: (
            <>
                <path d="M6 20h12l-1.5-9h-9z" />
                <path d="M7.5 11 9 6h6l1.5 5M18 12.5l2-2.5" />
            </>
        ),
    },
    none: {
        label: "No cooking",
        icon: <path d="M7 3v8M5 3v5a2 2 0 0 0 4 0V3M7 11v10M17 3c-2 0-3 2-3 6h3v12" />,
    },
};

/** Ready now (#88, project/ReadyNow.dc.html): 3 to 5 quick ideas, each with what it uses from stock. */
function IdeasView({
    meal,
    cards,
    preferUseFirst,
    isSaved = () => false,
    onToggleSave,
    onAdjust,
    onRetry,
    onUndo,
    onMore,
    onStartAgain,
}: {
    meal: RecipeMeal;
    cards: IdeaCard[];
    preferUseFirst: boolean;
    isSaved?: (idea: QuickIdea) => boolean;
    onToggleSave?: (idea: QuickIdea) => void;
    /** Adjust (#108): opens the sheet for that card; Try again and Undo act on that card only. */
    onAdjust?: (index: number) => void;
    onRetry?: (index: number) => void;
    onUndo?: (index: number) => void;
    onMore: () => void;
    onStartAgain: () => void;
}) {
    const open = (productId: string) => (e: React.MouseEvent) => {
        e.preventDefault();
        navigate(itemPath(productId));
    };
    const ideas = cards.map((c) => c.idea);
    const usesUseFirst = ideas.some((i) => i.uses.some((u) => u.useFirst));
    return (
        <>
            <div className={styles.recipe}>
                <div className={styles.recipeHead}>
                    <div className={styles.kicker}>Ready now · {mealLabel(meal)}</div>
                    <h1 className={styles.recipeTitle}>
                        {ideas.length} quick {ideas.length === 1 ? "idea" : "ideas"}
                    </h1>
                    <div className={styles.summary}>
                        Toaster, microwave, kettle or no cooking, from what&apos;s in.
                        {preferUseFirst && usesUseFirst && " Things to use first come first."}
                    </div>
                </div>

                {cards.map((card, index) => {
                    const { idea } = card;
                    const expired = idea.uses.filter(
                        (u) => u.daysUntilSoonest !== null && u.daysUntilSoonest < 0,
                    );
                    const appliance = APPLIANCES[idea.appliance];
                    const updating = card.status === "updating";
                    const failed = typeof card.status === "object" ? card.status.message : null;
                    const previous = card.history[card.history.length - 1]?.idea;
                    const before = new Set(previous?.uses.map((u) => u.productId) ?? []);
                    const isNew = (productId: string) => previous !== undefined && !before.has(productId);
                    const adjusted = card.summary !== null && card.history.length > 0;
                    return (
                        <article
                            key={index}
                            className={[
                                styles.idea,
                                adjusted && styles.ideaAdjusted,
                                updating && styles.ideaUpdating,
                            ]
                                .filter(Boolean)
                                .join(" ")}
                            aria-label={idea.title}
                            aria-busy={updating}
                        >
                            {updating && (
                                <div className={styles.ideaUpdatingNote} role="status">
                                    <span className={styles.smallSpinner} aria-hidden="true" />
                                    Updating this idea…
                                </div>
                            )}
                            {failed && (
                                <div className={styles.errorCard} role="alert">
                                    <span>{failed}</span>
                                    <button
                                        type="button"
                                        className={styles.tryAgain}
                                        onClick={() => onRetry?.(index)}
                                    >
                                        Try again
                                    </button>
                                </div>
                            )}
                            {adjusted && (
                                <div className={styles.ideaAdjustedLine}>
                                    <span className={styles.adjustedText}>
                                        {adjustmentLine(card.summary!)}
                                    </span>
                                    <button
                                        type="button"
                                        className={styles.undoAdjust}
                                        disabled={updating}
                                        onClick={() => onUndo?.(index)}
                                    >
                                        Undo
                                    </button>
                                </div>
                            )}
                            <div className={styles.ideaHead}>
                                <h2 className={styles.ideaTitle}>{idea.title}</h2>
                                <span className={styles.ideaMinutes}>{idea.minutes} min</span>
                            </div>
                            <div>
                                <span className={styles.appliance}>
                                    <svg
                                        width="16"
                                        height="16"
                                        viewBox="0 0 24 24"
                                        fill="none"
                                        stroke="currentColor"
                                        strokeWidth="2"
                                        strokeLinecap="round"
                                        strokeLinejoin="round"
                                        aria-hidden="true"
                                    >
                                        {appliance.icon}
                                    </svg>
                                    {appliance.label}
                                </span>
                            </div>
                            <div className={styles.how}>{idea.how}</div>
                            {expired.map((u) => (
                                <div key={u.productId} className={styles.check}>
                                    Check the {u.displayName.toLowerCase()} is still good first.
                                </div>
                            ))}
                            {idea.uses.length > 0 && (
                                <div className={styles.uses}>
                                    <div className={styles.usesLabel}>Uses</div>
                                    <div className={styles.useLinks}>
                                        {idea.uses.map((u) => {
                                            const gone =
                                                u.daysUntilSoonest !== null && u.daysUntilSoonest < 0;
                                            return (
                                                <a
                                                    key={u.productId}
                                                    href={itemPath(u.productId)}
                                                    className={styles.useLink}
                                                    onClick={open(u.productId)}
                                                >
                                                    {(gone || u.useFirst) && (
                                                        <span
                                                            className={`${styles.useDot} ${gone ? styles.useDotExpired : ""}`}
                                                            aria-hidden="true"
                                                        />
                                                    )}
                                                    {u.displayName}
                                                    {(gone || u.useFirst) && (
                                                        <span className={styles.srOnly}>
                                                            {gone ? " (estimated expired)" : " (use first)"}
                                                        </span>
                                                    )}
                                                    {isNew(u.productId) && (
                                                        <span className={styles.usedAddedTag}>Added</span>
                                                    )}
                                                </a>
                                            );
                                        })}
                                    </div>
                                </div>
                            )}
                            {idea.alsoNeeded && (
                                <div className={styles.ideaAlso}>
                                    You&apos;ll also need: {idea.alsoNeeded}
                                </div>
                            )}
                            {(idea.couldNotAdd ?? []).length > 0 && (
                                <p className={styles.notFitted}>
                                    Couldn&apos;t add: {idea.couldNotAdd!.join(", ")}
                                </p>
                            )}
                            {(idea.couldNotRemove ?? []).length > 0 && (
                                <p className={styles.notFitted}>
                                    Couldn&apos;t remove: {idea.couldNotRemove!.join(", ")}
                                </p>
                            )}
                            {(onAdjust || onToggleSave) && (
                                <div className={styles.ideaButtons}>
                                    {onAdjust && (
                                        <button
                                            type="button"
                                            className={styles.ideaAdjust}
                                            aria-label={`Adjust ${idea.title}`}
                                            disabled={updating}
                                            onClick={() => onAdjust(index)}
                                        >
                                            <svg
                                                width="16"
                                                height="16"
                                                viewBox="0 0 24 24"
                                                fill="none"
                                                stroke="currentColor"
                                                strokeWidth="2.2"
                                                strokeLinecap="round"
                                                strokeLinejoin="round"
                                                aria-hidden="true"
                                            >
                                                <path d="M4 7h10M18 7h2M4 17h4M12 17h8" />
                                                <circle cx="16" cy="7" r="2" />
                                                <circle cx="10" cy="17" r="2" />
                                            </svg>
                                            Adjust
                                        </button>
                                    )}
                                    {onToggleSave && (
                                        <SaveIdeaButton
                                            title={idea.title}
                                            saved={isSaved(idea)}
                                            onClick={() => onToggleSave(idea)}
                                        />
                                    )}
                                </div>
                            )}
                        </article>
                    );
                })}
            </div>
            <div className={styles.actions}>
                <button type="button" className={styles.primary} onClick={onMore}>
                    More ideas
                </button>
                <button type="button" className={styles.secondary} onClick={onStartAgain}>
                    Start again
                </button>
            </div>
        </>
    );
}
