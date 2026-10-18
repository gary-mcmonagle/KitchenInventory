import { ADD_PATH, navigate, RECIPES_PATH, SCANNERS_PATH } from "./router";
import styles from "./BottomNav.module.css";

/** The bottom nav (#55), as in the mockups: Stock, Recipes (#73), the Add pill (#58) and Scanners. */
export function BottomNav({ current }: { current: "stock" | "recipes" | "scanners" }) {
    const go = (path: string) => (e: React.MouseEvent) => {
        e.preventDefault();
        navigate(path);
    };
    return (
        <nav className={styles.nav} aria-label="Main">
            <a
                href="/"
                className={styles.item}
                aria-current={current === "stock" ? "page" : undefined}
                onClick={go("/")}
            >
                <svg
                    width="24"
                    height="24"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    aria-hidden="true"
                >
                    <rect x="4" y="3" width="16" height="18" rx="2" />
                    <path d="M4 12h16M10 7.5h4M10 16.5h4" />
                </svg>
                Stock
            </a>
            <a
                href={RECIPES_PATH}
                className={styles.item}
                aria-current={current === "recipes" ? "page" : undefined}
                onClick={go(RECIPES_PATH)}
            >
                <svg
                    width="24"
                    height="24"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    aria-hidden="true"
                >
                    <path d="M4 11h16a8 8 0 0 1-16 0z" />
                    <path d="M9 4c0 1.5 1 1.5 1 3M13 4c0 1.5 1 1.5 1 3" />
                </svg>
                Recipes
            </a>
            <a href={ADD_PATH} className={styles.item} aria-label="Add an item" onClick={go(ADD_PATH)}>
                <span className={styles.pill}>
                    <svg
                        width="22"
                        height="22"
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        strokeWidth="2"
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        aria-hidden="true"
                    >
                        <path d="M4 8.5A1.5 1.5 0 0 1 5.5 7H8l1.5-2h5L16 7h2.5A1.5 1.5 0 0 1 20 8.5v9a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 17.5z" />
                        <circle cx="12" cy="13" r="3.2" />
                    </svg>
                </span>
                Add
            </a>
            <a
                href={SCANNERS_PATH}
                className={styles.item}
                aria-current={current === "scanners" ? "page" : undefined}
                onClick={go(SCANNERS_PATH)}
            >
                <svg
                    width="24"
                    height="24"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    aria-hidden="true"
                >
                    <path d="M4 7v10M7 7v10M10 7v10M14 7v10M17 7v10M20 7v10" />
                </svg>
                Scanners
            </a>
        </nav>
    );
}
