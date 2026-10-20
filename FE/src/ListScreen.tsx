import { useEffect, useRef, useState } from "react";
import { SignInRequiredError, type ApiClient, type ListSuggestion, type ShoppingListItem } from "./api";
import { groupByAisle, listWhen, type ListGroup } from "./shoppingList";
import { useShoppingList } from "./useShoppingList";
import styles from "./List.module.css";

/** Sort by aisle, remembered on this device. Storage can be missing or throw: then off. */
export const BY_AISLE_KEY = "list.byAisle";

function storedByAisle(): boolean {
    try {
        return localStorage.getItem(BY_AISLE_KEY) === "true";
    } catch {
        return false;
    }
}

function rememberByAisle(on: boolean) {
    try {
        localStorage.setItem(BY_AISLE_KEY, String(on));
    } catch {
        // Not remembered; the toggle still works.
    }
}

const SUGGEST_DELAY_MS = 200;

/**
 * The shared shopping list (#84, project/List.dc.html): add with suggestions, tick off, remove, Sort by aisle, Recently
 * ticked off and Clear ticked. Live through SignalR (no polling); see useShoppingList.
 */
export function ListScreen({ api, now }: { api: ApiClient; now?: Date }) {
    const list = useShoppingList();
    const [query, setQuery] = useState("");
    const [suggestions, setSuggestions] = useState<ListSuggestion[]>([]);
    const [byAisle, setByAisle] = useState(storedByAisle);
    const [doneOpen, setDoneOpen] = useState(true);
    const asked = useRef(0);

    useEffect(() => {
        const q = query.trim();
        if (!q) {
            setSuggestions([]);
            return;
        }
        const ticket = ++asked.current;
        const timer = setTimeout(() => {
            api.listSuggestions(q)
                .then((answer) => ticket === asked.current && setSuggestions(answer.suggestions))
                .catch((error: unknown) => {
                    if (!(error instanceof SignInRequiredError))
                        console.warn("Couldn't load suggestions", error);
                });
        }, SUGGEST_DELAY_MS);
        return () => clearTimeout(timer);
    }, [api, query]);

    const add = (name: string, productId: string | null = null) => {
        list.add(name, productId, productId ? "suggestion" : "typed");
        asked.current++;
        setQuery("");
        setSuggestions([]);
    };

    const openNames = new Set(list.open.map((i) => i.name.trim().toLowerCase()));
    const shown = suggestions.filter((s) => !openNames.has(s.name.trim().toLowerCase()));
    const groups: ListGroup[] = byAisle ? groupByAisle(list.open) : [{ title: null, items: list.open }];
    const count = list.open.length;
    const at = now ?? new Date();

    return (
        <div className={styles.screen}>
            <header className={styles.header}>
                <div className={styles.kicker}>Shared with everyone at home</div>
                <h1 className={styles.title}>Shopping list</h1>
                <div className={styles.summary}>
                    {count} {count === 1 ? "thing" : "things"} to buy
                </div>
            </header>

            <div className={styles.addArea}>
                <form
                    className={styles.addRow}
                    onSubmit={(e) => {
                        e.preventDefault();
                        add(query);
                    }}
                >
                    <label className={styles.addBox}>
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
                            <path d="M12 5v14M5 12h14" />
                        </svg>
                        <span className={styles.srOnly}>Add to the list</span>
                        <input
                            type="text"
                            role="combobox"
                            aria-expanded={shown.length > 0}
                            aria-controls="list-suggestions"
                            autoComplete="off"
                            maxLength={80}
                            className={styles.addInput}
                            placeholder="Add something…"
                            value={query}
                            onChange={(e) => setQuery(e.target.value)}
                        />
                    </label>
                    <button type="submit" className={styles.addButton}>
                        Add
                    </button>
                </form>
                {shown.length > 0 && (
                    <ul
                        id="list-suggestions"
                        role="listbox"
                        aria-label="Suggestions"
                        className={styles.suggestions}
                    >
                        {shown.map((s) => (
                            <li
                                key={s.name}
                                role="option"
                                aria-selected={false}
                                className={styles.suggestion}
                            >
                                <button
                                    type="button"
                                    className={styles.suggestionButton}
                                    onClick={() => add(s.name, s.productId)}
                                >
                                    <span className={styles.suggestionName}>{s.name}</span>
                                    <span className={s.inStock > 0 ? styles.inStock : styles.noneInStock}>
                                        {s.inStock > 0 ? `${s.inStock} in stock` : "None in stock"}
                                    </span>
                                </button>
                            </li>
                        ))}
                    </ul>
                )}
            </div>

            <div className={styles.content}>
                {list.failed && (
                    <p className={styles.problem} role="alert">
                        Couldn&apos;t load the list.{" "}
                        <button type="button" className={styles.linkButton} onClick={list.reload}>
                            Try again
                        </button>
                    </p>
                )}
                <section className={styles.section} aria-labelledby="to-buy">
                    <h2 id="to-buy" className={styles.sectionTitle}>
                        <span>To buy</span>
                        <span className={styles.sectionCount}>{count}</span>
                    </h2>
                    <div>
                        <button
                            type="button"
                            aria-pressed={byAisle}
                            className={styles.aisleToggle}
                            onClick={() => {
                                rememberByAisle(!byAisle);
                                setByAisle(!byAisle);
                            }}
                        >
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
                                <path d="M4 5h16M4 5v14M20 5v14M8 9h3M13 9h3M8 13h3M13 13h3M8 17h3M13 17h3" />
                            </svg>
                            Sort by aisle
                        </button>
                    </div>
                    {list.loaded && count === 0 && (
                        <div className={styles.empty}>Nothing on the list. Nice.</div>
                    )}
                    {groups
                        .filter((g) => g.items.length > 0)
                        .map((g) => (
                            <div key={g.title ?? "all"} className={styles.group}>
                                {g.title && (
                                    <div className={styles.groupTitle}>
                                        <span>{g.title}</span>
                                        <span className={styles.groupCount}>{g.items.length}</span>
                                    </div>
                                )}
                                <ul className={styles.rows} aria-label={g.title ?? "To buy"}>
                                    {g.items.map((item) => (
                                        <OpenRow
                                            key={item.id}
                                            item={item}
                                            now={at}
                                            onTick={() => list.tick(item, true)}
                                            onRemove={() => list.remove(item)}
                                        />
                                    ))}
                                </ul>
                            </div>
                        ))}
                </section>

                {list.done.length > 0 && (
                    <section className={styles.section}>
                        <button
                            type="button"
                            aria-expanded={doneOpen}
                            className={styles.doneToggle}
                            onClick={() => setDoneOpen(!doneOpen)}
                        >
                            <span className={styles.doneToggleLabel}>
                                <svg
                                    width="16"
                                    height="16"
                                    viewBox="0 0 24 24"
                                    fill="none"
                                    stroke="currentColor"
                                    strokeWidth="2.4"
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    aria-hidden="true"
                                    className={doneOpen ? styles.chevronOpen : undefined}
                                >
                                    <path d="M9 6l6 6-6 6" />
                                </svg>
                                Recently ticked off
                            </span>
                            <span className={styles.sectionCount}>{list.done.length}</span>
                        </button>
                        {doneOpen && (
                            <>
                                <ul className={styles.rows} aria-label="Recently ticked off">
                                    {list.done.map((item) => (
                                        <li key={item.id} className={styles.doneRow}>
                                            <button
                                                type="button"
                                                role="checkbox"
                                                aria-checked="true"
                                                aria-label={`Put ${item.name} back on the list`}
                                                className={styles.tick}
                                                onClick={() => list.tick(item, false)}
                                            >
                                                <span className={styles.ticked} aria-hidden="true">
                                                    <svg
                                                        width="14"
                                                        height="14"
                                                        viewBox="0 0 24 24"
                                                        fill="none"
                                                        stroke="currentColor"
                                                        strokeWidth="3"
                                                        strokeLinecap="round"
                                                        strokeLinejoin="round"
                                                    >
                                                        <path d="M5 12.5l4.5 4.5L19 7.5" />
                                                    </svg>
                                                </span>
                                            </button>
                                            <span className={styles.doneName}>{item.name}</span>
                                            <span className={styles.doneMeta}>
                                                {item.doneByName ?? ""}
                                                {item.doneAt ? ` · ${listWhen(item.doneAt, at)}` : ""}
                                            </span>
                                        </li>
                                    ))}
                                </ul>
                                <button type="button" className={styles.clear} onClick={list.clearTicked}>
                                    Clear ticked
                                </button>
                            </>
                        )}
                    </section>
                )}
            </div>
        </div>
    );
}

function OpenRow({
    item,
    now,
    onTick,
    onRemove,
}: {
    item: ShoppingListItem;
    now: Date;
    onTick: () => void;
    onRemove: () => void;
}) {
    return (
        <li className={styles.row}>
            <button
                type="button"
                role="checkbox"
                aria-checked="false"
                aria-label={`Tick off ${item.name}`}
                className={styles.tick}
                onClick={onTick}
            >
                <span className={styles.tickCircle} aria-hidden="true" />
            </button>
            <div className={styles.rowText}>
                <div className={styles.rowTop}>
                    <span className={styles.rowName}>{item.name}</span>
                    {item.note && <span className={styles.note}>· {item.note}</span>}
                    {item.findingAisle && (
                        <span role="status" aria-label="Finding its aisle" className={styles.spinner} />
                    )}
                </div>
                <div className={styles.meta}>
                    {item.findingAisle
                        ? "Finding its aisle…"
                        : `Added by ${item.addedByName} · ${listWhen(item.addedAt, now)}`}
                </div>
            </div>
            <button
                type="button"
                aria-label={`Remove ${item.name}`}
                className={styles.remove}
                onClick={onRemove}
            >
                <svg
                    width="18"
                    height="18"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2.2"
                    strokeLinecap="round"
                    aria-hidden="true"
                >
                    <path d="M6 6l12 12M18 6L6 18" />
                </svg>
            </button>
        </li>
    );
}
