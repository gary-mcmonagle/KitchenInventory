import { useEffect, useState } from "react";
import { ApiError, SignInRequiredError, type ApiClient } from "./api";
import {
    phoneNotifications,
    setPreferences,
    SHOPPING_LIST,
    STILL_GOT_THIS,
    STOCK_CHANGES,
    subscribe,
    unsubscribe,
    type PhoneNotifications,
    type PushEnvironment,
} from "./push";
import styles from "./ThisPhone.module.css";

type Api = Pick<
    ApiClient,
    "pushConfig" | "putPushSubscription" | "deletePushSubscription" | "setPushPreferences" | "pushTest"
>;

const LOOK = {
    on: { line: "On for this phone", pill: "On", tone: styles.on },
    off: { line: "Off for this phone", pill: "Off", tone: styles.off },
    blocked: { line: "Blocked in your settings", pill: "Blocked", tone: styles.blocked },
    needsInstall: { line: "Needs the Home Screen app", pill: null, tone: styles.off },
    unsupported: { line: "This phone can't show notifications.", pill: null, tone: styles.off },
} as const;

/**
 * "This phone" on the Settings screen (#80, #122, project/Settings.dc.html): turn notifications on or off for this device,
 * choose stock change, shopping list (#86) and "Still got this?" (#94) pushes, and send a test. Permission is only ever asked from the Turn on tap (iOS requires it).
 */
export function ThisPhoneCard({ api, env }: { api: Api; env?: PushEnvironment }) {
    const [state, setState] = useState<PhoneNotifications | null>(null);
    const [busy, setBusy] = useState(false);
    const [problem, setProblem] = useState<string | null>(null);
    const [test, setTest] = useState<"idle" | "sent" | "failed" | "tooMany">("idle");

    useEffect(() => {
        let live = true;
        phoneNotifications(api, env)
            .then((s) => live && setState(s))
            .catch((error: unknown) => {
                if (!(error instanceof SignInRequiredError))
                    console.warn("Couldn't read this phone's notifications", error);
                if (live) setState({ kind: "off" });
            });
        return () => {
            live = false;
        };
    }, [api, env]);

    const run = async (action: () => Promise<void>, failure: string) => {
        setBusy(true);
        setProblem(null);
        try {
            await action();
        } catch (error) {
            if (!(error instanceof SignInRequiredError)) console.warn(failure, error);
            setProblem(failure);
        } finally {
            setBusy(false);
        }
    };

    const turnOn = () =>
        run(async () => {
            const result = await subscribe(api as ApiClient, env);
            if (result === "subscribed") {
                const defaults = { [STOCK_CHANGES]: true, [SHOPPING_LIST]: true, [STILL_GOT_THIS]: true }; // on by default
                await setPreferences(api as ApiClient, defaults, env);
                setState({ kind: "on", preferences: defaults });
            } else if (result === "notConfigured") {
                setProblem("Notifications aren't set up yet.");
            } else {
                setState(await phoneNotifications(api, env));
            }
        }, "Couldn't turn on notifications. Try again.");

    const turnOff = () =>
        run(async () => {
            await unsubscribe(api as ApiClient, env);
            setState({ kind: "off" });
            setTest("idle");
        }, "Couldn't turn off notifications. Try again.");

    const flip = (key: string, on: boolean) =>
        state?.kind === "on" &&
        run(async () => {
            setState({ kind: "on", preferences: { ...state.preferences, [key]: on } });
            if (!(await setPreferences(api as ApiClient, { [key]: on }, env))) setState({ kind: "off" });
        }, "Couldn't save that. Try again.");

    const sendTest = async () => {
        setBusy(true);
        try {
            await api.pushTest();
            setTest("sent");
        } catch (error) {
            setTest(error instanceof ApiError && error.status === 429 ? "tooMany" : "failed");
        } finally {
            setBusy(false);
        }
    };

    const kind = state?.kind ?? "off";
    const look = LOOK[kind];
    const stockOn = state?.kind === "on" && state.preferences[STOCK_CHANGES] === true;
    const listOn = state?.kind === "on" && state.preferences[SHOPPING_LIST] !== false;
    const checkOn = state?.kind === "on" && state.preferences[STILL_GOT_THIS] !== false;

    return (
        <section className={styles.section} aria-labelledby="phone-title">
            <h2 id="phone-title" className={styles.title}>
                <span>This phone</span>
                <span className={styles.titleNote}>Notifications</span>
            </h2>
            <div className={styles.card} aria-busy={state === null}>
                <div className={styles.head}>
                    <div className={`${styles.tile} ${look.tone}`} aria-hidden="true">
                        <svg
                            width="24"
                            height="24"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                        >
                            <path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15z" />
                            <path d="M10 20.5a2 2 0 0 0 4 0" />
                        </svg>
                    </div>
                    <div className={styles.headText}>
                        <div className={styles.name}>Notifications</div>
                        <div className={styles.line}>{state ? look.line : "Checking…"}</div>
                    </div>
                    {state && look.pill && <span className={`${styles.pill} ${look.tone}`}>{look.pill}</span>}
                </div>

                {state?.kind === "on" && (
                    <>
                        <div className={styles.toggles}>
                            <div className={styles.toggle}>
                                <div className={styles.toggleText}>
                                    <span id="sw-stock" className={styles.toggleLabel}>
                                        Stock changes
                                    </span>
                                    <span className={styles.toggleHelp}>
                                        What was added or removed, when you didn't see it in the app
                                    </span>
                                </div>
                                <button
                                    type="button"
                                    role="switch"
                                    aria-checked={stockOn}
                                    aria-labelledby="sw-stock"
                                    className={styles.switch}
                                    disabled={busy}
                                    onClick={() => void flip(STOCK_CHANGES, !stockOn)}
                                >
                                    <span
                                        className={`${styles.track} ${stockOn ? styles.trackOn : ""}`}
                                        aria-hidden="true"
                                    >
                                        <span className={styles.knob} />
                                    </span>
                                </button>
                            </div>
                            <div className={styles.toggle}>
                                <div className={styles.toggleText}>
                                    <span id="sw-list" className={styles.toggleLabel}>
                                        Shopping list
                                    </span>
                                    <span className={styles.toggleHelp}>
                                        When someone else adds to the list
                                    </span>
                                </div>
                                <button
                                    type="button"
                                    role="switch"
                                    aria-checked={listOn}
                                    aria-labelledby="sw-list"
                                    className={styles.switch}
                                    disabled={busy}
                                    onClick={() => void flip(SHOPPING_LIST, !listOn)}
                                >
                                    <span
                                        className={`${styles.track} ${listOn ? styles.trackOn : ""}`}
                                        aria-hidden="true"
                                    >
                                        <span className={styles.knob} />
                                    </span>
                                </button>
                            </div>
                            <div className={styles.toggle}>
                                <div className={styles.toggleText}>
                                    <span id="sw-check" className={styles.toggleLabel}>
                                        Still got this?
                                    </span>
                                    <span className={styles.toggleHelp}>
                                        Tue, Thu and Sun at 18:00, if anything has expired
                                    </span>
                                </div>
                                <button
                                    type="button"
                                    role="switch"
                                    aria-checked={checkOn}
                                    aria-labelledby="sw-check"
                                    className={styles.switch}
                                    disabled={busy}
                                    onClick={() => void flip(STILL_GOT_THIS, !checkOn)}
                                >
                                    <span
                                        className={`${styles.track} ${checkOn ? styles.trackOn : ""}`}
                                        aria-hidden="true"
                                    >
                                        <span className={styles.knob} />
                                    </span>
                                </button>
                            </div>
                            <div className={styles.quiet}>
                                Quiet from 21:30 to 07:30: nothing is sent overnight.
                            </div>
                        </div>
                        <div className={styles.buttons}>
                            <button
                                type="button"
                                className={styles.outline}
                                disabled={busy}
                                onClick={() => void sendTest()}
                            >
                                {test === "sent"
                                    ? "Test sent"
                                    : test === "tooMany"
                                      ? "Try again later"
                                      : test === "failed"
                                        ? "Couldn't send"
                                        : "Send a test"}
                            </button>
                            <button
                                type="button"
                                className={styles.plain}
                                disabled={busy}
                                onClick={() => void turnOff()}
                            >
                                Turn off on this phone
                            </button>
                        </div>
                    </>
                )}

                {state?.kind === "off" && (
                    <>
                        <div className={styles.text}>
                            Get a notification when stock is added or removed and you didn't see it in the
                            app, or when something goes on the shopping list.
                        </div>
                        <button
                            type="button"
                            className={styles.primary}
                            disabled={busy}
                            onClick={() => void turnOn()}
                        >
                            Turn on notifications
                        </button>
                    </>
                )}

                {state?.kind === "blocked" && (
                    <div className={styles.warn}>
                        Notifications are blocked for Cupboard. Turn them on in your phone's settings, then
                        come back here.
                    </div>
                )}

                {state?.kind === "needsInstall" && (
                    <>
                        <div className={styles.text}>
                            To get notifications on iPhone, add Cupboard to your Home Screen and open it from
                            there.
                        </div>
                        <ol className={styles.steps}>
                            <li>
                                <span className={styles.step} aria-hidden="true">
                                    1
                                </span>
                                <span className={styles.stepText}>
                                    Tap Share
                                    <svg
                                        width="18"
                                        height="18"
                                        viewBox="0 0 24 24"
                                        fill="none"
                                        stroke="currentColor"
                                        strokeWidth="2"
                                        strokeLinecap="round"
                                        strokeLinejoin="round"
                                        aria-hidden="true"
                                    >
                                        <path d="M12 15V3M8 7l4-4 4 4" />
                                        <path d="M6 11H5v10h14V11h-1" />
                                    </svg>
                                </span>
                            </li>
                            <li>
                                <span className={styles.step} aria-hidden="true">
                                    2
                                </span>
                                <span>Choose Add to Home Screen</span>
                            </li>
                            <li>
                                <span className={styles.step} aria-hidden="true">
                                    3
                                </span>
                                <span>Open Cupboard from your Home Screen</span>
                            </li>
                        </ol>
                    </>
                )}

                {problem && (
                    <p className={styles.problem} role="alert">
                        {problem}
                    </p>
                )}
            </div>
        </section>
    );
}
