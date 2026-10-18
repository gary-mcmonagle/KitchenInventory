import { navigate, SCANNERS_PATH } from "./router";
import styles from "./BottomNav.module.css";

/** The bottom nav (#55), as in the mockups: Stock and Scanners (Add arrives with loose items, #58). */
export function BottomNav({ current }: { current: "stock" | "scanners" }) {
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
