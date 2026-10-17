import styles from "./App.module.css";

/** Shown until the user signs in. */
export function SignIn({ onSignIn }: { onSignIn: () => void }) {
    return (
        <main className={styles.page}>
            <div className={styles.signIn}>
                <h1 className={styles.title}>Cupboard</h1>
                <p className={styles.lead}>Sign in to see what's in the cupboard.</p>
                <button type="button" className={styles.primary} onClick={onSignIn}>
                    Sign in
                </button>
                <p className={styles.small}>Use the Microsoft account you were invited with.</p>
            </div>
        </main>
    );
}
