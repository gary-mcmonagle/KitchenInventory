import { useEffect, useRef, useState } from "react";
import { SignInRequiredError, type ApiClient, type ListSuggestion, type ShoppingListItem } from "./api";
import { groupByAisle, listWhen, type ListGroup } from "./shoppingList";
import { useShoppingList, type ShoppingList } from "./useShoppingList";
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

/** A suggestion as shown: offline ones (#85) have no stock count. */
type Suggestion = Omit<ListSuggestion, "inStock"> & { inStock: number | null };

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

    const offline = list.offline;
    useEffect(() => {
        const q = query.trim();
        if (!q || offline) {
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
    }, [api, query, offline]);

    const add = (name: string, productId: string | null = null) => {
        list.add(name, productId, productId ? "suggestion" : "typed");
        asked.current++;
        setQuery("");
        setSuggestions([]);
    };

    const openNames = new Set(list.open.map((i) => i.name.trim().toLowerCase()));
    const known: Suggestion[] = offline ? offlineSuggestions(list.knownNames, query) : suggestions;
    const shown = known.filter((s) => !openNames.has(s.name.trim().toLowerCase()));
    const groups: ListGroup[] = byAisle ? groupByAisle(list.open) : [{ title: null, items: list.open }];
    const count = list.open.length;
    const at = now ?? new Date();

    return (
        <div className={styles.screen}>
            <SyncBanner list={list} api={api} />
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
                                    {s.inStock !== null && (
                                        <span className={s.inStock > 0 ? styles.inStock : styles.noneInStock}>
                                            {s.inStock > 0 ? `${s.inStock} in stock` : "None in stock"}
                                        </span>
                                    )}
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
                                            pending={
                                                (list.offline || list.needsSignIn) &&
                                                list.pending.has(item.id)
                                            }
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

/** Suggestions with no connection (#85): names seen on the list before, from this phone's copy. No stock counts. */
export function offlineSuggestions(names: readonly string[], query: string): Suggestion[] {
    const q = query.trim().toLowerCase();
    if (!q) return [];
    return names
        .filter((n) => n.toLowerCase().includes(q))
        .slice(0, 8)
        .map((name) => ({ name, productId: null, inStock: null }));
}

const changes = (n: number) => (n === 1 ? "1 change waiting to sync" : `${n} changes waiting to sync`);

/** Offline, Syncing…, Synced, or Sign in to sync (#85; the Shopping list artboard's `offline` banner). */
function SyncBanner({ list, api }: { list: ShoppingList; api: ApiClient }) {
    if (list.needsSignIn)
        return (
            <div role="status" className={styles.banner}>
                <span className={styles.bannerText}>Sign in to sync your changes</span>
                <button type="button" className={styles.bannerButton} onClick={() => void api.signInAgain()}>
                    Sign in
                </button>
            </div>
        );
    if (list.offline)
        return (
            <div role="status" className={styles.banner}>
                <OfflineIcon />
                <span className={styles.bannerText}>
                    Offline{list.waiting > 0 ? ` · ${changes(list.waiting)}` : ""}
                </span>
            </div>
        );
    if (list.sync === "syncing" && list.waiting > 0)
        return (
            <div role="status" className={styles.bannerCalm}>
                <span className={styles.bannerText}>
                    Syncing {changes(list.waiting).replace(" waiting to sync", "")}…
                </span>
            </div>
        );
    if (list.sync === "synced")
        return (
            <div role="status" className={styles.bannerCalm}>
                <span className={styles.bannerText}>Synced</span>
            </div>
        );
    return null;
}

function OfflineIcon() {
    return (
        <svg
            width="18"
            height="18"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2.2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
        >
            <path d="M2 8.5a15 15 0 0 1 20 0M5.5 12a10 10 0 0 1 13 0M9 15.5a5 5 0 0 1 6 0" />
            <path d="M12 19h.01" />
            <path d="M3 3l18 18" />
        </svg>
    );
}

function OpenRow({
    item,
    now,
    pending,
    onTick,
    onRemove,
}: {
    item: ShoppingListItem;
    now: Date;
    pending: boolean;
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
                    {item.source === "autoRunOut" && <span className={styles.auto}>Auto</span>}
                    {pending && <span className={styles.notSynced}>Not synced</span>}
                </div>
                <div className={styles.meta}>
                    {item.findingAisle
                        ? "Finding its aisle…"
                        : item.source === "autoRunOut"
                          ? `Added when the last one was used · ${listWhen(item.addedAt, now)}`
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
