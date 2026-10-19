import { LIST_PATH, navigate, RECIPES_PATH } from "./router";
import styles from "./BottomNav.module.css";

export type NavTab = "stock" | "list" | "recipes";

/**
 * The bottom nav (#87, option A on the canvas): three tabs, Stock · List · Recipes. The open tab has a light-green pill
 * behind its icon and a bold green label. Add and Scanners live in the stock list header. The List tab's count badge
 * shows only when there's something on the list.
 */
export function BottomNav({ current, listCount = 0 }: { current: NavTab; listCount?: number }) {
    const go = (path: string) => (e: React.MouseEvent) => {
        e.preventDefault();
        navigate(path);
    };
    const tab = (id: NavTab, path: string, label: string, icon: React.ReactNode, badge?: number) => (
        <a
            href={path}
            className={styles.item}
            aria-current={current === id ? "page" : undefined}
            aria-label={badge ? `${label}, ${badge} item${badge === 1 ? "" : "s"}` : undefined}
            onClick={go(path)}
        >
            <span className={styles.pill}>
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
                    {icon}
                </svg>
                {badge ? (
                    <span className={styles.badge} aria-hidden="true">
                        {badge}
                    </span>
                ) : null}
            </span>
            {label}
        </a>
    );
    return (
        <nav className={styles.nav} aria-label="Main">
            {tab(
                "stock",
                "/",
                "Stock",
                <>
                    <rect x="4" y="3" width="16" height="18" rx="2" />
                    <path d="M4 12h16M10 7.5h4M10 16.5h4" />
                </>,
            )}
            {tab(
                "list",
                LIST_PATH,
                "List",
                <>
                    <path d="M9 6h11M9 12h11M9 18h11" />
                    <path d="M4 6l1 1 2-2M4 12l1 1 2-2M4 18h2" />
                </>,
                listCount,
            )}
            {tab(
                "recipes",
                RECIPES_PATH,
                "Recipes",
                <>
                    <path d="M4 11h16a8 8 0 0 1-16 0z" />
                    <path d="M9 4c0 1.5 1 1.5 1 3M13 4c0 1.5 1 1.5 1 3" />
                </>,
            )}
        </nav>
    );
}
