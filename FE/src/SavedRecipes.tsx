import { useEffect, useId, useRef, useState } from "react";
import type { QuickIdea, Recipe, RecipeMeal, RecipeStep, SaveRecipeBody, SavedRecipe } from "./api";
import { PlateLegend, PlateTag, servingsText, stepClass, stepNumberClass } from "./Plates";
import plateStyles from "./Plates.module.css";
import { useOptionalShoppingList } from "./useShoppingList";
import styles from "./SavedRecipes.module.css";

/** Saved recipes and Ready now ideas (#105): project/SavedPicker.dc.html and project/SavedRecipe.dc.html. */

const MEAL_LABELS: Record<RecipeMeal, string> = {
    breakfast: "Breakfast",
    lunch: "Lunch",
    dinner: "Dinner",
    snack: "Snack",
};

const MEAL_CHIPS: Array<{ id: RecipeMeal | "all"; label: string }> = [
    { id: "all", label: "All" },
    { id: "breakfast", label: "Breakfast" },
    { id: "lunch", label: "Lunch" },
    { id: "dinner", label: "Dinner" },
    { id: "snack", label: "Snack" },
];

const TIME_CHIPS: Array<{ id: number | null; label: string }> = [
    { id: null, label: "Any time" },
    { id: 15, label: "15 min or less" },
    { id: 30, label: "30 min or less" },
    { id: 60, label: "1 hour or less" },
];

export const MAX_TITLE = 80;
export const MAX_NOTE = 300;

const dayKey = (d: Date) => `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;

/** "Saved today", "Saved yesterday" or "Saved 12 Oct". */
export function savedWhen(savedAt: string, now: Date = new Date()): string {
    const at = new Date(savedAt);
    if (dayKey(at) === dayKey(now)) return "Saved today";
    const yesterday = new Date(now);
    yesterday.setDate(now.getDate() - 1);
    if (dayKey(at) === dayKey(yesterday)) return "Saved yesterday";
    return `Saved ${at.toLocaleDateString("en-GB", { day: "numeric", month: "short" })}`;
}

/** "A", "A and B", "A, B and C". */
const listOf = (names: string[]) =>
    names.length <= 1 ? (names[0] ?? "") : `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;

/** The row's status line: green when everything's in, amber with what's missing. */
export function statusLine(recipe: SavedRecipe): { text: string; missing: boolean } {
    if (recipe.missing.length > 0) return { text: `Missing: ${recipe.missing.join(", ")}`, missing: true };
    const usesUp = recipe.usesUp.length > 0 ? ` · Uses up ${recipe.usesUp.join(", ")}` : "";
    return { text: `You've got everything${usesUp}`, missing: false };
}

/** What Save sends for a recipe as shown (an adjusted one saves that version). No Foundry call. */
export function recipeToSave(recipe: Recipe): SaveRecipeBody {
    return {
        kind: "recipe",
        title: recipe.title,
        meal: recipe.meal,
        totalMinutes: recipe.totalMinutes,
        servings: recipe.servings,
        ingredients: recipe.ingredients.map((i) => ({
            name: i.name,
            amount: i.amount,
            productId: i.productId,
            for: i.for ?? "all",
        })),
        steps: recipe.steps,
        alsoNeeded: recipe.alsoNeeded,
        diet: recipe.diet ?? "any",
        servingsMeat: recipe.servingsMeat ?? 0,
        servingsVeg: recipe.servingsVeg ?? 0,
    };
}

export function ideaToSave(idea: QuickIdea, meal: RecipeMeal): SaveRecipeBody {
    return {
        kind: "readyNow",
        title: idea.title,
        meal,
        totalMinutes: idea.minutes,
        uses: idea.uses.map((u) => ({ name: u.displayName, productId: u.productId })),
        alsoNeeded: idea.alsoNeeded ? [{ name: idea.alsoNeeded, for: "all" }] : [],
        diet: idea.diet ?? "any",
        how: idea.how,
        appliance: idea.appliance,
    };
}

/** The snapshot again, for Undo of a removal (the same id brings it back). */
export function savedToSave(saved: SavedRecipe): SaveRecipeBody {
    const items = saved.ingredients.map((i) => ({
        name: i.name,
        amount: i.amount,
        productId: i.productId,
        genericName: i.genericName,
        ...(saved.kind === "readyNow" ? {} : { for: i.for ?? "all" }),
    }));
    return {
        kind: saved.kind,
        title: saved.title,
        originalTitle: saved.originalTitle,
        meal: saved.meal,
        totalMinutes: saved.totalMinutes,
        servings: saved.servings,
        ...(saved.kind === "readyNow" ? { uses: items } : { ingredients: items }),
        steps: saved.steps,
        alsoNeeded: saved.alsoNeeded,
        diet: saved.diet ?? "any",
        servingsMeat: saved.servingsMeat ?? 0,
        servingsVeg: saved.servingsVeg ?? 0,
        how: saved.how,
        appliance: saved.appliance,
        note: saved.note,
    };
}

/** A saved recipe as the recipe Adjust (#104) works on. */
export function savedToRecipe(saved: SavedRecipe): Recipe {
    return {
        title: saved.title,
        summary: "",
        meal: saved.meal,
        servings: saved.servings ?? 1,
        totalMinutes: saved.totalMinutes,
        ingredients: saved.ingredients.map((i) => ({
            name: i.name,
            amount: i.amount ?? "",
            productId: i.status === "missing" ? null : i.productId,
            useFirst: i.status === "useFirst",
            for: i.for ?? "all",
        })),
        steps: saved.kind === "readyNow" && saved.how ? [{ text: saved.how, for: "all" }] : saved.steps,
        usesUp: [],
        alsoNeeded: saved.alsoNeeded,
        couldNotFitIn: [],
        diet: saved.diet ?? "any",
        servingsMeat: saved.servingsMeat ?? 0,
        servingsVeg: saved.servingsVeg ?? 0,
    };
}

function Bookmark({ size, filled }: { size: number; filled: boolean }) {
    return (
        <svg
            width={size}
            height={size}
            viewBox="0 0 24 24"
            fill={filled ? "currentColor" : "none"}
            stroke="currentColor"
            strokeWidth="2"
            strokeLinejoin="round"
            aria-hidden="true"
        >
            <path d="M6 4h12v17l-6-4-6 4z" />
        </svg>
    );
}

/** The Save pill (top right of a recipe, and of a saved recipe): outlined "Save", or filled "Saved". */
export function SavePill({ saved, onClick }: { saved: boolean; onClick: () => void }) {
    return (
        <button
            type="button"
            className={saved ? `${styles.pill} ${styles.pillOn}` : styles.pill}
            aria-pressed={saved}
            onClick={onClick}
        >
            <Bookmark size={18} filled={saved} />
            {saved ? "Saved" : "Save"}
        </button>
    );
}

/** Save on a Ready now idea card (project/ReadyNow.dc.html, ReadyNowStates.dc.html). */
export function SaveIdeaButton({
    title,
    saved,
    onClick,
}: {
    title: string;
    saved: boolean;
    onClick: () => void;
}) {
    return (
        <button
            type="button"
            className={saved ? `${styles.ideaSave} ${styles.ideaSaveOn}` : styles.ideaSave}
            aria-pressed={saved}
            aria-label={saved ? `Saved ${title}` : `Save ${title}`}
            onClick={onClick}
        >
            <Bookmark size={16} filled={saved} />
            {saved ? "Saved" : "Save"}
        </button>
    );
}

/**
 * The Saved box at the top of Hungry? (project/SavedPicker.dc.html): a combobox over the household's saved recipes,
 * filtered by meal, time and search text here (the list is small). Closed by default; opens on focus or the chevron,
 * closes on Escape, a tap outside, or choosing one.
 */
export function SavedBox({
    saved,
    failed,
    now,
    onOpenChange,
    onChoose,
}: {
    saved: SavedRecipe[] | null;
    failed: boolean;
    now?: Date;
    onOpenChange: (open: boolean) => void;
    onChoose: (recipe: SavedRecipe) => void;
}) {
    const [open, setOpenState] = useState(false);
    const [query, setQuery] = useState("");
    const [meal, setMeal] = useState<RecipeMeal | "all">("all");
    const [maxMinutes, setMaxMinutes] = useState<number | null>(null);
    const box = useRef<HTMLDivElement>(null);
    const listId = useId();
    const inputId = useId();

    const setOpen = (next: boolean) => {
        setOpenState(next);
        onOpenChange(next);
    };

    useEffect(() => {
        if (!open) return;
        const outside = (e: PointerEvent) => {
            if (box.current && !box.current.contains(e.target as Node)) {
                setOpenState(false);
                onOpenChange(false);
            }
        };
        document.addEventListener("pointerdown", outside);
        return () => document.removeEventListener("pointerdown", outside);
    }, [open, onOpenChange]);

    const all = saved ?? [];
    const q = query.trim().toLowerCase();
    const rows = all.filter(
        (s) =>
            (meal === "all" || s.meal === meal) &&
            (maxMinutes === null || s.totalMinutes <= maxMinutes) &&
            (!q || s.title.toLowerCase().includes(q)),
    );
    const count = saved === null ? "" : all.length === 0 ? "None saved" : `${all.length} saved`;

    return (
        <div
            className={styles.saved}
            ref={box}
            onKeyDown={(e) => {
                if (e.key === "Escape" && open) {
                    e.stopPropagation();
                    setOpen(false);
                }
            }}
        >
            <div className={styles.savedHead}>
                <label htmlFor={inputId} className={styles.savedLabel}>
                    Saved
                </label>
                <span className={styles.savedCount}>{count}</span>
            </div>
            <div className={open ? `${styles.box} ${styles.boxOpen}` : styles.box}>
                <div className={styles.searchRow}>
                    <span className={styles.searchIcon}>
                        <Bookmark size={20} filled={false} />
                    </span>
                    <input
                        id={inputId}
                        type="search"
                        role="combobox"
                        aria-expanded={open}
                        aria-controls={listId}
                        autoComplete="off"
                        className={styles.search}
                        placeholder="Find a saved recipe"
                        value={query}
                        onFocus={() => !open && setOpen(true)}
                        onChange={(e) => {
                            setQuery(e.target.value);
                            if (!open) setOpen(true);
                        }}
                    />
                    <button
                        type="button"
                        className={styles.chevron}
                        aria-label={open ? "Hide saved recipes" : "Show saved recipes"}
                        onClick={() => setOpen(!open)}
                    >
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
                            <path d={open ? "M6 15l6-6 6 6" : "M6 9l6 6 6-6"} />
                        </svg>
                    </button>
                </div>
                {open && (
                    <>
                        {all.length > 0 && (
                            <div className={styles.filters}>
                                <div role="radiogroup" aria-label="Filter by meal" className={styles.chips}>
                                    {MEAL_CHIPS.map((c) => (
                                        <button
                                            key={c.id}
                                            type="button"
                                            role="radio"
                                            aria-checked={meal === c.id}
                                            className={
                                                meal === c.id
                                                    ? `${styles.chip} ${styles.chipOn}`
                                                    : styles.chip
                                            }
                                            onClick={() => setMeal(c.id)}
                                        >
                                            {c.label}
                                        </button>
                                    ))}
                                </div>
                                <div
                                    role="radiogroup"
                                    aria-label="Filter by cook time"
                                    className={styles.chips}
                                >
                                    {TIME_CHIPS.map((c) => (
                                        <button
                                            key={c.label}
                                            type="button"
                                            role="radio"
                                            aria-checked={maxMinutes === c.id}
                                            className={
                                                maxMinutes === c.id
                                                    ? `${styles.chip} ${styles.chipOn}`
                                                    : styles.chip
                                            }
                                            onClick={() => setMaxMinutes(c.id)}
                                        >
                                            {c.label}
                                        </button>
                                    ))}
                                </div>
                            </div>
                        )}
                        <ul id={listId} role="listbox" aria-label="Saved recipes" className={styles.rows}>
                            {failed ? (
                                <li className={styles.empty}>Couldn&apos;t load your saved recipes.</li>
                            ) : saved === null ? (
                                <li className={styles.empty}>Loading…</li>
                            ) : all.length === 0 ? (
                                <li className={styles.empty}>
                                    Nothing saved yet. Tap Save on a recipe you like.
                                </li>
                            ) : rows.length === 0 ? (
                                <li className={styles.empty}>Nothing saved matches that.</li>
                            ) : (
                                rows.map((r) => {
                                    const status = statusLine(r);
                                    return (
                                        <li key={r.id} role="option" aria-selected={false}>
                                            <button
                                                type="button"
                                                className={styles.row}
                                                onClick={() => {
                                                    setOpen(false);
                                                    onChoose(r);
                                                }}
                                            >
                                                <span className={styles.rowTitleLine}>
                                                    <span className={styles.rowTitle}>{r.title}</span>
                                                    {r.kind === "readyNow" && (
                                                        <span className={styles.readyTag}>Ready now</span>
                                                    )}
                                                </span>
                                                <span className={styles.rowMeta}>
                                                    {MEAL_LABELS[r.meal]} · {r.totalMinutes} min ·{" "}
                                                    {savedWhen(r.savedAt, now)}
                                                </span>
                                                <span
                                                    className={
                                                        status.missing
                                                            ? `${styles.rowStatus} ${styles.rowMissing}`
                                                            : styles.rowStatus
                                                    }
                                                >
                                                    {status.text}
                                                </span>
                                            </button>
                                        </li>
                                    );
                                })
                            )}
                        </ul>
                    </>
                )}
            </div>
        </div>
    );
}

/**
 * A saved recipe or idea (project/SavedRecipe.dc.html), checked against today's stock: rename, the shared note, Add
 * missing to the list, Adjust to what's in (recipes), and Remove from saved.
 */
export function SavedRecipeView({
    recipe,
    saved,
    now,
    updating = false,
    adjustError = null,
    onBack,
    onToggleSaved,
    onUpdate,
    onAdjust,
    onRetryAdjust,
}: {
    recipe: SavedRecipe;
    /** false after Remove from saved (until Undo or Save again). */
    saved: boolean;
    now?: Date;
    updating?: boolean;
    adjustError?: string | null;
    onBack: () => void;
    onToggleSaved: () => void;
    /** Rename or note: resolves false when it couldn't be saved. */
    onUpdate: (change: { title?: string; note?: string }) => Promise<boolean>;
    onAdjust: (() => void) | null;
    onRetryAdjust?: () => void;
}) {
    const list = useOptionalShoppingList();
    const [renaming, setRenaming] = useState(false);
    const [title, setTitle] = useState(recipe.title);
    const [note, setNote] = useState(recipe.note);
    const [problem, setProblem] = useState<string | null>(null);
    const titleInput = useRef<HTMLInputElement>(null);

    useEffect(() => {
        setTitle(recipe.title);
        setNote(recipe.note);
    }, [recipe.id, recipe.title, recipe.note]);
    useEffect(() => {
        if (renaming) titleInput.current?.focus();
    }, [renaming]);

    const onList = (name: string) =>
        list?.open.some((i) => i.name.trim().toLowerCase() === name.trim().toLowerCase()) ?? false;
    const missingNames = recipe.ingredients
        .filter((i) => i.status === "missing")
        .map((i) => i.genericName ?? i.name);
    const allOnList = missingNames.length > 0 && missingNames.every(onList);

    const saveTitle = async () => {
        const next = title.trim();
        setRenaming(false);
        if (!next || next === recipe.title) {
            setTitle(recipe.title);
            return;
        }
        setProblem(null);
        if (!(await onUpdate({ title: next }))) {
            setTitle(recipe.title);
            setProblem("Couldn't rename it. Try again.");
        }
    };

    const saveNote = async () => {
        if (note === recipe.note) return;
        setProblem(null);
        if (!(await onUpdate({ note }))) setProblem("Couldn't save your note. Try again.");
    };

    const addMissing = () => {
        if (!list) return;
        for (const name of missingNames) {
            if (!onList(name)) list.add(name, null, "savedRecipe");
        }
    };

    const ready = recipe.kind === "readyNow";
    const meta = [
        MEAL_LABELS[recipe.meal],
        ready ? "Ready now" : null,
        recipe.servings
            ? servingsText(recipe.servings, recipe.diet, recipe.servingsMeat, recipe.servingsVeg)
            : null,
        recipe.diet === "veg" ? "Veggie" : null,
        `${recipe.totalMinutes} min`,
        `${savedWhen(recipe.savedAt, now)} by ${recipe.savedByName}`,
    ]
        .filter(Boolean)
        .join(" · ");
    const steps: RecipeStep[] = ready ? (recipe.how ? [{ text: recipe.how, for: "all" }] : []) : recipe.steps;

    return (
        <>
            <div
                className={updating ? `${styles.view} ${styles.updating}` : styles.view}
                aria-busy={updating}
            >
                <div className={styles.top}>
                    <button type="button" className={styles.back} onClick={onBack}>
                        <span className={styles.backIcon}>
                            <svg
                                width="22"
                                height="22"
                                viewBox="0 0 24 24"
                                fill="none"
                                stroke="currentColor"
                                strokeWidth="2.2"
                                strokeLinecap="round"
                                strokeLinejoin="round"
                                aria-hidden="true"
                            >
                                <path d="M15 5l-7 7 7 7" />
                            </svg>
                        </span>
                        Hungry?
                    </button>
                    <SavePill saved={saved} onClick={onToggleSaved} />
                </div>

                {updating && (
                    <div className={styles.updatingNote} role="status">
                        <span className={styles.spinner} aria-hidden="true" />
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
                {problem && (
                    <div className={styles.errorCard} role="alert">
                        <span>{problem}</span>
                    </div>
                )}

                <div className={styles.head}>
                    <div className={styles.meta}>{meta}</div>
                    <div className={styles.titleRow}>
                        {renaming ? (
                            <input
                                ref={titleInput}
                                className={styles.titleInput}
                                aria-label="Name"
                                value={title}
                                maxLength={MAX_TITLE}
                                onChange={(e) => setTitle(e.target.value)}
                                onBlur={() => void saveTitle()}
                                onKeyDown={(e) => {
                                    if (e.key === "Enter") {
                                        e.preventDefault();
                                        e.currentTarget.blur();
                                    } else if (e.key === "Escape") {
                                        setTitle(recipe.title);
                                        setRenaming(false);
                                    }
                                }}
                            />
                        ) : (
                            <h1 className={styles.title}>{title}</h1>
                        )}
                        {!renaming && saved && (
                            <button
                                type="button"
                                className={styles.rename}
                                aria-label="Rename"
                                onClick={() => setRenaming(true)}
                            >
                                <svg
                                    width="20"
                                    height="20"
                                    viewBox="0 0 24 24"
                                    fill="none"
                                    stroke="currentColor"
                                    strokeWidth="2"
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    aria-hidden="true"
                                >
                                    <path d="M4 20h4L19 9l-4-4L4 16z" />
                                </svg>
                            </button>
                        )}
                    </div>
                    {recipe.diet === "half" && <PlateLegend />}
                </div>

                {saved && (
                    <div className={styles.noteCard}>
                        <div className={styles.noteHead}>
                            <label htmlFor={`note-${recipe.id}`} className={styles.sectionLabel}>
                                Your note
                            </label>
                            <span className={styles.noteHint}>Everyone at home sees this</span>
                        </div>
                        <textarea
                            id={`note-${recipe.id}`}
                            className={styles.note}
                            rows={2}
                            maxLength={MAX_NOTE}
                            value={note}
                            onChange={(e) => setNote(e.target.value)}
                            onBlur={() => void saveNote()}
                        />
                    </div>
                )}

                {missingNames.length > 0 && (
                    <div className={styles.missing}>
                        {allOnList ? (
                            <>
                                <div className={styles.missingTitle}>
                                    {listOf(missingNames)} {missingNames.length === 1 ? "is" : "are"} on the
                                    shopping list
                                </div>
                                <div className={styles.missingText}>
                                    Everything else is in. Adjust it if you want to make it without.
                                </div>
                            </>
                        ) : (
                            <>
                                <div className={styles.missingTitle}>
                                    Missing {missingNames.length}{" "}
                                    {missingNames.length === 1 ? "thing" : "things"}
                                </div>
                                <div className={styles.missingText}>
                                    You&apos;ve got everything except {listOf(missingNames)}.
                                </div>
                            </>
                        )}
                        {list && (
                            <button
                                type="button"
                                className={
                                    allOnList ? `${styles.addMissing} ${styles.added}` : styles.addMissing
                                }
                                disabled={allOnList}
                                onClick={addMissing}
                            >
                                {allOnList ? "Added ✓" : "Add missing to the list"}
                            </button>
                        )}
                    </div>
                )}

                <section className={styles.card}>
                    <h2 className={styles.sectionLabel}>
                        {ready ? "Uses" : "Ingredients"} · checked against today&apos;s stock
                    </h2>
                    <ul className={styles.ingredients}>
                        {recipe.ingredients.map((i, index) => {
                            const missing = i.status === "missing";
                            const noteText = missing
                                ? onList(i.genericName ?? i.name)
                                    ? "On the list"
                                    : "Not in stock"
                                : i.status === "useFirst"
                                  ? "Use first"
                                  : "In stock";
                            return (
                                <li key={index} className={styles.ingredient}>
                                    <span
                                        className={
                                            missing ? `${styles.icon} ${styles.iconMissing}` : styles.icon
                                        }
                                        aria-hidden="true"
                                    >
                                        {missing ? "!" : "✓"}
                                    </span>
                                    {!ready && <span className={styles.amount}>{i.amount}</span>}
                                    <span className={styles.name}>{i.name}</span>
                                    <span
                                        className={
                                            i.status === "have"
                                                ? styles.stockNote
                                                : `${styles.stockNote} ${styles.warn}`
                                        }
                                    >
                                        {noteText}
                                    </span>
                                    <PlateTag plate={i.for} />
                                </li>
                            );
                        })}
                    </ul>
                    {recipe.alsoNeeded.length > 0 && (
                        <div className={styles.also}>
                            You&apos;ll also need:{" "}
                            {recipe.alsoNeeded.map((a, index) => (
                                <span key={index}>
                                    {index > 0 && " · "}
                                    {a.name}
                                    {a.for !== "all" && " "}
                                    <PlateTag plate={a.for} small />
                                </span>
                            ))}
                        </div>
                    )}
                </section>

                {steps.length > 0 && (
                    <section className={styles.card}>
                        <h2 className={styles.sectionLabel}>{ready ? "How" : "Method"}</h2>
                        <ol className={styles.steps}>
                            {steps.map((step, index) => (
                                <li key={index} className={`${styles.step} ${stepClass(step.for) ?? ""}`}>
                                    <span
                                        className={`${styles.stepNumber} ${stepNumberClass(step.for) ?? ""}`}
                                        aria-hidden="true"
                                    >
                                        {index + 1}
                                    </span>
                                    {step.for === "all" ? (
                                        <span>{step.text}</span>
                                    ) : (
                                        <span className={plateStyles.stepBody}>
                                            <PlateTag plate={step.for} />
                                            <span>{step.text}</span>
                                        </span>
                                    )}
                                </li>
                            ))}
                        </ol>
                    </section>
                )}
            </div>
            <div className={styles.actions}>
                {onAdjust && (
                    <button type="button" className={styles.adjust} onClick={onAdjust} disabled={updating}>
                        Adjust to what&apos;s in
                    </button>
                )}
                <button
                    type="button"
                    className={onAdjust ? styles.remove : `${styles.remove} ${styles.removeWide}`}
                    onClick={onToggleSaved}
                    disabled={updating}
                >
                    {saved ? "Remove from saved" : "Save again"}
                </button>
            </div>
        </>
    );
}
