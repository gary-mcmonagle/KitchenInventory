import { useState } from "react";
import type { ApiClient } from "./api";
import { pushSupport, subscribe, unsubscribe } from "./push";
import { navigate } from "./router";
import styles from "./App.module.css";

/**
 * TEMPORARY (#77 verification only): turn notifications on for this device and send a test, before the real
 * notifications card exists. Remove this screen, its route and the footer link once Gary has tried it.
 */
export function PushTestScreen({ api }: { api: ApiClient }) {
    const [status, setStatus] = useState(`Support: ${pushSupport()}`);
    const run = (label: string, work: () => Promise<unknown>) => async () => {
        setStatus(`${label}…`);
        try {
            setStatus(`${label}: ${JSON.stringify(await work())}`);
        } catch (error) {
            setStatus(`${label} failed: ${error instanceof Error ? error.message : String(error)}`);
        }
    };
    const button = {
        minHeight: 52,
        borderRadius: 14,
        border: 0,
        background: "#2F6B4F",
        color: "#fff",
        fontWeight: 700,
        fontSize: 16,
    };
    return (
        <div className={styles.content} style={{ paddingTop: 28, gap: 12 }}>
            <h1 className={styles.title}>Test notifications</h1>
            <p>Temporary page for checking push notifications on this device.</p>
            <button type="button" style={button} onClick={run("Turn on", () => subscribe(api))}>
                Turn on notifications
            </button>
            <button type="button" style={button} onClick={run("Send a test", () => api.pushTest())}>
                Send a test
            </button>
            <button
                type="button"
                style={{ ...button, background: "#fff", color: "#1F5238", border: "1px solid #2F6B4F" }}
                onClick={run("Turn off", () => unsubscribe(api))}
            >
                Turn off on this device
            </button>
            <p role="status">{status}</p>
            <button
                type="button"
                style={{ ...button, background: "transparent", color: "#3C4C44" }}
                onClick={() => navigate("/")}
            >
                Back
            </button>
        </div>
    );
}
