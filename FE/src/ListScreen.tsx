import styles from "./List.module.css";

/** The List tab (#87): a placeholder until the shared shopping list lands (#84). Header as on project/List.dc.html. */
export function ListScreen() {
    return (
        <div className={styles.screen}>
            <header className={styles.header}>
                <div className={styles.kicker}>Shared with everyone at home</div>
                <h1 className={styles.title}>Shopping list</h1>
            </header>
            <p className={styles.soon}>The shared shopping list is coming soon.</p>
        </div>
    );
}
