import { useCallback, useEffect, useRef, useState } from "react";
import {
    ApiError,
    SignInRequiredError,
    type ApiClient,
    type MealPlan,
    type PlanMeal,
    type PlanMealType,
} from "./api";
import { MAX_COMMENTS, MEAL_TYPES, mealsOf, mealsText, planError, planSummary, sharesText } from "./plans";
import { CheckBeforeCooking } from "./Plates";
import styles from "./Plans.module.css";
import { goBack, PLAN_PATH } from "./router";

const POLL_MS = 2000;
const TOAST_MS = 8000;

type Toast = { text: string; undo?: () => void };

/**
 * Plan meals, step 2: the plan (#119, project/PlanMeals.dc.html). A new plan is written in the background, so this waits
 * ("Planning 6 meals…", polling every 2 seconds). Then meals grouped by type; each can be Kept, Adjusted, replaced (Another one) or
 * deleted (with Undo), a meal can be added under each type, and the whole plan can be adjusted (kept meals stay). Every change is
 * saved on the server at once; a failed call leaves the plan as it was, with a message.
 */
export function PlanScreen({
    api,
    planId,
    pollMs = POLL_MS,
}: {
    api: ApiClient;
    planId: string;
    pollMs?: number;
}) {
    const [plan, setPlan] = useState<MealPlan | null>(null);
    const [missing, setMissing] = useState(false);
    const [loadFailed, setLoadFailed] = useState(false);
    const [busy, setBusy] = useState<string | null>(null);
    const [problem, setProblem] = useState<string | null>(null);
    const [adjusting, setAdjusting] = useState<string | null>(null);
    const [adjustText, setAdjustText] = useState("");
    const [whole, setWhole] = useState("");
    const [toast, setToast] = useState<Toast | null>(null);
    const live = useRef(true);
    const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

    const load = useCallback(() => {
        clearTimeout(timer.current);
        setLoadFailed(false);
        const once = async () => {
            try {
                const next = await api.mealPlan(planId);
                if (!live.current) return;
                setPlan(next);
                if (next.status === "generating") timer.current = setTimeout(() => void once(), pollMs);
            } catch (e) {
                if (!live.current || e instanceof SignInRequiredError) return;
                if (e instanceof ApiError && e.status === 404) setMissing(true);
                else setLoadFailed(true);
            }
        };
        void once();
    }, [api, planId, pollMs]);

    useEffect(() => {
        live.current = true;
        load();
        return () => {
            live.current = false;
            clearTimeout(timer.current);
            clearTimeout(toastTimer.current);
        };
    }, [load]);

    const showToast = (next: Toast) => {
        clearTimeout(toastTimer.current);
        setToast(next);
        toastTimer.current = setTimeout(() => setToast(null), TOAST_MS);
    };

    /** One change at a time: the buttons are off while a call runs, and a failure leaves the plan as it was. */
    const run = async (key: string, call: () => Promise<MealPlan>, then?: () => void) => {
        if (busy) return;
        setBusy(key);
        setProblem(null);
        try {
            const next = await call();
            if (!live.current) return;
            setPlan(next);
            then?.();
        } catch (e) {
            if (e instanceof SignInRequiredError) return;
            setProblem(planError(e, "Couldn't change the plan. Try again."));
        } finally {
            if (live.current) setBusy(null);
        }
    };

    const remove = async (meal: PlanMeal) => {
        if (busy || !plan) return;
        setBusy(`delete:${meal.id}`);
        setProblem(null);
        try {
            const deleted = await api.deletePlannedMeal(plan.id, meal.id);
            if (!live.current) return;
            setPlan(deleted.plan);
            showToast({
                text: `Deleted ${meal.title}`,
                undo: () => {
                    setToast(null);
                    void run("undo", () => api.restorePlannedMeal(plan.id, deleted.meal));
                },
            });
        } catch (e) {
            if (!(e instanceof SignInRequiredError))
                setProblem(planError(e, "Couldn't delete that. Try again."));
        } finally {
            if (live.current) setBusy(null);
        }
    };

    const back = () => goBack(PLAN_PATH);

    const header = (title: string, subtitle?: string) => (
        <>
            <div className={styles.top}>
                <a
                    href={PLAN_PATH}
                    className={styles.back}
                    aria-label="Back to choices"
                    onClick={(e) => {
                        e.preventDefault();
                        back();
                    }}
                >
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
                        <path d="M15 6l-6 6 6 6" />
                    </svg>
                </a>
                <span className={styles.step}>Plan meals · Step 2 of 3</span>
            </div>
            <header className={styles.header}>
                <h1 className={styles.title}>{title}</h1>
                {subtitle && <div className={styles.subtitle}>{subtitle}</div>}
            </header>
        </>
    );

    if (missing) {
        return (
            <div className={styles.screen}>
                {header("Your meals")}
                <div className={styles.content}>
                    <p className={styles.note} role="alert">
                        That plan isn&apos;t there any more.
                    </p>
                    <button type="button" className={styles.primary} onClick={back}>
                        Back to choices
                    </button>
                </div>
            </div>
        );
    }

    if (loadFailed && !plan) {
        return (
            <div className={styles.screen}>
                {header("Your meals")}
                <div className={styles.content}>
                    <div className={styles.problem}>
                        <p className={styles.note} role="alert">
                            Couldn&apos;t load the plan.
                        </p>
                        <button type="button" className={styles.link} onClick={load}>
                            Try again
                        </button>
                    </div>
                </div>
            </div>
        );
    }

    if (!plan || plan.status === "generating") {
        const count = plan ? plan.choices.days * plan.choices.mealTypes.length : null;
        return (
            <div className={styles.screen}>
                {header("Your meals")}
                <div className={styles.loading} role="status">
                    <span className={styles.spinner} aria-hidden="true" />
                    {count === null ? "Planning…" : `Planning ${mealsText(count)}…`}
                    <span className={styles.subtitle}>Looking at what you have. This can take a minute.</span>
                </div>
            </div>
        );
    }

    if (plan.status === "failed") {
        return (
            <div className={styles.screen}>
                {header("Your meals")}
                <div className={styles.content}>
                    <p className={styles.note} role="alert">
                        {plan.error ?? "Couldn't plan just now. Try again."}
                    </p>
                    <button type="button" className={styles.primary} onClick={back}>
                        Back to choices
                    </button>
                </div>
            </div>
        );
    }

    const working = busy !== null;
    return (
        <div className={styles.screen}>
            {header("Your meals", planSummary(plan))}
            <div className={styles.content}>
                {problem && (
                    <p className={styles.note} role="alert">
                        {problem}
                    </p>
                )}
                {working && (
                    <div className={styles.subtitle} role="status">
                        Working on it…
                    </div>
                )}

                <form
                    className={styles.whole}
                    onSubmit={(e) => {
                        e.preventDefault();
                        const comments = whole.trim();
                        if (comments)
                            void run(
                                "whole",
                                () => api.adjustMealPlan(plan.id, comments),
                                () => setWhole(""),
                            );
                    }}
                >
                    <label htmlFor="whole" className={styles.wholeLabel}>
                        Adjust the whole plan
                    </label>
                    <div className={styles.wholeRow}>
                        <input
                            id="whole"
                            type="text"
                            className={styles.input}
                            maxLength={MAX_COMMENTS}
                            placeholder="e.g. cheaper, more fish"
                            value={whole}
                            onChange={(e) => setWhole(e.target.value)}
                        />
                        <button
                            type="submit"
                            className={styles.outline}
                            disabled={working || whole.trim() === ""}
                        >
                            {busy === "whole" ? "Adjusting…" : "Adjust"}
                        </button>
                    </div>
                    <div className={styles.help}>Meals you&apos;ve marked Keep stay as they are.</div>
                </form>

                {MEAL_TYPES.filter(
                    (t) => plan.choices.mealTypes.includes(t.id) || mealsOf(plan, t.id).length > 0,
                ).map((t) => {
                    const meals = mealsOf(plan, t.id);
                    return (
                        <section key={t.id} className={styles.section} aria-labelledby={`group-${t.id}`}>
                            <h2 id={`group-${t.id}`} className={styles.sectionTitle}>
                                <span>{t.title}</span>
                                <span className={styles.sectionCount}>{mealsText(meals.length)}</span>
                            </h2>
                            {meals.map((meal) => (
                                <MealCard
                                    key={meal.id}
                                    meal={meal}
                                    working={working}
                                    busy={busy}
                                    adjusting={adjusting === meal.id}
                                    adjustText={adjustText}
                                    onAdjustText={setAdjustText}
                                    onOpenAdjust={() => {
                                        setAdjusting(meal.id);
                                        setAdjustText("");
                                    }}
                                    onCloseAdjust={() => setAdjusting(null)}
                                    onAdjust={() => {
                                        const comments = adjustText.trim();
                                        if (comments)
                                            void run(
                                                `adjust:${meal.id}`,
                                                () => api.adjustPlannedMeal(plan.id, meal.id, comments),
                                                () => setAdjusting(null),
                                            );
                                    }}
                                    onKeep={() =>
                                        void run(`keep:${meal.id}`, () =>
                                            api.keepPlannedMeal(plan.id, meal.id, !meal.keep),
                                        )
                                    }
                                    onAnother={() =>
                                        void run(`another:${meal.id}`, () =>
                                            api.anotherPlannedMeal(plan.id, meal.id),
                                        )
                                    }
                                    onDelete={() => void remove(meal)}
                                />
                            ))}
                            <button
                                type="button"
                                className={styles.addMeal}
                                disabled={working}
                                onClick={() =>
                                    void run(`add:${t.id}`, () =>
                                        api.addPlannedMeal(plan.id, t.id as PlanMealType),
                                    )
                                }
                            >
                                <svg
                                    width="18"
                                    height="18"
                                    viewBox="0 0 24 24"
                                    fill="none"
                                    stroke="currentColor"
                                    strokeWidth="2.4"
                                    strokeLinecap="round"
                                    aria-hidden="true"
                                >
                                    <path d="M12 5v14M5 12h14" />
                                </svg>
                                {busy === `add:${t.id}` ? "Adding…" : `Add a ${t.word}`}
                            </button>
                        </section>
                    );
                })}
            </div>

            {toast && (
                <div className={styles.toast} role="status">
                    <span className={styles.toastText}>{toast.text}</span>
                    {toast.undo && (
                        <button type="button" className={styles.toastUndo} onClick={toast.undo}>
                            Undo
                        </button>
                    )}
                </div>
            )}
        </div>
    );
}

function MealCard({
    meal,
    working,
    busy,
    adjusting,
    adjustText,
    onAdjustText,
    onOpenAdjust,
    onCloseAdjust,
    onAdjust,
    onKeep,
    onAnother,
    onDelete,
}: {
    meal: PlanMeal;
    working: boolean;
    busy: string | null;
    adjusting: boolean;
    adjustText: string;
    onAdjustText: (text: string) => void;
    onOpenAdjust: () => void;
    onCloseAdjust: () => void;
    onAdjust: () => void;
    onKeep: () => void;
    onAnother: () => void;
    onDelete: () => void;
}) {
    const shares = sharesText(meal);
    const inputId = `adjust-${meal.id}`;
    return (
        <article
            className={meal.keep ? `${styles.card} ${styles.cardKept}` : styles.card}
            aria-label={meal.title}
        >
            <div className={styles.cardHead}>
                <div className={styles.cardText}>
                    <div className={styles.mealTitle}>{meal.title}</div>
                    <div className={styles.mealSummary}>{meal.summary}</div>
                </div>
                <button
                    type="button"
                    className={styles.keep}
                    aria-pressed={meal.keep}
                    disabled={working}
                    onClick={onKeep}
                >
                    <svg
                        width="14"
                        height="14"
                        viewBox="0 0 24 24"
                        fill={meal.keep ? "currentColor" : "none"}
                        stroke="currentColor"
                        strokeWidth="2.2"
                        strokeLinejoin="round"
                        aria-hidden="true"
                    >
                        <path d="M7 3h10v18l-5-4-5 4z" />
                    </svg>
                    Keep
                </button>
            </div>
            <div className={styles.facts}>
                <span className={styles.factUses}>
                    Uses {meal.usesCount} {meal.usesCount === 1 ? "thing" : "things"} you have
                </span>
                {shares && <span className={styles.factShares}>{shares}</span>}
                {meal.useFirst.length > 0 && (
                    <span className={styles.chip}>Use first: {meal.useFirst.join(", ")}</span>
                )}
            </div>
            <CheckBeforeCooking warnings={meal.warnings} />

            {adjusting && (
                <div className={styles.adjust}>
                    <label htmlFor={inputId} className={styles.adjustLabel}>
                        What would you change?
                    </label>
                    <textarea
                        id={inputId}
                        className={styles.comments}
                        rows={2}
                        maxLength={MAX_COMMENTS}
                        placeholder="e.g. no peppers, make it quicker"
                        value={adjustText}
                        onChange={(e) => onAdjustText(e.target.value)}
                        autoFocus
                    />
                    <div className={styles.adjustButtons}>
                        <button type="button" className={styles.cancel} onClick={onCloseAdjust}>
                            Cancel
                        </button>
                        <button
                            type="button"
                            className={styles.confirm}
                            disabled={working || adjustText.trim() === ""}
                            onClick={onAdjust}
                        >
                            {busy === `adjust:${meal.id}` ? "Adjusting…" : "Adjust"}
                        </button>
                    </div>
                </div>
            )}

            <div className={styles.actions}>
                <button type="button" className={styles.action} disabled={working} onClick={onOpenAdjust}>
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
                        <path d="M4 20h4L19 9l-4-4L4 16z" />
                    </svg>
                    Adjust
                </button>
                <button type="button" className={styles.action} disabled={working} onClick={onAnother}>
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
                        <path d="M20 12a8 8 0 1 1-2.3-5.7M20 4v5h-5" />
                    </svg>
                    {busy === `another:${meal.id}` ? "Finding another…" : "Another one"}
                </button>
                <button
                    type="button"
                    className={styles.delete}
                    aria-label={`Delete ${meal.title}`}
                    disabled={working}
                    onClick={onDelete}
                >
                    <svg
                        width="18"
                        height="18"
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        strokeWidth="2"
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        aria-hidden="true"
                    >
                        <path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13" />
                    </svg>
                </button>
            </div>
        </article>
    );
}
