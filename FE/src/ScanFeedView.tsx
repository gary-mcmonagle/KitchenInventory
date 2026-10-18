import { namePath, navigate } from "./router";
import { toastSubtitle, visibleToasts, type FeedState, type FeedToast } from "./scanFeed";
import styles from "./Feed.module.css";

/** The live scan feed's toasts on the stock list (#54), as in project/Main.dc.html: newest on top, at most 3, then
 * "+n more waiting." Amber "No match" toasts have Identify and dismiss; "Not in stock" has dismiss only. */
export function ScanFeedView({
    feed,
    now,
    onDismiss,
}: {
    feed: FeedState;
    now: number;
    onDismiss: (toast: FeedToast) => void;
}) {
    const { shown, more } = visibleToasts(feed);
    return (
        <div className={styles.feed} role="status" aria-live="polite" aria-label="Scan feed">
            {shown.map((toast) => (
                <Toast key={toast.scanId} toast={toast} now={now} onDismiss={onDismiss} />
            ))}
            {more > 0 && <div className={styles.more}>+{more} more waiting.</div>}
        </div>
    );
}

function Toast({
    toast,
    now,
    onDismiss,
}: {
    toast: FeedToast;
    now: number;
    onDismiss: (toast: FeedToast) => void;
}) {
    const sub = toastSubtitle(toast, new Date(now));
    const text = (
        <div className={styles.text}>
            <span className={styles.title}>{toast.title}</span>
            <span className={styles.sub}>{sub}</span>
        </div>
    );
    if (toast.kind === "loading") {
        return (
            <div className={`${styles.toast} ${styles.loading}`}>
                <span className={styles.spinner} aria-hidden="true" />
                {text}
            </div>
        );
    }
    if (toast.kind === "ok") {
        return (
            <div className={`${styles.toast} ${styles.ok}`}>
                <svg
                    className={styles.icon}
                    width="22"
                    height="22"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2.4"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    aria-hidden="true"
                >
                    <path d="M5 12.5l4.5 4.5L19 7.5" />
                </svg>
                {text}
            </div>
        );
    }
    return (
        <div className={`${styles.toast} ${styles.warn}`}>
            <svg
                className={styles.icon}
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
                <path d="M12 4l9 16H3z" />
                <path d="M12 10v4M12 17.2v.1" />
            </svg>
            {text}
            {toast.kind === "warn" && toast.productId && (
                <button
                    type="button"
                    className={styles.identify}
                    onClick={() => navigate(namePath(toast.productId!))}
                >
                    Identify
                </button>
            )}
            <button
                type="button"
                className={styles.dismiss}
                aria-label={`Dismiss ${toast.title}`}
                onClick={() => onDismiss(toast)}
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
                    <path d="M6 6l12 12M18 6L6 18" />
                </svg>
            </button>
        </div>
    );
}
