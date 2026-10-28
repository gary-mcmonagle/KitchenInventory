import { useEffect, useState } from "react";
import {
    KITS,
    SignInRequiredError,
    type ApiClient,
    type Diet,
    type Kit,
    type PlanMealType,
    type StockProduct,
} from "./api";
import { expiryBadge } from "./format";
import { DIETS } from "./Plates";
import { MAX_COMMENTS, MEAL_TYPES, mealsText, planError, planLabel } from "./plans";
import styles from "./Plans.module.css";
import { goBack, LIST_PATH, navigate, planPath } from "./router";

const ALL_KIT: Kit[] = KITS.map((k) => k.id);
const COUNT_FROM = 240;

/** "Chicken breast · tomorrow", the amber chip for a use-first product (as on Hungry?). */
function chipText(p: StockProduct): string {
    if (p.daysUntilSoonest === null || p.soonestEstimatedExpiryDate === null) return p.displayName;
    return `${p.displayName} · ${expiryBadge(p.daysUntilSoonest, p.soonestEstimatedExpiryDate).text}`;
}

/**
 * Plan meals, step 1: choose (#119, project/PlanChoose.dc.html). How many days, which meals (one per day per type), meat or
 * veg, kit, whether to build around what needs using up (off unless ticked, as on Hungry?) and a wish. **Plan N meals**
 * queues the plan and opens it (step 2), which waits for the meals to be written.
 */
export function PlanChooseScreen({ api }: { api: ApiClient }) {
    const [days, setDays] = useState(3);
    const [meals, setMeals] = useState<ReadonlySet<PlanMealType>>(new Set(["dinner"]));
    const [diet, setDiet] = useState<Diet>("any");
    const [equipment, setEquipment] = useState<Kit[]>(ALL_KIT);
    const [prefer, setPrefer] = useState(false);
    const [comments, setComments] = useState("");
    const [useFirst, setUseFirst] = useState<StockProduct[]>([]);
    const [busy, setBusy] = useState(false);
    const [problem, setProblem] = useState<string | null>(null);

    useEffect(() => {
        let live = true;
        Promise.resolve()
            .then(() => api.stock())
            .then((s) => live && setUseFirst(s.products.filter((p) => p.status === "known" && p.useFirst)))
            .catch(() => undefined); // only the amber chips depend on it
        return () => {
            live = false;
        };
    }, [api]);

    const total = days * meals.size;
    const noKit = equipment.length === 0;
    const things = `${useFirst.length} ${useFirst.length === 1 ? "thing" : "things"}`;
    const dietHelp = DIETS.find((d) => d.id === diet)!.help;

    const submit = async (event: React.FormEvent) => {
        event.preventDefault();
        if (busy || total === 0 || noKit) return;
        setBusy(true);
        setProblem(null);
        try {
            const plan = await api.createMealPlan({
                days,
                meals: MEAL_TYPES.map((t) => t.id).filter((t) => meals.has(t)),
                diet,
                equipment: equipment.length === ALL_KIT.length ? null : equipment,
                preferUseFirst: prefer && useFirst.length > 0,
                comments: comments.trim() || null,
            });
            navigate(planPath(plan.id));
        } catch (e) {
            if (e instanceof SignInRequiredError) return;
            setProblem(planError(e, "Couldn't start the plan. Try again."));
            setBusy(false);
        }
    };

    return (
        <div className={styles.screen}>
            <div className={styles.top}>
                <a
                    href={LIST_PATH}
                    className={styles.back}
                    aria-label="Back"
                    onClick={(e) => {
                        e.preventDefault();
                        goBack(LIST_PATH);
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
                <span className={styles.step}>Plan meals · Step 1 of 3</span>
            </div>
            <header className={styles.header}>
                <h1 className={styles.title}>Plan meals</h1>
                <div className={styles.subtitle}>Built from what you have, plus things you usually buy.</div>
            </header>

            <form className={styles.content} onSubmit={(e) => void submit(e)} noValidate>
                <div className={styles.group}>
                    <div id="days-label" className={styles.label}>
                        How many days?
                    </div>
                    <div role="group" aria-labelledby="days-label" className={styles.stepper}>
                        <button
                            type="button"
                            className={styles.stepButton}
                            aria-label="One day fewer"
                            aria-disabled={days === 1}
                            onClick={() => setDays((d) => Math.max(1, d - 1))}
                        >
                            <svg
                                width="20"
                                height="20"
                                viewBox="0 0 24 24"
                                fill="none"
                                stroke="currentColor"
                                strokeWidth="2.6"
                                strokeLinecap="round"
                                aria-hidden="true"
                            >
                                <path d="M5 12h14" />
                            </svg>
                        </button>
                        <div className={styles.days} aria-live="polite">
                            {days === 1 ? "1 day" : `${days} days`}
                        </div>
                        <button
                            type="button"
                            className={styles.stepButton}
                            aria-label="One more day"
                            aria-disabled={days === 7}
                            onClick={() => setDays((d) => Math.min(7, d + 1))}
                        >
                            <svg
                                width="20"
                                height="20"
                                viewBox="0 0 24 24"
                                fill="none"
                                stroke="currentColor"
                                strokeWidth="2.6"
                                strokeLinecap="round"
                                aria-hidden="true"
                            >
                                <path d="M12 5v14M5 12h14" />
                            </svg>
                        </button>
                    </div>
                </div>

                <div className={styles.group}>
                    <div id="meals-label" className={styles.label}>
                        Which meals?
                    </div>
                    <div role="group" aria-labelledby="meals-label" className={styles.mealList}>
                        {MEAL_TYPES.map((t) => {
                            const on = meals.has(t.id);
                            return (
                                <label key={t.id} className={styles.mealRow}>
                                    <input
                                        type="checkbox"
                                        className={styles.checkbox}
                                        checked={on}
                                        onChange={() =>
                                            setMeals((m) => {
                                                const next = new Set(m);
                                                if (on) next.delete(t.id);
                                                else next.add(t.id);
                                                return next;
                                            })
                                        }
                                    />
                                    <span className={styles.mealName}>{t.title}</span>
                                    <span className={styles.aside}>{on ? mealsText(days) : ""}</span>
                                </label>
                            );
                        })}
                    </div>
                </div>

                <div className={styles.group}>
                    <div id="diet-label" className={styles.label}>
                        Meat or veg?
                    </div>
                    <div role="radiogroup" aria-labelledby="diet-label" className={styles.diets}>
                        {DIETS.map((d) => (
                            <button
                                key={d.id}
                                type="button"
                                role="radio"
                                aria-checked={diet === d.id}
                                className={styles.diet}
                                onClick={() => setDiet(d.id)}
                            >
                                {d.label}
                            </button>
                        ))}
                    </div>
                    <div className={styles.help}>{dietHelp}</div>
                </div>

                <div className={styles.group}>
                    <div className={styles.labelRow}>
                        <div id="kit-label" className={styles.label}>
                            What can you use?
                        </div>
                        <span className={styles.aside}>
                            {equipment.length} of {KITS.length}
                        </span>
                    </div>
                    <div role="group" aria-labelledby="kit-label" className={styles.kitChips}>
                        {KITS.map((k) => {
                            const on = equipment.includes(k.id);
                            return (
                                <button
                                    key={k.id}
                                    type="button"
                                    aria-pressed={on}
                                    className={on ? `${styles.kitChip} ${styles.kitChipOn}` : styles.kitChip}
                                    onClick={() =>
                                        setEquipment((e) =>
                                            on
                                                ? e.filter((x) => x !== k.id)
                                                : ALL_KIT.filter((id) => id === k.id || e.includes(id)),
                                        )
                                    }
                                >
                                    {on && (
                                        <svg
                                            width="14"
                                            height="14"
                                            viewBox="0 0 24 24"
                                            fill="none"
                                            stroke="currentColor"
                                            strokeWidth="3"
                                            strokeLinecap="round"
                                            strokeLinejoin="round"
                                            aria-hidden="true"
                                        >
                                            <path d="M5 12.5l4.5 4.5L19 7.5" />
                                        </svg>
                                    )}
                                    {k.label}
                                </button>
                            );
                        })}
                    </div>
                </div>

                <div className={styles.preferCard}>
                    <label className={styles.preferLabel}>
                        <input
                            type="checkbox"
                            className={styles.checkbox}
                            checked={prefer && useFirst.length > 0}
                            disabled={useFirst.length === 0}
                            onChange={(e) => setPrefer(e.target.checked)}
                        />
                        <span className={styles.preferText}>Prefer ingredients that need using</span>
                    </label>
                    <div className={`${styles.help} ${styles.indent}`}>
                        {useFirst.length === 0
                            ? "Nothing needs using up right now."
                            : prefer
                              ? `Plans meals around the ${things} to use first.`
                              : "Chooses freely from what you have."}
                    </div>
                    {prefer && useFirst.length > 0 && (
                        <div className={`${styles.chips} ${styles.indent}`}>
                            {useFirst.map((p) => (
                                <span key={p.productId} className={styles.chip}>
                                    {chipText(p)}
                                </span>
                            ))}
                        </div>
                    )}
                </div>

                <div className={styles.group}>
                    <div className={styles.labelRow}>
                        <label htmlFor="plan-comments" className={styles.label}>
                            Anything else?
                        </label>
                        <span className={styles.aside}>Optional</span>
                    </div>
                    <textarea
                        id="plan-comments"
                        className={styles.comments}
                        rows={3}
                        maxLength={MAX_COMMENTS}
                        placeholder="e.g. quick lunches, fish once, nothing too spicy"
                        value={comments}
                        onChange={(e) => setComments(e.target.value.slice(0, MAX_COMMENTS))}
                    />
                    {comments.length > COUNT_FROM && (
                        <div className={styles.count} aria-live="polite">
                            {comments.length} / {MAX_COMMENTS}
                        </div>
                    )}
                </div>

                <div className={styles.group}>
                    {problem && (
                        <p className={styles.note} role="alert">
                            {problem}
                        </p>
                    )}
                    {noKit && total > 0 && (
                        <p className={styles.note}>Tick at least one thing you can use.</p>
                    )}
                    <button
                        type="submit"
                        className={styles.primary}
                        disabled={busy || total === 0 || noKit}
                        aria-disabled={busy || total === 0 || noKit}
                    >
                        {total === 0 ? "Pick at least one meal" : busy ? "Starting…" : planLabel(total)}
                    </button>
                    <div className={styles.footnote}>
                        Meals aren&apos;t tied to days. Make them in any order.
                    </div>
                </div>
            </form>
        </div>
    );
}
