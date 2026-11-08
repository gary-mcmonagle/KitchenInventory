import {
    createContext,
    useCallback,
    useContext,
    useEffect,
    useMemo,
    useRef,
    useState,
    type ReactNode,
} from "react";
import {
    SignInRequiredError,
    type ApiClient,
    type RatedDish,
    type Rating,
    type RatingContext,
    type RatingReason,
    type RatingValue,
} from "./api";
import {
    bodyOf,
    dishKey,
    normalizeTitle,
    totalRatings,
    withoutRating,
    withRating,
    type RatingSnapshot,
} from "./ratings";
import { newId } from "./shoppingList";

/** The household's ratings (#128): what's on screen asks it for the signed-in person's rating, and changes go through it. */
export interface Ratings {
    loaded: boolean;
    failed: boolean;
    dishes: RatedDish[];
    /** How many ratings there are in all (everyone's). */
    total: number;
    /** The signed-in person's rating of a dish, if they have rated it. */
    mine: (kind: Rating["kind"], title: string) => Rating | undefined;
    /** Rates (or re-rates: yours is replaced). Resolves the saved rating, or null when it couldn't be saved (the screen goes back as it was). */
    rate: (snapshot: RatingSnapshot, value: RatingValue, context: RatingContext) => Promise<Rating | null>;
    /** Takes your rating back. Resolves false when it couldn't be (the screen goes back as it was). */
    clear: (rating: Rating) => Promise<boolean>;
    /** Changes the reasons of your 👎. */
    setReasons: (id: string, reasons: RatingReason[], dislikedIngredients: string[]) => Promise<boolean>;
    /** Puts a removed rating back (Undo). */
    restore: (rating: Rating) => Promise<boolean>;
    /** Reads them again (someone else may have rated, or saving recorded a 👍). */
    refresh: () => void;
}

const Context = createContext<Ratings | null>(null);

/** The ratings when there are some (screens shown outside the signed-in app, e.g. in tests, have none: no buttons). */
export function useOptionalRatings(): Ratings | null {
    return useContext(Context);
}

export function RatingsProvider({ api, children }: { api: ApiClient; children: ReactNode }) {
    const [dishes, setDishes] = useState<RatedDish[]>([]);
    const [loaded, setLoaded] = useState(false);
    const [failed, setFailed] = useState(false);
    const current = useRef(dishes);
    current.current = dishes;

    const refresh = useCallback(() => {
        Promise.resolve()
            .then(() => api.ratings())
            .then((answer) => {
                setDishes(answer.dishes);
                setLoaded(true);
                setFailed(false);
            })
            .catch((error: unknown) => {
                if (error instanceof SignInRequiredError) return;
                console.warn("Couldn't load the ratings", error);
                setFailed(true);
            });
    }, [api]);

    useEffect(refresh, [refresh]);
    useEffect(() => {
        const onVisible = () => document.visibilityState === "visible" && refresh();
        document.addEventListener("visibilitychange", onVisible);
        return () => document.removeEventListener("visibilitychange", onVisible);
    }, [refresh]);

    /** Applies a change at once and puts things back if the server refuses it. */
    const attempt = useCallback(
        async <T,>(apply: (d: RatedDish[]) => RatedDish[], send: () => Promise<T>): Promise<T | null> => {
            const before = current.current;
            setDishes(apply(before));
            try {
                return await send();
            } catch (error) {
                setDishes(before);
                if (!(error instanceof SignInRequiredError)) console.warn("Couldn't save the rating", error);
                return null;
            }
        },
        [],
    );

    const value = useMemo<Ratings>(() => {
        const mine: Ratings["mine"] = (kind, title) => {
            const key = dishKey(kind, title);
            for (const d of current.current) {
                if (dishKey(d.kind, d.title) !== key) continue;
                return d.ratings.find((r) => r.mine);
            }
            return undefined;
        };
        return {
            loaded,
            failed,
            dishes,
            total: totalRatings(dishes),
            mine,
            rate: async (snapshot, rating, context) => {
                const id = newId();
                const now = new Date().toISOString();
                const body = { ...snapshot, rating, context };
                const guess: Rating = {
                    id,
                    rating,
                    reasons: [],
                    dislikedIngredients: [],
                    context,
                    ratedAt: now,
                    updatedAt: now,
                    personName:
                        current.current.flatMap((d) => d.ratings).find((r) => r.mine)?.personName ?? "You",
                    mine: true,
                    kind: snapshot.kind,
                    title: snapshot.title,
                    key: normalizeTitle(snapshot.title),
                    meal: snapshot.meal,
                    diet: snapshot.diet ?? "any",
                    totalMinutes: snapshot.totalMinutes,
                    ingredients: snapshot.ingredients.map((i) => ({
                        name: i.name,
                        genericName: i.genericName ?? null,
                    })),
                    equipment: snapshot.equipment ?? [],
                    savedRecipeId: snapshot.savedRecipeId ?? null,
                    planId: snapshot.planId ?? null,
                    mealId: snapshot.mealId ?? null,
                };
                const saved = await attempt(
                    (d) => withRating(d, guess),
                    () => api.putRating(id, body),
                );
                if (saved) setDishes((d) => withRating(d, saved));
                return saved;
            },
            clear: async (rating) =>
                (await attempt(
                    (d) => withoutRating(d, rating.id),
                    () => api.deleteRating(rating.id),
                )) !== null,
            setReasons: async (id, reasons, disliked) => {
                const saved = await attempt(
                    (d) =>
                        d.map((dish) => ({
                            ...dish,
                            ratings: dish.ratings.map((r) =>
                                r.id === id ? { ...r, reasons, dislikedIngredients: disliked } : r,
                            ),
                        })),
                    () => api.updateRating(id, { reasons, dislikedIngredients: disliked }),
                );
                if (saved) setDishes((d) => withRating(d, saved));
                return saved !== null;
            },
            restore: async (rating) => {
                const saved = await attempt(
                    (d) => withRating(d, rating),
                    () => api.putRating(rating.id, bodyOf(rating)),
                );
                if (saved) setDishes((d) => withRating(d, saved));
                return saved !== null;
            },
            refresh,
        };
    }, [api, attempt, dishes, failed, loaded, refresh]);

    return <Context.Provider value={value}>{children}</Context.Provider>;
}
