import { useEffect, useRef, useState } from "react";
import type { RatedDish, Rating } from "./api";
import {
    cookedAfter,
    inTab,
    mealName,
    reasonsText,
    snapshotOf,
    whenText,
    whoText,
    type TasteTab,
} from "./ratings";
import styles from "./Ratings.module.css";
import { goBack, RECIPES_PATH } from "./router";
import { useOptionalRatings } from "./useRatings";

const TOAST_MS = 6000;

/**
 * Our taste (#128, project/OurTaste.dc.html): the household's ratings, **Liked** and **Not for us**, newest first. A dish people
 * disagree about is in both tabs. You can change or remove your own rating from its row (with Undo), never someone else's.
 * The top of the page is kept for the taste profile (#129): nothing is shown there yet.
 */
export function OurTasteScreen({ now }: { now?: Date }) {
    const ratings = useOptionalRatings();
    const [tab, setTab] = useState<TasteTab>("liked");
    const [toast, setToast] = useState<{ text: string; undo?: () => void } | null>(null);
    const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    const refresh = ratings?.refresh;
    useEffect(() => refresh?.(), [refresh]); // someone else at home may have rated since this was last read
    useEffect(() => () => clearTimeout(timer.current), []);

    const showToast = (text: string, undo?: () => void) => {
        clearTimeout(timer.current);
        setToast({ text, undo });
        timer.current = setTimeout(() => setToast(null), TOAST_MS);
    };

    const liked = ratings?.dishes.filter((d) => inTab(d, "liked")) ?? [];
    const not = ratings?.dishes.filter((d) => inTab(d, "not")) ?? [];
    const shown = tab === "liked" ? liked : not;

    const remove = async (rating: Rating) => {
        if (!ratings) return;
        if (!(await ratings.clear(rating))) return showToast("Couldn't remove that. Try again.");
        showToast("Removed your rating", () => {
            setToast(null);
            void ratings.restore(rating).then((ok) => !ok && showToast("Couldn't put that back. Try again."));
        });
    };

    const change = async (rating: Rating) => {
        if (!ratings) return;
        const next = rating.rating === "up" ? "down" : "up";
        const saved = await ratings.rate(snapshotOf(rating), next, rating.context);
        if (!saved) return showToast("Couldn't save that. Try again.");
        showToast(next === "up" ? "Changed to 👍" : "Changed to 👎", () => {
            setToast(null);
            void ratings.restore(rating).then((ok) => !ok && showToast("Couldn't put that back. Try again."));
        });
    };

    return (
        <div className={styles.page}>
            <div className={styles.top}>
                <a
                    href={RECIPES_PATH}
                    className={styles.back}
                    aria-label="Back to Hungry?"
                    onClick={(e) => {
                        e.preventDefault();
                        goBack(RECIPES_PATH);
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
            </div>
            <header className={styles.header}>
                <h1 className={styles.title}>Our taste</h1>
                <div className={styles.subtitle}>What you&apos;ve liked and not.</div>
            </header>
            <div className={styles.body}>
                {!ratings || (!ratings.loaded && !ratings.failed) ? (
                    <div className={styles.empty} role="status">
                        Loading…
                    </div>
                ) : !ratings.loaded ? (
                    <div className={styles.errorCard} role="alert">
                        <span>Couldn&apos;t load the ratings.</span>
                        <button type="button" onClick={ratings.refresh}>
                            Try again
                        </button>
                    </div>
                ) : (
                    <>
                        <div role="tablist" aria-label="Ratings" className={styles.tabs}>
                            {(
                                [
                                    ["liked", "Liked", liked.length],
                                    ["not", "Not for us", not.length],
                                ] as const
                            ).map(([id, label, count]) => (
                                <button
                                    key={id}
                                    type="button"
                                    role="tab"
                                    className={styles.tab}
                                    aria-selected={tab === id}
                                    onClick={() => setTab(id)}
                                >
                                    {label} · {count}
                                </button>
                            ))}
                        </div>
                        {shown.length === 0 ? (
                            <div className={styles.empty} role="tabpanel">
                                {tab === "liked"
                                    ? "Nothing here yet. Tap Like on something you'd make again."
                                    : "Nothing here yet. Tap Not for us on something that wasn't right."}
                            </div>
                        ) : (
                            <ul role="tabpanel" className={styles.rows}>
                                {shown.map((dish) => (
                                    <DishRow
                                        key={`${dish.kind}|${dish.key}`}
                                        dish={dish}
                                        tab={tab}
                                        now={now}
                                        onRemove={remove}
                                        onChange={change}
                                    />
                                ))}
                            </ul>
                        )}
                        <div className={styles.footnote}>
                            You can change or remove your own ratings. Other people&apos;s are theirs.
                        </div>
                    </>
                )}
            </div>
            {toast && (
                <div className={styles.toast} role="status">
                    <span>{toast.text}</span>
                    {toast.undo && (
                        <button type="button" className={styles.undo} onClick={toast.undo}>
                            Undo
                        </button>
                    )}
                </div>
            )}
        </div>
    );
}

function DishRow({
    dish,
    tab,
    now,
    onRemove,
    onChange,
}: {
    dish: RatedDish;
    tab: TasteTab;
    now?: Date;
    onRemove: (rating: Rating) => void;
    onChange: (rating: Rating) => void;
}) {
    const mine = dish.ratings.find((r) => r.mine);
    const reasons = tab === "not" ? reasonsText(dish) : "";
    return (
        <li className={styles.row}>
            <div className={styles.rowText}>
                <div className={styles.rowTitleLine}>
                    <span className={styles.rowTitle}>{dish.title}</span>
                    {cookedAfter(dish) && <span className={styles.cooked}>After cooking</span>}
                </div>
                <div className={styles.rowMeta}>
                    {mealName(dish.meal)} · {whenText(dish.latestAt, now)}
                </div>
                <div className={styles.rowWho}>{whoText(dish)}</div>
                {reasons && <div className={styles.rowReasons}>{reasons}</div>}
            </div>
            {mine && (
                <div className={styles.rowActions}>
                    <button
                        type="button"
                        className={styles.remove}
                        aria-label={`Remove your rating of ${dish.title}`}
                        onClick={() => onRemove(mine)}
                    >
                        <svg
                            width="18"
                            height="18"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2.2"
                            strokeLinecap="round"
                            aria-hidden="true"
                        >
                            <path d="M6 6l12 12M18 6L6 18" />
                        </svg>
                    </button>
                    <button
                        type="button"
                        className={styles.change}
                        aria-label={`Change your rating of ${dish.title} to ${mine.rating === "up" ? "not for us" : "like"}`}
                        onClick={() => onChange(mine)}
                    >
                        {mine.rating === "up" ? "Change to 👎" : "Change to 👍"}
                    </button>
                </div>
            )}
        </li>
    );
}
