import { useEffect, useRef, useState, type FormEvent } from "react";
import {
    ApiError,
    SignInRequiredError,
    type ApiClient,
    type FieldErrors,
    type PhotoRecognition,
    type Product,
} from "./api";
import { toJpeg } from "./photo";
import { groupBarcode, relativeTime } from "./format";
import { AISLES } from "./aisles";
import { Icon, ICON_NAMES, ProductTile } from "./ProductTile";
import { itemPath, navigate } from "./router";
import styles from "./Name.module.css";

export const MAX_NAME = 100;
export const MAX_DETAIL = 60;

type Errors = Partial<Record<"name" | "brand" | "size", string>>;

/** Client-side checks, the same rules as the API (#51). */
export function validate(name: string, brand: string, size: string): Errors {
    const errors: Errors = {};
    if (!name.trim()) errors.name = "Enter a name.";
    else if (name.trim().length > MAX_NAME) errors.name = `Keep the name to ${MAX_NAME} characters or fewer.`;
    if (brand.trim().length > MAX_DETAIL)
        errors.brand = `Keep the brand to ${MAX_DETAIL} characters or fewer.`;
    if (size.trim().length > MAX_DETAIL) errors.size = `Keep the size to ${MAX_DETAIL} characters or fewer.`;
    return errors;
}

/** "Name this product" (#51): the manual parts of project/NameIt.dc.html. In edit mode (#53), the same form corrects
 * a known product's details: "Edit product", prefilled, Save and Cancel, back to item detail. */
export function NameScreen({
    api,
    productId,
    mode = "name",
    prepare = toJpeg,
}: {
    api: ApiClient;
    productId: string;
    mode?: "name" | "edit";
    /** Resizes a photo before recognition; tests pass a stub. */
    prepare?: (file: Blob) => Promise<Blob>;
}) {
    const editing = mode === "edit";
    const [product, setProduct] = useState<Product | null>(null);
    const [loadError, setLoadError] = useState<string | null>(null);
    const [name, setName] = useState("");
    const [brand, setBrand] = useState("");
    const [size, setSize] = useState("");
    const [aisle, setAisle] = useState("");
    const [errors, setErrors] = useState<Errors>({});
    const [formError, setFormError] = useState<string | null>(null);
    const [saving, setSaving] = useState(false);
    const nameInput = useRef<HTMLInputElement>(null);

    useEffect(() => {
        let live = true;
        api.product(productId)
            .then((p) => {
                if (!live) return;
                setProduct(p);
                if (editing && p.status === "known") {
                    setName(p.displayName);
                    setBrand(p.brand ?? "");
                    setSize(p.size ?? "");
                    setAisle(p.aisleId ?? "");
                }
            })
            .catch((error: unknown) => {
                if (!live || error instanceof SignInRequiredError) return;
                setLoadError(
                    error instanceof ApiError && error.status === 404
                        ? "We couldn't find that product."
                        : "Couldn't load this product. Try again.",
                );
            });
        return () => {
            live = false;
        };
    }, [api, productId, editing]);

    useEffect(() => {
        if (product) nameInput.current?.focus();
    }, [product]);

    const back = () => navigate(editing ? itemPath(productId) : "/");

    const save = async (e: FormEvent) => {
        e.preventDefault();
        if (saving) return;
        const found = validate(name, brand, size);
        setErrors(found);
        setFormError(null);
        if (Object.keys(found).length > 0) return;
        setSaving(true);
        try {
            const details = {
                name: name.trim(),
                brand: brand.trim() || undefined,
                size: size.trim() || undefined,
            };
            if (editing) {
                await api.editDetails(productId, details);
                // The aisle (#71) is saved with the form, only when it changed.
                if (aisle && aisle !== product?.aisleId) await api.setAisle(productId, aisle);
            } else await api.nameProduct(productId, details);
            back();
        } catch (error) {
            if (error instanceof SignInRequiredError) return;
            if (error instanceof ApiError && error.status === 400) {
                setErrors((error.body as FieldErrors | undefined)?.errors ?? {});
                setFormError("Check the details.");
            } else if (error instanceof ApiError && error.status === 409) {
                setFormError(
                    editing
                        ? "This product changed while you were editing. Go back and try again."
                        : "This product already has a name.",
                );
            } else {
                setFormError("Couldn't save. Try again.");
            }
            setSaving(false);
        }
    };

    // The form shows for an unknown product when naming, and for a known one when editing.
    const showForm = product && (editing ? product.status === "known" : product.status !== "known");

    return (
        <form className={styles.screen} onSubmit={(e) => void save(e)} noValidate>
            <div className={styles.header}>
                <button type="button" className={styles.back} aria-label="Back to stock" onClick={back}>
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
                <h1 className={styles.title}>{editing ? "Edit product" : "Name this product"}</h1>
            </div>

            <div className={styles.content}>
                {loadError && (
                    <p className={styles.notice} role="alert">
                        {loadError}
                    </p>
                )}
                {!product && !loadError && <p className={styles.context}>Loading…</p>}

                {product && !showForm && (
                    <p className={styles.notice} role="alert">
                        {editing
                            ? "This product has no name yet: name it from the stock list."
                            : `This product already has a name: ${product.displayName}.`}
                    </p>
                )}

                {showForm && (
                    <>
                        <div className={styles.context}>
                            {!editing && product.latestCheckedInAt && (
                                <span>
                                    Scanned {relativeTime(product.latestCheckedInAt)}
                                    {product.latestDeviceRole === "in" && " on the check-in scanner"}
                                    {product.latestDeviceRole === "out" && " on the check-out scanner"}.
                                </span>
                            )}
                            {product.barcode && (
                                <span className={styles.barcode}>
                                    Barcode {groupBarcode(product.barcode)}
                                </span>
                            )}
                        </div>

                        {!editing && (
                            <PhotoGuess
                                api={api}
                                prepare={prepare}
                                onGuess={(guess) => {
                                    setName(guess.name);
                                    setBrand(guess.brand ?? "");
                                    setSize(guess.size ?? "");
                                    setErrors({});
                                }}
                            />
                        )}

                        <div className={styles.fields}>
                            <Field
                                id="name"
                                label="Name"
                                value={name}
                                onChange={setName}
                                error={errors.name}
                                inputRef={nameInput}
                            />
                            <Field
                                id="brand"
                                label="Brand"
                                value={brand}
                                onChange={setBrand}
                                error={errors.brand}
                            />
                            <Field
                                id="size"
                                label="Size"
                                value={size}
                                onChange={setSize}
                                error={errors.size}
                            />
                            {editing && (
                                <div className={styles.field}>
                                    <label htmlFor="aisle" className={styles.label}>
                                        Aisle
                                    </label>
                                    <select
                                        id="aisle"
                                        className={styles.input}
                                        value={aisle}
                                        onChange={(e) => setAisle(e.target.value)}
                                    >
                                        {!aisle && <option value="">Not sorted yet</option>}
                                        {AISLES.map((a) => (
                                            <option key={a.id} value={a.id}>
                                                {a.name}
                                            </option>
                                        ))}
                                    </select>
                                </div>
                            )}
                        </div>
                        {editing && (
                            <IconPicker
                                api={api}
                                product={product}
                                onChange={(icon, source) =>
                                    setProduct({ ...product, icon, iconSource: source })
                                }
                            />
                        )}

                        {formError && (
                            <p className={styles.notice} role="alert">
                                {formError}
                            </p>
                        )}
                    </>
                )}
            </div>

            <div className={styles.bar}>
                {showForm && (
                    <button type="submit" className={styles.save} disabled={saving}>
                        {saving ? "Saving…" : editing ? "Save" : "Save product"}
                    </button>
                )}
                <button type="button" className={styles.notNow} onClick={back}>
                    {editing ? "Cancel" : "Not now"}
                </button>
            </div>
        </form>
    );
}

function Field(props: {
    id: string;
    label: string;
    value: string;
    onChange: (value: string) => void;
    error?: string;
    inputRef?: React.Ref<HTMLInputElement>;
}) {
    const errorId = `${props.id}-error`;
    return (
        <div className={styles.field}>
            <label htmlFor={props.id} className={styles.label}>
                {props.label}
            </label>
            <input
                id={props.id}
                ref={props.inputRef}
                type="text"
                className={props.error ? `${styles.input} ${styles.inputError}` : styles.input}
                value={props.value}
                onChange={(e) => props.onChange(e.target.value)}
                aria-invalid={props.error ? true : undefined}
                aria-describedby={props.error ? errorId : undefined}
                autoComplete="off"
            />
            {props.error && (
                <span id={errorId} className={styles.error}>
                    {props.error}
                </span>
            )}
        </div>
    );
}

/**
 * The product's icon (#72): Change opens a searchable grid of the bundled icons; picking one saves at once and is
 * never overwritten by Jev. "Let Jev choose" clears it, so the next timer run picks again.
 */
function IconPicker({
    api,
    product,
    onChange,
}: {
    api: ApiClient;
    product: Product;
    onChange: (icon: string | null, source: string | null) => void;
}) {
    const [open, setOpen] = useState(false);
    const [query, setQuery] = useState("");
    const [error, setError] = useState<string | null>(null);
    const q = query.trim().toLowerCase().replace(/\s+/g, "-");
    const names = ICON_NAMES.filter((n) => !q || n.includes(q));

    const save = async (icon: string | null) => {
        setError(null);
        try {
            await api.setIcon(product.productId, icon);
            onChange(icon, icon ? "manual" : null);
            setOpen(false);
        } catch (e) {
            if (e instanceof SignInRequiredError) return;
            setError("Couldn't save the icon. Try again.");
        }
    };

    return (
        <div className={styles.iconField}>
            <span className={styles.label}>Icon</span>
            <div className={styles.iconRow}>
                <ProductTile
                    className={styles.iconTile}
                    name={product.displayName}
                    icon={product.icon}
                    aisleId={product.aisleId}
                />
                <span className={styles.iconNote}>
                    {product.iconSource === "manual"
                        ? "Chosen by you"
                        : product.icon
                          ? "Chosen for you"
                          : "Not chosen yet"}
                </span>
                <button
                    type="button"
                    className={styles.iconButton}
                    aria-expanded={open}
                    onClick={() => setOpen((o) => !o)}
                >
                    Change
                </button>
            </div>
            {open && (
                <div className={styles.iconPicker}>
                    <input
                        type="search"
                        className={styles.input}
                        aria-label="Search icons"
                        placeholder="Search icons, e.g. bread"
                        value={query}
                        onChange={(e) => setQuery(e.target.value)}
                        onKeyDown={(e) => e.key === "Enter" && e.preventDefault()} // don't submit the form
                    />
                    <div className={styles.iconGrid} role="group" aria-label="Icons">
                        {names.map((n) => (
                            <button
                                key={n}
                                type="button"
                                className={styles.iconChoice}
                                aria-label={n.replace(/-/g, " ")}
                                aria-pressed={product.icon === n}
                                onClick={() => void save(n)}
                            >
                                <Icon name={n} />
                            </button>
                        ))}
                        {names.length === 0 && <span className={styles.iconNote}>No icon matches that.</span>}
                    </div>
                    {product.iconSource === "manual" && (
                        <button type="button" className={styles.iconButton} onClick={() => void save(null)}>
                            Let Jev choose
                        </button>
                    )}
                </div>
            )}
            {error && (
                <p className={styles.notice} role="alert">
                    {error}
                </p>
            )}
        </div>
    );
}

type Guess = PhotoRecognition["items"][number];

/**
 * Identify from a photo (Gary, 2026-10-18; a first slice of #56): photograph the pack, and the photo recogniser (#58)
 * fills in name, brand and size to check before saving. Several things in the photo: pick one. The photo isn't saved.
 */
function PhotoGuess({
    api,
    prepare,
    onGuess,
}: {
    api: ApiClient;
    prepare: (file: Blob) => Promise<Blob>;
    onGuess: (guess: Guess) => void;
}) {
    const input = useRef<HTMLInputElement>(null);
    const [state, setState] = useState<
        | { step: "idle" }
        | { step: "reading" }
        | { step: "done"; items: Guess[]; chosen: Guess | null }
        | { step: "failed" }
    >({ step: "idle" });

    const choose = (items: Guess[], guess: Guess) => {
        onGuess(guess);
        setState({ step: "done", items, chosen: guess });
    };

    const read = async (file: File | undefined) => {
        if (!file) return;
        setState({ step: "reading" });
        try {
            const result = await api.recognisePhoto(await prepare(file));
            if (!result.available || result.items.length === 0) {
                setState({ step: "failed" });
            } else if (result.items.length === 1) {
                choose(result.items, result.items[0]);
            } else {
                setState({ step: "done", items: result.items, chosen: null });
            }
        } catch (e) {
            if (e instanceof SignInRequiredError) return;
            setState({ step: "failed" });
        }
    };

    const details = (g: Guess) => [g.brand, g.size].filter(Boolean).join(" · ");
    return (
        <div className={styles.photo}>
            <input
                ref={input}
                type="file"
                accept="image/*"
                capture="environment"
                className={styles.fileInput}
                aria-label="Photo of the item"
                tabIndex={-1}
                onChange={(e) => {
                    void read(e.target.files?.[0]);
                    e.target.value = "";
                }}
            />
            {state.step === "reading" ? (
                <p className={styles.photoNote} role="status">
                    Looking at your photo… This takes a couple of seconds. The photo is not saved.
                </p>
            ) : (
                <button type="button" className={styles.photoButton} onClick={() => input.current?.click()}>
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
                        <path d="M4 8.5A1.5 1.5 0 0 1 5.5 7H8l1.5-2h5L16 7h2.5A1.5 1.5 0 0 1 20 8.5v9a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 17.5z" />
                        <circle cx="12" cy="13" r="3.2" />
                    </svg>
                    {state.step === "idle" ? "Identify from a photo" : "Retake photo"}
                </button>
            )}
            {state.step === "failed" && (
                <p className={styles.photoNote} role="alert">
                    We couldn't work that one out. Type it in or retake the photo.
                </p>
            )}
            {state.step === "done" && state.chosen && (
                <p className={styles.photoNote} role="status">
                    Filled in from your photo. Check it, then save.
                </p>
            )}
            {state.step === "done" && state.items.length > 1 && (
                <div className={styles.guesses} role="group" aria-label="Which one is it?">
                    <span className={styles.photoNote}>
                        More than one thing in the photo. Which one is it?
                    </span>
                    {state.items.map((g, i) => (
                        <button
                            key={`${g.name}-${i}`}
                            type="button"
                            className={styles.guess}
                            aria-pressed={state.chosen === g}
                            onClick={() => choose(state.items, g)}
                        >
                            <span className={styles.guessName}>{g.name}</span>
                            {details(g) && <span className={styles.guessDetails}>{details(g)}</span>}
                        </button>
                    ))}
                </div>
            )}
        </div>
    );
}
