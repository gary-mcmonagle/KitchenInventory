import { useEffect, useMemo, useRef, useState } from "react";
import {
    ApiError,
    SignInRequiredError,
    type AddItem,
    type AddSource,
    type ApiClient,
    type LooseProduct,
    type PhotoRecognition,
} from "./api";
import { setFlash } from "./flash";
import { MAX_NAME } from "./NameScreen";
import { toJpeg } from "./photo";
import { navigate } from "./router";
import styles from "./Add.module.css";

export const MAX_QUANTITY = 20;
export const MAX_ROWS = 20;

/** One row of the checklist: something seen in a photo, picked, or typed. */
export interface Row {
    key: string;
    checked: boolean;
    name: string;
    brand: string | null;
    size: string | null;
    quantity: number;
    source: AddSource;
    /** The existing product it adds to; cleared when the name is edited (the API then matches by name). */
    productId: string | null;
    /** "Matches <name> in your stock list". */
    matchName: string | null;
}

type PhotoState =
    | { step: "idle" }
    | { step: "reading" }
    | { step: "done"; found: number }
    | { step: "failed"; message: string };

const COULDNT_READ = "We couldn't read that photo. Pick it from the list or type it below.";

let nextKey = 0;
const key = () => `row-${++nextKey}`;

/** Rows for what the photo shows: matched items add to their product, ticked, with the model's count. */
export function rowsFromPhoto(recognition: PhotoRecognition): Row[] {
    return recognition.items.map((item) => ({
        key: key(),
        checked: true,
        name: item.match?.displayName ?? item.name,
        brand: item.brand,
        size: item.size,
        quantity: Math.min(Math.max(item.quantity, 1), MAX_QUANTITY),
        source: "photo",
        productId: item.match?.productId ?? null,
        matchName: item.match?.displayName ?? null,
    }));
}

/** What the API is sent for a row. */
export function toAddItem(row: Row): AddItem {
    return row.productId
        ? { productId: row.productId, quantity: row.quantity, source: row.source }
        : {
              name: row.name.trim(),
              brand: row.brand ?? undefined,
              size: row.size ?? undefined,
              quantity: row.quantity,
              source: row.source,
          };
}

/** "Added 6 × Bananas", or "Added 3 items" for several. */
export function addedMessage(items: Array<{ displayName: string; quantity: number }>): string {
    if (items.length === 1) {
        const [item] = items;
        return item.quantity === 1
            ? `Added ${item.displayName}`
            : `Added ${item.quantity} × ${item.displayName}`;
    }
    const units = items.reduce((n, i) => n + i.quantity, 0);
    return `Added ${units} items`;
}

const newRequestId = () => crypto.randomUUID(); // a secure context: https, or localhost

/**
 * "Add to stock" (#58): photograph what you're putting away (the main way in), pick a loose item, or type one; tick
 * and fix the list; add. In assign mode ("What is this?") it says what a store-labelled check-in was: one row,
 * quantity 1, added at the original scan time.
 */
export function AddScreen({
    api,
    assignScanId,
    prepare = toJpeg,
}: {
    api: ApiClient;
    /** The store-label check-in being assigned (#58). */
    assignScanId?: string;
    /** Resizes the photo before upload; tests pass a stub. */
    prepare?: (file: Blob) => Promise<Blob>;
}) {
    const assigning = assignScanId !== undefined;
    const [rows, setRows] = useState<Row[]>([]);
    const [photo, setPhoto] = useState<PhotoState>({ step: "idle" });
    const [loose, setLoose] = useState<LooseProduct[] | null>(null);
    const [search, setSearch] = useState("");
    const [typed, setTyped] = useState("");
    const [error, setError] = useState<string | null>(null);
    const [saving, setSaving] = useState(false);
    const [requestId] = useState(newRequestId); // one per screen, so a retry never adds twice
    const camera = useRef<HTMLInputElement>(null);
    const photoRun = useRef(0);

    useEffect(() => {
        let live = true;
        api.looseProducts()
            .then((result) => live && setLoose(result.products))
            .catch((e: unknown) => {
                if (!live || e instanceof SignInRequiredError) return;
                console.warn("Couldn't load loose products", e);
                setLoose([]);
            });
        return () => {
            live = false;
        };
    }, [api]);

    /** In assign mode the newest choice replaces the row; otherwise it's added (or bumps a matching row). */
    const addRows = (incoming: Row[]) =>
        setRows((current) => {
            if (assigning) return incoming.slice(0, 1).map((r) => ({ ...r, quantity: 1, checked: true }));
            const next = [...current];
            for (const row of incoming) {
                const same = next.findIndex(
                    (r) =>
                        (row.productId && r.productId === row.productId) ||
                        (!row.productId && !r.productId && sameName(r.name, row.name)),
                );
                if (same >= 0) {
                    const r = next[same];
                    next[same] = {
                        ...r,
                        checked: true,
                        quantity: Math.min(r.quantity + row.quantity, MAX_QUANTITY),
                    };
                } else if (next.length < MAX_ROWS) next.push(row);
            }
            return next;
        });

    const takePhoto = async (file: File | undefined) => {
        if (!file) return;
        const run = ++photoRun.current;
        setError(null);
        setPhoto({ step: "reading" });
        try {
            const jpeg = await prepare(file);
            const result = await api.recognisePhoto(jpeg);
            if (run !== photoRun.current) return;
            if (result.available === false) {
                setPhoto({ step: "failed", message: COULDNT_READ });
                return;
            }
            const found = rowsFromPhoto(result);
            // Retake replaces the last photo's rows; picked and typed rows stay.
            setRows((current) => current.filter((r) => r.source !== "photo"));
            addRows(found);
            setPhoto(
                !result.readable && found.length === 0
                    ? {
                          step: "failed",
                          message: "That photo is too blurry or dark to read. Try again closer.",
                      }
                    : { step: "done", found: found.length },
            );
        } catch (e) {
            if (e instanceof SignInRequiredError || run !== photoRun.current) return;
            console.warn("Photo recognition failed", e);
            setPhoto({
                step: "failed",
                message:
                    e instanceof ApiError && e.status === 400
                        ? "That photo couldn't be used. Try another one."
                        : COULDNT_READ,
            });
        }
    };

    const pick = (product: LooseProduct) =>
        addRows([
            {
                key: key(),
                checked: true,
                name: product.displayName,
                brand: null,
                size: null,
                quantity: 1,
                source: "pick",
                productId: product.productId,
                matchName: null,
            },
        ]);

    const addTyped = () => {
        const name = typed.trim();
        if (!name) return;
        addRows([
            {
                key: key(),
                checked: true,
                name: name.slice(0, MAX_NAME),
                brand: null,
                size: null,
                quantity: 1,
                source: "typed",
                productId: null,
                matchName: null,
            },
        ]);
        setTyped("");
    };

    const update = (rowKey: string, change: Partial<Row>) =>
        setRows((current) => current.map((r) => (r.key === rowKey ? { ...r, ...change } : r)));

    const ticked = rows.filter((r) => r.checked);
    const invalid = ticked.some((r) => !r.name.trim());

    const save = async () => {
        if (saving || ticked.length === 0) return;
        if (invalid) {
            setError("Give every ticked item a name.");
            return;
        }
        setSaving(true);
        setError(null);
        try {
            if (assignScanId !== undefined) {
                const item = toAddItem(ticked[0]);
                const result = await api.assignStoreLabel(assignScanId, {
                    productId: item.productId,
                    name: item.name,
                    brand: item.brand,
                    size: item.size,
                    source: item.source,
                });
                setFlash(`Added ${result.displayName}`);
            } else {
                const result = await api.addToStock(requestId, ticked.map(toAddItem));
                setFlash(addedMessage(result.items));
            }
            navigate("/");
        } catch (e) {
            if (e instanceof SignInRequiredError) return;
            setSaving(false);
            if (e instanceof ApiError && e.status === 409) {
                setError("That store label has already been sorted out.");
            } else if (e instanceof ApiError && e.status === 404) {
                setError("We couldn't find that store-label scan.");
            } else if (e instanceof ApiError && e.status === 400) {
                const errors = (e.body as { errors?: Record<string, string> } | undefined)?.errors;
                setError(errors ? Object.values(errors)[0] : "Check the items.");
            } else {
                setError("Couldn't add that. Try again.");
            }
        }
    };

    const q = search.trim().toLowerCase();
    const choices = useMemo(
        () => (loose ?? []).filter((p) => !q || p.displayName.toLowerCase().includes(q)),
        [loose, q],
    );

    const reading = photo.step === "reading";
    return (
        <div className={styles.screen}>
            <div className={styles.header}>
                <button
                    type="button"
                    className={styles.back}
                    aria-label="Back to stock"
                    onClick={() => navigate("/")}
                >
                    <svg
                        width="24"
                        height="24"
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        strokeWidth="2.2"
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        aria-hidden="true"
                    >
                        <path d="M15 5l-7 7 7 7" />
                    </svg>
                </button>
                <h1 className={styles.title}>{assigning ? "What is this?" : "Add to stock"}</h1>
            </div>

            <div className={styles.content}>
                {assigning && (
                    <p className={styles.context}>
                        You scanned a label printed in the shop. Say what it is and it goes in stock from when
                        you scanned it.
                    </p>
                )}

                <input
                    ref={camera}
                    type="file"
                    accept="image/*"
                    capture="environment"
                    className={styles.fileInput}
                    aria-label="Photo"
                    tabIndex={-1}
                    onChange={(e) => {
                        void takePhoto(e.target.files?.[0]);
                        e.target.value = ""; // the same photo can be chosen again
                    }}
                />

                {reading ? (
                    <div className={styles.reading} role="status">
                        <span className={styles.bigSpinner} aria-hidden="true" />
                        <h2 className={styles.readingTitle}>Looking at your photo</h2>
                        <p className={styles.readingText}>
                            This takes a couple of seconds. The photo is not saved.
                        </p>
                    </div>
                ) : (
                    <button
                        type="button"
                        className={styles.photoButton}
                        onClick={() => camera.current?.click()}
                    >
                        <CameraIcon />
                        {photo.step === "idle" ? "Take a photo" : "Retake"}
                    </button>
                )}

                {photo.step === "failed" && (
                    <p className={styles.notice} role="alert">
                        {photo.message}
                    </p>
                )}
                {photo.step === "done" && photo.found === 0 && (
                    <p className={styles.notice} role="alert">
                        We couldn't see anything we recognise. Try again closer, or type it below.
                    </p>
                )}

                {rows.length > 0 && (
                    <ul className={styles.rows} aria-label="To add">
                        {rows.map((row) => (
                            <RowView key={row.key} row={row} assigning={assigning} onChange={update} />
                        ))}
                    </ul>
                )}

                <section className={styles.section} aria-labelledby="pick-title">
                    <h2 id="pick-title" className={styles.sectionTitle}>
                        Pick a loose item
                    </h2>
                    <label className={styles.search}>
                        <svg
                            width="20"
                            height="20"
                            viewBox="0 0 24 24"
                            fill="none"
                            stroke="currentColor"
                            strokeWidth="2"
                            strokeLinecap="round"
                            aria-hidden="true"
                        >
                            <circle cx="11" cy="11" r="7" />
                            <path d="M20 20l-3.5-3.5" />
                        </svg>
                        <span className={styles.visuallyHidden}>Search loose items</span>
                        <input
                            type="search"
                            className={styles.searchInput}
                            placeholder="Search loose items"
                            value={search}
                            onChange={(e) => setSearch(e.target.value)}
                        />
                    </label>
                    {loose === null ? (
                        <p className={styles.muted}>Loading…</p>
                    ) : loose.length === 0 ? (
                        <p className={styles.muted}>
                            Loose items you add will appear here to pick next time.
                        </p>
                    ) : choices.length === 0 ? (
                        <p className={styles.muted}>Nothing matches that.</p>
                    ) : (
                        <div className={styles.chips}>
                            {choices.map((p) => (
                                <button
                                    key={p.productId}
                                    type="button"
                                    className={styles.chip}
                                    onClick={() => pick(p)}
                                >
                                    {p.displayName}
                                </button>
                            ))}
                        </div>
                    )}
                </section>

                <section className={styles.section} aria-labelledby="type-title">
                    <h2 id="type-title" className={styles.sectionTitle}>
                        Add something else
                    </h2>
                    <form
                        className={styles.typeRow}
                        onSubmit={(e) => {
                            e.preventDefault();
                            addTyped();
                        }}
                    >
                        <input
                            type="text"
                            className={styles.input}
                            aria-label="Name of something else"
                            placeholder="e.g. Leeks"
                            maxLength={MAX_NAME}
                            value={typed}
                            onChange={(e) => setTyped(e.target.value)}
                            autoComplete="off"
                        />
                        <button type="submit" className={styles.secondary} disabled={!typed.trim()}>
                            Add
                        </button>
                    </form>
                </section>

                {error && (
                    <p className={styles.notice} role="alert">
                        {error}
                    </p>
                )}
            </div>

            <div className={styles.bar}>
                <button
                    type="button"
                    className={styles.save}
                    disabled={saving || ticked.length === 0 || reading}
                    onClick={() => void save()}
                >
                    {saving ? "Adding…" : assigning ? "This is it" : "Add to stock"}
                </button>
                <button type="button" className={styles.cancel} onClick={() => navigate("/")}>
                    Cancel
                </button>
            </div>
        </div>
    );
}

function RowView({
    row,
    assigning,
    onChange,
}: {
    row: Row;
    assigning: boolean;
    onChange: (key: string, change: Partial<Row>) => void;
}) {
    const details = [row.brand, row.size].filter(Boolean).join(" · ");
    const nameId = `${row.key}-name`;
    return (
        <li className={row.checked ? styles.row : `${styles.row} ${styles.rowOff}`}>
            <div className={styles.rowMain}>
                <label className={styles.checkArea}>
                    <input
                        type="checkbox"
                        className={styles.check}
                        checked={row.checked}
                        aria-label={`Add ${row.name || "this item"}`}
                        onChange={(e) => onChange(row.key, { checked: e.target.checked })}
                    />
                </label>
                <div className={styles.rowText}>
                    <label htmlFor={nameId} className={styles.visuallyHidden}>
                        Name
                    </label>
                    <input
                        id={nameId}
                        type="text"
                        className={styles.rowName}
                        value={row.name}
                        maxLength={MAX_NAME}
                        onChange={(e) =>
                            onChange(row.key, { name: e.target.value, productId: null, matchName: null })
                        }
                        autoComplete="off"
                    />
                    {details && <span className={styles.muted}>{details}</span>}
                    {row.matchName && (
                        <span className={styles.match}>
                            Matches <em>{row.matchName}</em> in your stock list
                        </span>
                    )}
                </div>
            </div>
            {!assigning && (
                <div className={styles.stepper} role="group" aria-label={`How many ${row.name}`}>
                    <button
                        type="button"
                        className={styles.step}
                        aria-label="One fewer"
                        disabled={row.quantity <= 1}
                        onClick={() => onChange(row.key, { quantity: row.quantity - 1 })}
                    >
                        −
                    </button>
                    <span className={styles.count} aria-live="polite">
                        {row.quantity}
                    </span>
                    <button
                        type="button"
                        className={styles.step}
                        aria-label="One more"
                        disabled={row.quantity >= MAX_QUANTITY}
                        onClick={() => onChange(row.key, { quantity: row.quantity + 1 })}
                    >
                        +
                    </button>
                </div>
            )}
        </li>
    );
}

function sameName(a: string, b: string): boolean {
    const key = (s: string) => s.trim().replace(/\s+/g, " ").toLowerCase();
    return key(a) === key(b);
}

function CameraIcon() {
    return (
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
    );
}
