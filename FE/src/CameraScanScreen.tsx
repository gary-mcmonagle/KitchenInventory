import { useCallback, useEffect, useRef, useState } from "react";
import { SignInRequiredError, type ApiClient, type CameraScanResult } from "./api";
import { CameraUnavailableError, startReader, type ReaderHandle, type StartReader } from "./barcodeReader";
import { goBack } from "./router";
import { newId } from "./shoppingList";
import styles from "./CameraScan.module.css";

/** The same barcode is ignored for this long after it was last seen, so one pack held in view counts once (#133). */
export const SAME_BARCODE_MS = 2000;
const FLASH_MS = 1800;

type Mode = "in" | "out";
type Tone = "in" | "out" | "warn";

interface Entry {
    id: string;
    kind: "in" | "out" | "new";
    name: string;
    meta: string;
    /** Takes this scan back; null when there is nothing to undo (a store label). */
    undo: (() => Promise<unknown>) | null;
    undone: boolean;
    failed: boolean;
}

const CAMERA_TEXT =
    "We can't use the camera. Allow camera access in your browser settings, or use the scanners.";
const OFFLINE_TEXT = "You're offline. Scans need a connection.";

const leftText = (n: number) => (n > 0 ? `Soonest-dated unit · ${n} left` : "Soonest-dated unit · none left");

/**
 * Scan in and out with the phone camera (#133): continuous reads until Done, Check in or Check out at the top, a running
 * list with Undo on each row. Each read is one `POST /api/camera-scans`, so it's the same check-in or check-out a USB
 * scanner makes. It pauses while offline (no queue). `start` is the barcode reader (tests pass a fake).
 */
export function CameraScanScreen({ api, start = startReader }: { api: ApiClient; start?: StartReader }) {
    const videoRef = useRef<HTMLVideoElement>(null);
    const handle = useRef<ReaderHandle | null>(null);
    const [mode, setMode] = useState<Mode>("in");
    const [entries, setEntries] = useState<Entry[]>([]);
    const [flash, setFlash] = useState<{ tone: Tone; text: string } | null>(null);
    const [cameraFailed, setCameraFailed] = useState(false);
    const [torchSupported, setTorchSupported] = useState(false);
    const [torchOn, setTorchOn] = useState(false);
    const [offline, setOffline] = useState(() => !navigator.onLine);
    const flashTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

    // What the reader's callback needs, kept current without restarting the camera.
    const live = useRef({ mode, offline });
    live.current = { mode, offline };
    const lastSeen = useRef(new Map<string, number>());
    const inFlight = useRef(new Set<string>());
    const retryIds = useRef(new Map<string, string>());

    useEffect(() => () => clearTimeout(flashTimer.current), []);

    useEffect(() => {
        const sync = () => setOffline(!navigator.onLine);
        window.addEventListener("online", sync);
        window.addEventListener("offline", sync);
        return () => {
            window.removeEventListener("online", sync);
            window.removeEventListener("offline", sync);
        };
    }, []);

    const showFlash = useCallback((tone: Tone, text: string) => {
        clearTimeout(flashTimer.current);
        setFlash({ tone, text });
        flashTimer.current = setTimeout(() => setFlash(null), FLASH_MS);
    }, []);

    const addEntry = (entry: Omit<Entry, "id" | "undone" | "failed">) =>
        setEntries((current) => [...current, { ...entry, id: newId(), undone: false, failed: false }]);

    const handleRead = useCallback(
        async (code: string) => {
            const { mode: role, offline: isOffline } = live.current;
            if (isOffline) return; // paused
            const now = Date.now();
            const key = `${role}:${code}`;
            const seen = lastSeen.current.get(code);
            lastSeen.current.set(code, now); // every sighting renews it: holding a pack up is one count
            if (inFlight.current.has(key) || (seen !== undefined && now - seen < SAME_BARCODE_MS)) return;

            navigator.vibrate?.(60);
            inFlight.current.add(key);
            const requestId = retryIds.current.get(key) ?? newId(); // a retry after a failure is the same scan
            try {
                const result: CameraScanResult = await api.cameraScan({ requestId, barcode: code, role });
                retryIds.current.delete(key);
                if (result.outcome === "checkedIn") {
                    showFlash("in", `Checked in: ${result.displayName}`);
                    addEntry({
                        kind: "in",
                        name: result.displayName,
                        meta: `Now ${result.count} in stock`,
                        undo: () => api.undoCheckIn(result.unitId),
                    });
                } else if (result.outcome === "needsName") {
                    showFlash("in", "New barcode saved to name later");
                    const unitId = result.unitId;
                    addEntry({
                        kind: "new",
                        name: "Unknown barcode",
                        meta: "Saved to name later on Scanners",
                        undo: unitId ? () => api.undoCheckIn(unitId) : null,
                    });
                } else if (result.outcome === "checkedOut") {
                    showFlash("out", `Checked out: ${result.displayName}`);
                    addEntry({
                        kind: "out",
                        name: result.displayName,
                        meta: leftText(result.remainingCount),
                        undo: () => api.undoCheckOut(result.unitId),
                    });
                } else {
                    showFlash("warn", "Not in stock. Nothing changed.");
                }
            } catch (error) {
                if (error instanceof SignInRequiredError) return;
                retryIds.current.set(key, requestId);
                lastSeen.current.delete(code); // so showing it again tries again
                showFlash("warn", "Couldn't save that scan. Try again.");
            } finally {
                inFlight.current.delete(key);
            }
        },
        [api, showFlash],
    );

    useEffect(() => {
        let stopped = false;
        const video = videoRef.current;
        if (!video) return;
        start(video, (code) => void handleRead(code)).then(
            (reader) => {
                if (stopped) {
                    reader.stop();
                    return;
                }
                handle.current = reader;
                setTorchSupported(reader.torchSupported);
            },
            (error: unknown) => {
                if (!stopped) setCameraFailed(true);
                if (!(error instanceof CameraUnavailableError))
                    console.warn("The camera scanner failed", error);
            },
        );
        return () => {
            stopped = true;
            handle.current?.stop();
            handle.current = null;
        };
    }, [start, handleRead]);

    const toggleTorch = async () => {
        const next = !torchOn;
        try {
            await handle.current?.setTorch(next);
            setTorchOn(next);
        } catch {
            setTorchSupported(false); // it didn't take: hide the button
        }
    };

    const undo = async (entry: Entry) => {
        if (!entry.undo || entry.undone) return;
        try {
            await entry.undo();
            setEntries((current) =>
                current.map((e) => (e.id === entry.id ? { ...e, undone: true, failed: false } : e)),
            );
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            setEntries((current) => current.map((e) => (e.id === entry.id ? { ...e, failed: true } : e)));
        }
    };

    const live_ = entries.filter((e) => !e.undone);
    const ins = live_.filter((e) => e.kind !== "out").length;
    const outs = live_.filter((e) => e.kind === "out").length;
    const frameClass = flash
        ? flash.tone === "warn"
            ? styles.hitWarn
            : flash.tone === "out"
              ? styles.hitOut
              : styles.hitIn
        : "";

    return (
        <div className={styles.screen}>
            <div className={styles.top}>
                <button type="button" className={styles.close} onClick={() => goBack("/")}>
                    Done
                </button>
                <div className={styles.mode} role="group" aria-label="Scan mode">
                    <button
                        type="button"
                        className={styles.modeIn}
                        aria-pressed={mode === "in"}
                        onClick={() => setMode("in")}
                    >
                        Check in
                    </button>
                    <button
                        type="button"
                        className={styles.modeOut}
                        aria-pressed={mode === "out"}
                        onClick={() => setMode("out")}
                    >
                        Check out
                    </button>
                </div>
                {torchSupported && (
                    <button
                        type="button"
                        className={styles.torch}
                        aria-pressed={torchOn}
                        aria-label="Torch"
                        onClick={() => void toggleTorch()}
                    >
                        <svg
                            width="20"
                            height="20"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2"
                            strokeLinecap="round"
                            strokeLinejoin="round"
                            aria-hidden="true"
                        >
                            <path d="M9 2h6l-1 7h3l-7 13 1-9H7z" />
                        </svg>
                    </button>
                )}
            </div>

            <div className={styles.view}>
                <video ref={videoRef} className={styles.video} playsInline muted aria-label="Camera view" />
                {cameraFailed ? (
                    <div className={styles.unavailable} role="alert">
                        {CAMERA_TEXT}
                    </div>
                ) : (
                    <>
                        <div className={`${styles.frame} ${frameClass}`} aria-hidden="true">
                            <i className={`${styles.corner} ${styles.c1}`} />
                            <i className={`${styles.corner} ${styles.c2}`} />
                            <i className={`${styles.corner} ${styles.c3}`} />
                            <i className={`${styles.corner} ${styles.c4}`} />
                            <div className={styles.laser} />
                        </div>
                        {offline ? (
                            <div className={`${styles.flash} ${styles.flashWarn}`} role="status">
                                {OFFLINE_TEXT}
                            </div>
                        ) : (
                            flash && (
                                <div
                                    className={`${styles.flash} ${
                                        flash.tone === "warn"
                                            ? styles.flashWarn
                                            : flash.tone === "out"
                                              ? styles.flashOut
                                              : styles.flashIn
                                    }`}
                                    role="status"
                                >
                                    {flash.text}
                                </div>
                            )
                        )}
                        <div className={styles.label}>
                            {mode === "in"
                                ? "Point at a barcode. It scans on its own."
                                : "Checking out. Point at a barcode."}
                        </div>
                    </>
                )}
            </div>

            <div className={styles.tally}>
                <div className={styles.tHead}>
                    <b>{entries.length ? `This session: ${ins} in, ${outs} out` : "This session"}</b>
                    <span>Keeps scanning until you tap Done</span>
                </div>
                {entries.length === 0 ? (
                    <div className={styles.empty}>Nothing scanned yet.</div>
                ) : (
                    <ul className={styles.list}>
                        {[...entries].reverse().map((e) => (
                            <li key={e.id} className={`${styles.row} ${e.undone ? styles.undone : ""}`}>
                                <span
                                    className={`${styles.pill} ${
                                        e.kind === "new"
                                            ? styles.pillNew
                                            : e.kind === "in"
                                              ? styles.pillIn
                                              : styles.pillOut
                                    }`}
                                >
                                    {e.kind === "new" ? "Name later" : e.kind === "in" ? "In" : "Out"}
                                </span>
                                <span className={styles.text}>
                                    <span className={styles.name}>{e.name}</span>
                                    <span className={styles.meta}>
                                        {e.failed ? "Couldn't undo that. Try again." : e.meta}
                                    </span>
                                </span>
                                {e.undone ? (
                                    <span className={styles.undoneNote}>Undone</span>
                                ) : (
                                    e.undo && (
                                        <button
                                            type="button"
                                            className={styles.undo}
                                            aria-label={`Undo ${e.name}`}
                                            onClick={() => void undo(e)}
                                        >
                                            Undo
                                        </button>
                                    )
                                )}
                            </li>
                        ))}
                    </ul>
                )}
            </div>
        </div>
    );
}
