import { useEffect, useMemo, useRef, useState } from "react";
import {
    ApiError,
    SignInRequiredError,
    type ApiClient,
    type Recipe,
    type RecipeMeal,
    type RecipeRequest,
    type StockProduct,
} from "./api";
import { expiryBadge } from "./format";
import { itemPath, navigate } from "./router";
import styles from "./Recipes.module.css";

const MEALS: Array<{ id: RecipeMeal; label: string }> = [
    { id: "breakfast", label: "Breakfast" },
    { id: "lunch", label: "Lunch" },
    { id: "dinner", label: "Dinner" },
];

export const MAX_COMMENTS = 300;
const COUNT_FROM = 250;
const UNAVAILABLE = "Couldn't come up with a recipe just now. Try again.";

/** The meal for the time of day in the UK: before 11:00 breakfast, 11:00 to 16:00 lunch, otherwise dinner. */
export function defaultMeal(now: Date = new Date()): RecipeMeal {
    const hour = Number(
        new Intl.DateTimeFormat("en-GB", {
            hour: "2-digit",
            hourCycle: "h23",
            timeZone: "Europe/London",
        }).format(now),
    );
    return hour < 11 ? "breakfast" : hour < 16 ? "lunch" : "dinner";
}

const mealLabel = (meal: RecipeMeal) => MEALS.find((m) => m.id === meal)!.label;

/** "Hummus · Est. expired", with the badge's tone. */
function badge(name: string, days: number | null, date: string | null): { text: string; tone: string } {
    if (days === null || date === null) return { text: name, tone: styles.soon };
    const b = expiryBadge(days, date);
    return { text: `${name} · ${b.text}`, tone: b.tone === "expired" ? styles.expired : styles.soon };
}

interface Choices {
    meal: RecipeMeal;
    prefer: boolean;
    picked: string[];
    comments: string;
}

type Phase =
    | { kind: "form" }
    | { kind: "loading" }
    | { kind: "recipe"; recipe: Recipe }
    | { kind: "error"; message: string; retry: RecipeRequest };

/**
 * Recipes (#73): "What shall we cook?" (project/Recipes.dc.html), then the recipe (project/Recipe.dc.html). Built
 * from the stock on the server; the choices, comments, recipe and titles shown live only here, so leaving the screen
 * starts again with an empty form.
 */
export function RecipesScreen({ api, now }: { api: ApiClient; now?: Date }) {
    const [stock, setStock] = useState<StockProduct[] | null>(null);
    const [stockFailed, setStockFailed] = useState(false);
    const [choices, setChoices] = useState<Choices>(() => ({
        meal: defaultMeal(now),
        prefer: false,
        picked: [],
        comments: "",
    }));
    const [phase, setPhase] = useState<Phase>({ kind: "form" });
    const [shownTitles, setShownTitles] = useState<string[]>([]);
    const top = useRef<HTMLDivElement>(null);

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
        setPhase({ kind: "loading" });
        try {
            const answer = await api.suggestRecipe(request);
            if (!answer.available || !answer.recipe) {
                setPhase({ kind: "error", message: answer.message ?? UNAVAILABLE, retry: request });
                return;
            }
            const recipe = answer.recipe;
            setShownTitles(
                [...request.avoidTitles, recipe.title].filter((t, i, all) => all.indexOf(t) === i),
            );
            setPhase({ kind: "recipe", recipe });
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            const message =
                error instanceof ApiError &&
                typeof error.body === "object" &&
                error.body !== null &&
                "error" in error.body &&
                typeof error.body.error === "string"
                    ? error.body.error
                    : UNAVAILABLE;
            setPhase({ kind: "error", message, retry: request });
        }
    };

    const request = (avoidTitles: string[]): RecipeRequest => ({
        meal: choices.meal,
        preferUseFirst: choices.prefer,
        includeProductIds: choices.picked,
        comments: choices.comments.trim() || null,
        avoidTitles,
    });

    return (
        <div className={styles.screen} ref={top}>
            {phase.kind === "recipe" ? (
                <RecipeView
                    recipe={phase.recipe}
                    onAnother={() => void ask(request(shownTitles))}
                    onStartAgain={() => setPhase({ kind: "form" })}
                />
            ) : (
                <>
                    <header className={styles.header}>
                        <div className={styles.kicker}>Recipes</div>
                        <h1 className={styles.title}>What shall we cook?</h1>
                        <div className={styles.subtitle}>Built from what&apos;s in your cupboard.</div>
                    </header>
                    {phase.kind === "loading" ? (
                        <div className={styles.loading} role="status">
                            <span className={styles.spinner} aria-hidden="true" />
                            Thinking up something with what you&apos;ve got…
                        </div>
                    ) : (
                        <div className={styles.content}>
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
                                    onChange={setChoices}
                                    onSubmit={() => {
                                        setShownTitles([]);
                                        void ask(request([]));
                                    }}
                                />
                            )}
                        </div>
                    )}
                </>
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
    return (
        <form
            className={styles.form}
            onSubmit={(e) => {
                e.preventDefault();
                onSubmit();
            }}
        >
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
                        : `Builds the recipe around the ${useFirst.length} ${useFirst.length === 1 ? "thing" : "things"} to use first.`}
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
                    placeholder="e.g. something spicy, no oven, ready in 20 minutes"
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
                Get a recipe
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
    onAnother,
    onStartAgain,
}: {
    recipe: Recipe;
    onAnother: () => void;
    onStartAgain: () => void;
}) {
    const open = (productId: string) => (e: React.MouseEvent) => {
        e.preventDefault();
        navigate(itemPath(productId));
    };
    return (
        <>
            <div className={styles.recipe}>
                <div className={styles.recipeHead}>
                    <div className={styles.kicker}>
                        {mealLabel(recipe.meal)} · Serves {recipe.servings} · {recipe.totalMinutes} min
                    </div>
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
                            </li>
                        ))}
                    </ul>
                    {recipe.couldNotFitIn.length > 0 && (
                        <p className={styles.notFitted}>
                            Couldn&apos;t fit in: {recipe.couldNotFitIn.join(", ")}
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
            <div className={styles.actions}>
                <button type="button" className={styles.primary} onClick={onAnother}>
                    Another one
                </button>
                <button type="button" className={styles.secondary} onClick={onStartAgain}>
                    Start again
                </button>
            </div>
        </>
    );
}
