import { useEffect, useMemo, useRef, useState } from "react";
import {
    SignInRequiredError,
    type ApiClient,
    type Recipe,
    type RecipeAddition,
    type StockProduct,
} from "./api";
import styles from "./RecipeAdjust.module.css";

/** The suggested additions give up after this, quietly: nothing else waits for them (#104). */
export const SUGGESTIONS_TIMEOUT_MS = 8000;
export const MAX_TYPED = 3;
/** A Ready now idea has room for one thing that isn't in stock (#108). */
export const MAX_IDEA_TYPED = 1;
export const MAX_TWEAK = 300;

/** What the household chose in the sheet. */
export interface AdjustChoices {
    productIds: string[];
    typed: string[];
    remove: string[];
    tweak: string | null;
}

/** Something picked under Add: an in-stock product (from a suggestion or the search) or a typed thing to buy. */
type Pick = { kind: "product"; productId: string; name: string } | { kind: "typed"; name: string };

/**
 * "Adjust this recipe" (#104, project/RecipeAdjust.dc.html): a bottom sheet with Add (suggested chips from Jev, a search
 * of the cupboard, or something typed to buy), Remove (the recipe's ingredients as a checklist) and Tweak (free text).
 * Update recipe is off until something is chosen. Suggestions load as soon as it opens and hide quietly when there are
 * none, they fail, or they take more than 8 seconds.
 */
export function AdjustSheet({
    api,
    recipe,
    stock,
    onCancel,
    onUpdate,
    kind = "recipe",
}: {
    api: ApiClient;
    /** For a Ready now idea (#108), the idea as a recipe: its uses as the ingredients, its how as the one step. */
    recipe: Recipe;
    /** "idea": titled "Adjust this idea", with room for one thing to buy. */
    kind?: "recipe" | "idea";
    stock: StockProduct[];
    onCancel: () => void;
    onUpdate: (choices: AdjustChoices) => void;
}) {
    const maxTyped = kind === "idea" ? MAX_IDEA_TYPED : MAX_TYPED;
    const [suggestions, setSuggestions] = useState<RecipeAddition[] | "loading" | "none">("loading");
    const [picked, setPicked] = useState<Pick[]>([]);
    const [remove, setRemove] = useState<string[]>([]);
    const [tweak, setTweak] = useState("");
    const [query, setQuery] = useState("");
    const [typing, setTyping] = useState(false);
    const [typed, setTyped] = useState("");
    const closeRef = useRef(onCancel);
    closeRef.current = onCancel;

    useEffect(() => {
        let live = true;
        const timer = setTimeout(
            () => live && setSuggestions((s) => (s === "loading" ? "none" : s)),
            SUGGESTIONS_TIMEOUT_MS,
        );
        Promise.resolve()
            .then(() => api.suggestRecipeAdditions(recipe))
            .then(
                (answer) => {
                    if (live)
                        setSuggestions((s) =>
                            s === "loading" ? (answer.suggestions.length ? answer.suggestions : "none") : s,
                        );
                },
                (error: unknown) => {
                    if (!(error instanceof SignInRequiredError))
                        console.warn("Couldn't load suggested additions", error);
                    if (live) setSuggestions("none");
                },
            );
        return () => {
            live = false;
            clearTimeout(timer);
        };
    }, [api, recipe]);

    useEffect(() => {
        const onKey = (e: KeyboardEvent) => e.key === "Escape" && closeRef.current();
        document.addEventListener("keydown", onKey);
        return () => document.removeEventListener("keydown", onKey);
    }, []);

    const pickedIds = new Set(picked.flatMap((p) => (p.kind === "product" ? [p.productId] : [])));
    const inRecipe = new Set(recipe.ingredients.flatMap((i) => (i.productId ? [i.productId] : [])));
    const addProduct = (productId: string, name: string) =>
        !pickedIds.has(productId) && setPicked((p) => [...p, { kind: "product", productId, name }]);
    const typedCount = picked.filter((p) => p.kind === "typed").length;
    const addTyped = () => {
        const name = typed.trim().slice(0, 40);
        if (!name || typedCount >= maxTyped) return;
        if (!picked.some((p) => p.kind === "typed" && p.name.toLowerCase() === name.toLowerCase()))
            setPicked((p) => [...p, { kind: "typed", name }]);
        setTyped("");
        setTyping(false);
    };

    const q = query.trim().toLowerCase();
    const matches = useMemo(
        () =>
            q
                ? stock
                      .filter(
                          (p) =>
                              p.displayName.toLowerCase().includes(q) ||
                              p.genericName?.toLowerCase().includes(q),
                      )
                      .filter((p) => !inRecipe.has(p.productId))
                      .slice(0, 8)
                : [],
        // eslint-disable-next-line react-hooks/exhaustive-deps -- recipe is fixed while the sheet is open
        [q, stock],
    );

    const shownSuggestions = Array.isArray(suggestions)
        ? suggestions.filter((s) => !s.productIds.some((id) => pickedIds.has(id)))
        : [];
    const chosen = picked.length > 0 || remove.length > 0 || tweak.trim().length > 0;
    const update = () => {
        if (!chosen) return;
        onUpdate({
            productIds: picked.flatMap((p) => (p.kind === "product" ? [p.productId] : [])),
            typed: picked.flatMap((p) => (p.kind === "typed" ? [p.name] : [])),
            remove,
            tweak: tweak.trim() || null,
        });
    };

    return (
        <div className={styles.overlay}>
            <div className={styles.scrim} aria-hidden="true" onClick={onCancel} />
            <div role="dialog" aria-modal="true" aria-labelledby="adjust-title" className={styles.sheet}>
                <div className={styles.head}>
                    <div className={styles.handle} aria-hidden="true" />
                    <div className={styles.titleRow}>
                        <h2 id="adjust-title" className={styles.title}>
                            {kind === "idea" ? "Adjust this idea" : "Adjust this recipe"}
                        </h2>
                        <button type="button" className={styles.close} aria-label="Close" onClick={onCancel}>
                            <svg
                                width="20"
                                height="20"
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
                    </div>
                    <div className={styles.intro}>
                        Change anything below; we&apos;ll update the same{" "}
                        {kind === "idea" ? "idea" : "recipe"}.
                    </div>
                </div>

                <div className={styles.body}>
                    <section className={styles.section} aria-labelledby="adjust-add">
                        <h3 id="adjust-add" className={styles.sectionTitle}>
                            Add
                        </h3>
                        {suggestions !== "none" &&
                            (shownSuggestions.length > 0 || suggestions === "loading") && (
                                <div className={styles.suggested}>
                                    <div className={styles.suggestedLabel}>Suggested from your cupboard</div>
                                    <div className={styles.suggestions} aria-live="polite">
                                        {suggestions === "loading" ? (
                                            <>
                                                <span className={styles.finding} role="status">
                                                    <span
                                                        className={styles.smallSpinner}
                                                        aria-hidden="true"
                                                    />
                                                    Finding good additions…
                                                </span>
                                                <span className={styles.placeholderChip} aria-hidden="true" />
                                            </>
                                        ) : (
                                            shownSuggestions.map((s) => (
                                                <button
                                                    key={s.name}
                                                    type="button"
                                                    className={styles.suggestion}
                                                    aria-label={`Add ${s.name}${s.useFirst ? " (use first)" : ""}`}
                                                    onClick={() => addProduct(s.productIds[0], s.name)}
                                                >
                                                    {s.useFirst && (
                                                        <span
                                                            className={styles.useFirstDot}
                                                            aria-hidden="true"
                                                        />
                                                    )}
                                                    + {s.name}
                                                </button>
                                            ))
                                        )}
                                    </div>
                                </div>
                            )}
                        {picked.length > 0 && (
                            <div className={styles.picked}>
                                {picked.map((p) => (
                                    <span
                                        key={p.kind === "product" ? p.productId : `typed-${p.name}`}
                                        className={styles.pickedChip}
                                    >
                                        {p.name}
                                        <button
                                            type="button"
                                            className={styles.unpick}
                                            aria-label={`Remove ${p.name}`}
                                            onClick={() => setPicked((all) => all.filter((x) => x !== p))}
                                        >
                                            ×
                                        </button>
                                    </span>
                                ))}
                            </div>
                        )}
                        <label className={styles.searchBox}>
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
                                type="search"
                                className={styles.searchInput}
                                placeholder="Something else from your cupboard"
                                aria-label="Add something from your cupboard"
                                autoComplete="off"
                                value={query}
                                onChange={(e) => setQuery(e.target.value)}
                            />
                        </label>
                        {matches.length > 0 && (
                            <ul className={styles.matches} aria-label="In your cupboard">
                                {matches.map((p) => (
                                    <li key={p.productId}>
                                        <button
                                            type="button"
                                            className={styles.match}
                                            onClick={() => {
                                                addProduct(p.productId, p.genericName ?? p.displayName);
                                                setQuery("");
                                            }}
                                        >
                                            {p.useFirst && (
                                                <span className={styles.useFirstDot} aria-hidden="true" />
                                            )}
                                            {p.genericName ?? p.displayName}
                                        </button>
                                    </li>
                                ))}
                            </ul>
                        )}
                        {typing ? (
                            <div className={styles.typedRow}>
                                <input
                                    className={styles.typedInput}
                                    aria-label="Something you'd buy"
                                    placeholder="e.g. chilli flakes"
                                    maxLength={40}
                                    value={typed}
                                    onChange={(e) => setTyped(e.target.value)}
                                    onKeyDown={(e) => {
                                        if (e.key === "Enter") {
                                            e.preventDefault();
                                            addTyped();
                                        }
                                    }}
                                    autoFocus
                                />
                                <button
                                    type="button"
                                    className={styles.typedAdd}
                                    onClick={addTyped}
                                    disabled={!typed.trim()}
                                >
                                    Add
                                </button>
                            </div>
                        ) : (
                            typedCount < maxTyped && (
                                <button
                                    type="button"
                                    className={styles.linkButton}
                                    onClick={() => setTyping(true)}
                                >
                                    Or type something you&apos;d buy
                                </button>
                            )
                        )}
                    </section>

                    <section className={styles.section} aria-labelledby="adjust-remove">
                        <h3 id="adjust-remove" className={styles.sectionTitle}>
                            Remove
                        </h3>
                        <ul className={styles.removable}>
                            {recipe.ingredients.map((i, index) => {
                                const on = remove.includes(i.name);
                                return (
                                    <li key={`${i.name}-${index}`}>
                                        <label className={styles.removeRow}>
                                            <input
                                                type="checkbox"
                                                className={styles.removeBox}
                                                checked={on}
                                                onChange={() =>
                                                    setRemove((r) =>
                                                        on ? r.filter((x) => x !== i.name) : [...r, i.name],
                                                    )
                                                }
                                            />
                                            <span
                                                className={
                                                    on
                                                        ? `${styles.removeName} ${styles.removed}`
                                                        : styles.removeName
                                                }
                                            >
                                                {i.name}
                                            </span>
                                        </label>
                                    </li>
                                );
                            })}
                        </ul>
                    </section>

                    <section className={styles.section}>
                        <div className={styles.tweakHead}>
                            <label htmlFor="adjust-tweak" className={styles.sectionTitle}>
                                Tweak
                            </label>
                            <span className={styles.optional}>Optional</span>
                        </div>
                        <textarea
                            id="adjust-tweak"
                            className={styles.tweak}
                            rows={2}
                            maxLength={MAX_TWEAK}
                            placeholder="e.g. spicier, for 4 people, no oven, less washing up"
                            value={tweak}
                            onChange={(e) => setTweak(e.target.value)}
                        />
                    </section>
                </div>

                <div className={styles.footer}>
                    <button type="button" className={styles.cancel} onClick={onCancel}>
                        Cancel
                    </button>
                    <button
                        type="button"
                        className={chosen ? styles.update : `${styles.update} ${styles.updateOff}`}
                        aria-disabled={!chosen}
                        onClick={update}
                    >
                        {kind === "idea" ? "Update idea" : "Update recipe"}
                    </button>
                </div>
            </div>
        </div>
    );
}

/** `Adjusted: + Chorizo · − Onion · "spicier"` */
export function adjustmentLine(summary: {
    added: string[];
    removed: string[];
    tweak: string | null;
}): string {
    const parts = [
        ...summary.added.map((a) => `+ ${a}`),
        ...summary.removed.map((r) => `− ${r}`),
        ...(summary.tweak ? [`"${summary.tweak}"`] : []),
    ];
    return `Adjusted: ${parts.join(" · ")}`;
}
