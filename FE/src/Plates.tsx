import type { Diet, PlateFor } from "./api";
import styles from "./Plates.module.css";

/** Meat or veg? (#110): the switch's options, in order, and its help line (project/Hungry.dc.html). */
export const DIETS: Array<{ id: Diet; label: string; help: string }> = [
    { id: "any", label: "Meat & veg", help: "Anything goes." },
    { id: "veg", label: "Veggie", help: "No meat or fish, and nothing cooked with them." },
    {
        id: "half",
        label: "Half & half",
        help: "One meal: half the plates veggie, cooked alongside the meat.",
    },
];

/** "Serves 2 (1 meat, 1 veg)" for half & half, else "Serves 2" (project/RecipeHalfVeg.dc.html). */
export function servingsText(servings: number, diet?: Diet, meat?: number, veg?: number): string {
    return diet === "half" && meat && veg
        ? `Serves ${servings} (${meat} meat, ${veg} veg)`
        : `Serves ${servings}`;
}

/** The tag on something for one side only; nothing for everyone. */
export function PlateTag({ plate, small = false }: { plate?: PlateFor; small?: boolean }) {
    if (!plate || plate === "all") return null;
    const meat = plate === "meat";
    return (
        <span className={`${small ? styles.tagSmall : styles.tag} ${meat ? styles.meat : styles.veg}`}>
            {meat ? "Meat plates" : "Veggie plates"}
        </span>
    );
}

/** The legend under a half & half recipe's title. */
export function PlateLegend() {
    return (
        <div className={styles.legend} aria-label="Plate tags">
            <span className={`${styles.legendItem} ${styles.meat}`}>
                <span className={`${styles.dot} ${styles.dotMeat}`} aria-hidden="true" />
                Meat plates
            </span>
            <span className={`${styles.legendItem} ${styles.veg}`}>
                <span className={`${styles.dot} ${styles.dotVeg}`} aria-hidden="true" />
                Veggie plates
            </span>
            <span className={`${styles.legendItem} ${styles.everyone}`}>Untagged: everyone</span>
        </div>
    );
}

/** The class for a method step: tinted for one side, plain for everyone. */
export function stepClass(plate?: PlateFor): string | undefined {
    return plate === "meat" ? styles.stepMeat : plate === "veg" ? styles.stepVeg : undefined;
}

/** The step number's class for one side. */
export function stepNumberClass(plate?: PlateFor): string | undefined {
    return plate === "meat" ? styles.numberMeat : undefined;
}

/** "Check before cooking" (#111, #110): what still breaks a rule after the retry. */
export function CheckBeforeCooking({ warnings }: { warnings?: string[] }) {
    if (!warnings || warnings.length === 0) return null;
    return (
        <div role="note" className={styles.checkFirst}>
            <svg
                width="20"
                height="20"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2.2"
                strokeLinecap="round"
                strokeLinejoin="round"
                aria-hidden="true"
                className={styles.checkFirstIcon}
            >
                <path d="M12 4l9 16H3z" />
                <path d="M12 10v4M12 17.2v.1" />
            </svg>
            <div className={styles.checkFirstText}>
                <span className={styles.checkFirstTitle}>Check before cooking</span>
                {warnings.map((w) => (
                    <span key={w}>{w}</span>
                ))}
            </div>
        </div>
    );
}
