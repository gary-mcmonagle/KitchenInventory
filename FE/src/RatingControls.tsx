import { useEffect, useId, useRef, useState } from "react";
import type { Rating, RatingContext, RatingReason, RatingValue } from "./api";
import { REASONS, type RatingSnapshot } from "./ratings";
import styles from "./Ratings.module.css";
import { useOptionalRatings } from "./useRatings";

/** 👍 / 👎 on a recipe or idea (#128, project/RateRecipe.dc.html). They show only inside the signed-in app (they need the ratings). */

const NOTE_MS = 2500;

type Note = "saved" | "failed" | null;

function Thumb({ size, filled, down = false }: { size: number; filled: boolean; down?: boolean }) {
    return (
        <svg
            width={size}
            height={size}
            viewBox="0 0 24 24"
            fill={filled ? "currentColor" : "none"}
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
            className={down ? styles.thumbDown : undefined}
        >
            <path d="M7 10v11H4V10z" />
            <path d="M7 10l4-7a2 2 0 0 1 3 2l-1 5h6a2 2 0 0 1 2 2.3l-1.5 7A2 2 0 0 1 17.5 21H7" />
        </svg>
    );
}

/** The state behind both controls: your rating of this dish, pressing, the short "Saved" tick, and the reasons sheet. */
function useRate(snapshot: RatingSnapshot, context: RatingContext) {
    const ratings = useOptionalRatings();
    const [note, setNote] = useState<Note>(null);
    const [sheet, setSheet] = useState(false);
    const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    useEffect(() => () => clearTimeout(timer.current), []);
    const show = (n: Note) => {
        clearTimeout(timer.current);
        setNote(n);
        if (n === "saved") timer.current = setTimeout(() => setNote(null), NOTE_MS);
    };
    const mine = ratings?.mine(snapshot.kind, snapshot.title);

    const press = async (value: RatingValue) => {
        if (!ratings) return;
        if (mine?.rating === value) {
            // Tapping the pressed one again clears the rating.
            show((await ratings.clear(mine)) ? null : "failed");
            return;
        }
        const saved = await ratings.rate(snapshot, value, context);
        show(saved ? "saved" : "failed");
        setSheet(saved !== null && value === "down"); // the 👎 is already saved: the sheet only adds the reasons
    };

    const finish = async (rating: Rating, reasons: RatingReason[], ingredients: string[]) => {
        setSheet(false);
        if (!ratings) return;
        if (reasons.length === 0 && rating.reasons.length === 0) return;
        show((await ratings.setReasons(rating.id, reasons, ingredients)) ? "saved" : "failed");
    };

    return {
        available: ratings !== null,
        mine,
        note,
        sheet: sheet && mine?.rating === "down" ? mine : null,
        press,
        finish,
        close: () => setSheet(false),
    };
}

const noteText = (note: Note): string => (note === "failed" ? "Couldn't save that. Try again." : "Saved");

/** "Fancy this?" with Like and Not for us, under a recipe's heading. */
export function RatePills({ snapshot, context }: { snapshot: RatingSnapshot; context: RatingContext }) {
    const rate = useRate(snapshot, context);
    if (!rate.available) return null;
    const up = rate.mine?.rating === "up";
    const down = rate.mine?.rating === "down";
    return (
        <>
            <div className={styles.rateRow}>
                <span className={styles.rateLabel}>Fancy this?</span>
                <button
                    type="button"
                    className={`${styles.pill} ${styles.pillUp}`}
                    aria-pressed={up}
                    onClick={() => void rate.press("up")}
                >
                    <Thumb size={18} filled={up} />
                    Like
                </button>
                <button
                    type="button"
                    className={`${styles.pill} ${styles.pillDown}`}
                    aria-pressed={down}
                    onClick={() => void rate.press("down")}
                >
                    <Thumb size={18} filled={down} down />
                    Not for us
                </button>
            </div>
            {rate.note && (
                <div
                    role="status"
                    className={rate.note === "failed" ? `${styles.note} ${styles.noteFailed}` : styles.note}
                >
                    {noteText(rate.note)}
                </div>
            )}
            {rate.sheet && (
                <ReasonsSheet
                    rating={rate.sheet}
                    ingredients={snapshot.ingredients.map((i) => i.name)}
                    onDone={(reasons, ingredients) => void rate.finish(rate.sheet!, reasons, ingredients)}
                    onSkip={rate.close}
                />
            )}
        </>
    );
}

/** The two icon buttons on a Ready now idea. */
export function RateIcons({ snapshot, context }: { snapshot: RatingSnapshot; context: RatingContext }) {
    const rate = useRate(snapshot, context);
    if (!rate.available) return null;
    const up = rate.mine?.rating === "up";
    const down = rate.mine?.rating === "down";
    return (
        <>
            <div className={styles.iconRow}>
                {rate.note && (
                    <span role="status" className={styles.iconNote}>
                        {noteText(rate.note)}
                    </span>
                )}
                <button
                    type="button"
                    className={`${styles.icon} ${styles.iconUp}`}
                    aria-pressed={up}
                    aria-label="Like"
                    onClick={() => void rate.press("up")}
                >
                    <Thumb size={20} filled={up} />
                </button>
                <button
                    type="button"
                    className={`${styles.icon} ${styles.iconDown}`}
                    aria-pressed={down}
                    aria-label="Not for us"
                    onClick={() => void rate.press("down")}
                >
                    <Thumb size={20} filled={down} down />
                </button>
            </div>
            {rate.sheet && (
                <ReasonsSheet
                    rating={rate.sheet}
                    ingredients={snapshot.ingredients.map((i) => i.name)}
                    onDone={(reasons, ingredients) => void rate.finish(rate.sheet!, reasons, ingredients)}
                    onSkip={rate.close}
                />
            )}
        </>
    );
}

/**
 * "What wasn't right?" after a 👎: optional reasons, skippable in one tap. The 👎 is saved already, so closing it never loses
 * it. "Didn't like an ingredient" shows the dish's ingredients to pick from (and is left out when it has none; if none is
 * picked the reason isn't kept, since it says nothing).
 */
export function ReasonsSheet({
    rating,
    ingredients,
    onDone,
    onSkip,
}: {
    rating: Rating;
    ingredients: string[];
    onDone: (reasons: RatingReason[], ingredients: string[]) => void;
    onSkip: () => void;
}) {
    const titleId = useId();
    const whichId = useId();
    const [picked, setPicked] = useState<RatingReason[]>(rating.reasons);
    const [which, setWhich] = useState<string[]>(rating.dislikedIngredients);
    const sheet = useRef<HTMLDivElement>(null);
    useEffect(() => sheet.current?.focus(), []);

    const flip = <T,>(list: T[], item: T): T[] =>
        list.includes(item) ? list.filter((x) => x !== item) : [...list, item];
    const offered = REASONS.filter((r) => r.id !== "ingredient" || ingredients.length > 0);

    const done = () => {
        const ordered = REASONS.map((r) => r.id).filter((id) => picked.includes(id));
        const kept = which.length > 0 ? ordered : ordered.filter((id) => id !== "ingredient");
        onDone(kept, kept.includes("ingredient") ? which : []);
    };

    return (
        <>
            <div className={styles.scrim} aria-hidden="true" onClick={onSkip} />
            <div
                ref={sheet}
                className={styles.sheet}
                role="dialog"
                aria-modal="true"
                aria-labelledby={titleId}
                tabIndex={-1}
                onKeyDown={(e) => e.key === "Escape" && onSkip()}
            >
                <div className={styles.grab} aria-hidden="true" />
                <div>
                    <h2 id={titleId} className={styles.sheetTitle}>
                        What wasn&apos;t right?
                    </h2>
                    <div className={styles.sheetSub}>Optional. It helps the next ideas.</div>
                </div>
                <div role="group" aria-labelledby={titleId} className={styles.chips}>
                    {offered.map((r) => (
                        <button
                            key={r.id}
                            type="button"
                            className={styles.chip}
                            aria-pressed={picked.includes(r.id)}
                            onClick={() => setPicked((p) => flip(p, r.id))}
                        >
                            {r.label}
                        </button>
                    ))}
                </div>
                {picked.includes("ingredient") && ingredients.length > 0 && (
                    <div className={styles.which}>
                        <div id={whichId} className={styles.whichTitle}>
                            Which one?
                        </div>
                        <div role="group" aria-labelledby={whichId} className={styles.chips}>
                            {ingredients.map((name) => (
                                <button
                                    key={name}
                                    type="button"
                                    className={`${styles.chip} ${styles.smallChip}`}
                                    aria-pressed={which.includes(name)}
                                    onClick={() => setWhich((w) => flip(w, name))}
                                >
                                    {name}
                                </button>
                            ))}
                        </div>
                    </div>
                )}
                <div className={styles.sheetButtons}>
                    <button type="button" className={styles.skip} onClick={onSkip}>
                        Skip
                    </button>
                    <button type="button" className={styles.done} onClick={done}>
                        Done
                    </button>
                </div>
            </div>
        </>
    );
}
