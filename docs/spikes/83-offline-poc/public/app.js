// Spike #83 proof of concept: an offline-first shopping list. The page always shows the local copy (IndexedDB), puts
// every change in an outbox first, and replays the outbox in order whenever it can reach the server.
const DB = "shopping-poc";

function db() {
    return new Promise((resolve, reject) => {
        const open = indexedDB.open(DB, 1);
        open.onupgradeneeded = () => {
            open.result.createObjectStore("items", { keyPath: "id" });
            open.result.createObjectStore("outbox", { keyPath: "seq", autoIncrement: true }); // seq keeps the order
        };
        open.onsuccess = () => resolve(open.result);
        open.onerror = () => reject(open.error);
    });
}

async function tx(stores, mode, work) {
    const d = await db();
    return new Promise((resolve, reject) => {
        const t = d.transaction(stores, mode);
        const result = work(...stores.map((s) => t.objectStore(s)));
        t.oncomplete = () => resolve(result);
        t.onerror = () => reject(t.error);
    });
}

const all = (store) =>
    new Promise((resolve) => {
        const r = store.getAll();
        r.onsuccess = () => resolve(r.result);
    });

async function read() {
    let items, outbox;
    await tx(["items", "outbox"], "readonly", (i, o) => {
        all(i).then((x) => (items = x));
        all(o).then((x) => (outbox = x));
    });
    return { items, outbox };
}

/** Applies a change locally and queues it, in one transaction: the UI and the outbox never disagree. */
async function change(op, itemId, name) {
    const c = { changeId: crypto.randomUUID(), op, itemId, name, at: new Date().toISOString() };
    await tx(["items", "outbox"], "readwrite", (items, outbox) => {
        outbox.add(c);
        if (op === "add") items.put({ id: itemId, name, ticked: false });
        else {
            const r = items.get(itemId);
            r.onsuccess = () => {
                if (!r.result) return;
                if (op === "remove") items.delete(itemId);
                else items.put({ ...r.result, ticked: op === "tick" });
            };
        }
    });
    render();
    sync();
}

let syncing = false;
/** Sends the outbox in order; on success, drops the sent changes and takes the server's list as the truth. */
async function sync() {
    if (syncing) return;
    syncing = true;
    try {
        const { outbox } = await read();
        const response = outbox.length
            ? await fetch("/api/list/changes", {
                  method: "POST",
                  headers: { "Content-Type": "application/json" },
                  body: JSON.stringify({ changes: outbox.map(({ seq, ...c }) => c) }),
              })
            : await fetch("/api/list", { cache: "no-store" });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const body = await response.json();
        await tx(["items", "outbox"], "readwrite", (items, ob) => {
            for (const c of outbox) ob.delete(c.seq);
            // Changes made while this request was in flight are still in the outbox: keep their local effect.
            items.clear();
            for (const i of body.items) items.put({ id: i.id, name: i.name, ticked: i.ticked });
        });
        status.lastSync = new Date();
        status.error = null;
    } catch (error) {
        status.error = navigator.onLine ? String(error.message ?? error) : null; // offline is expected, not an error
    } finally {
        syncing = false;
        render();
    }
}

const status = { lastSync: null, error: null, persisted: null, estimate: null };

async function render() {
    const { items, outbox } = await read();
    const list = document.getElementById("list");
    list.replaceChildren(
        ...items
            .sort((a, b) => Number(a.ticked) - Number(b.ticked) || a.name.localeCompare(b.name))
            .map((i) => {
                const li = document.createElement("li");
                li.innerHTML = `<label><input type="checkbox" ${i.ticked ? "checked" : ""}> <span></span></label><button type="button" aria-label="Remove">×</button>`;
                li.querySelector("span").textContent = i.name;
                li.querySelector("input").onchange = (e) => change(e.target.checked ? "tick" : "untick", i.id);
                li.querySelector("button").onclick = () => change("remove", i.id);
                return li;
            }),
    );
    const banner = document.getElementById("banner");
    banner.textContent = !navigator.onLine
        ? `Offline${outbox.length ? ` · ${outbox.length} change${outbox.length === 1 ? "" : "s"} waiting to sync` : ""}`
        : outbox.length
          ? `${outbox.length} change${outbox.length === 1 ? "" : "s"} waiting to sync…`
          : status.error
            ? `Couldn't sync: ${status.error}`
            : status.lastSync
              ? "Synced"
              : "";
    banner.dataset.state = !navigator.onLine ? "offline" : outbox.length ? "waiting" : "synced";
    document.getElementById("storage").textContent =
        `Persistent storage: ${status.persisted} · used ${Math.round((status.estimate?.usage ?? 0) / 1024)} KB`;
    document.getElementById("installed").textContent = `Installed app: ${matchMedia("(display-mode: standalone)").matches || navigator.standalone === true}`;
}

document.getElementById("add").addEventListener("submit", (e) => {
    e.preventDefault();
    const input = e.target.elements.name;
    if (input.value.trim()) change("add", crypto.randomUUID(), input.value.trim());
    input.value = "";
});

// Sync triggers: open, back online, back to the foreground, and every 30 s while visible. (No Background Sync on iOS.)
addEventListener("online", () => sync());
addEventListener("offline", () => render());
document.addEventListener("visibilitychange", () => document.visibilityState === "visible" && sync());
setInterval(() => document.visibilityState === "visible" && navigator.onLine && sync(), 30000);

(async () => {
    if ("serviceWorker" in navigator) navigator.serviceWorker.register("/sw.js");
    status.persisted = navigator.storage?.persist ? await navigator.storage.persist() : "n/a";
    status.estimate = navigator.storage?.estimate ? await navigator.storage.estimate() : null;
    await render();
    await sync();
})();

window.poc = { read, sync, change }; // for the automated test
