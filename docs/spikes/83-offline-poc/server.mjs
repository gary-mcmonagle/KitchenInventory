// Throwaway proof of concept for spike #83 (offline shopping list). Not part of the app; never deployed.
//
// A tiny server, standard library only:
//   GET  /api/list                → { items, version }               the shared list
//   POST /api/list/changes        → { applied, skipped, items, version }
//        body { changes: [{ changeId, op, itemId, name?, at }] }   op: add | tick | untick | remove
//        Idempotent by changeId: a replayed change is skipped, never applied twice.
//   GET  /vendor/msal-browser.min.js  the app's own MSAL build (from FE/node_modules), for the offline sign-in check
//
// Conflict rules (the spike's proposal):
//   - add: an open item with the same name (ignoring case and spaces) absorbs it, so two offline adds merge;
//   - tick beats an older untick: tick/untick apply only if newer than the item's last tick/untick ("at" times);
//   - remove wins: a removed item stays removed, and later ticks or unticks for it are skipped.
//
// Usage: node server.mjs [port=8083]. State lives in list.json (git-ignored); delete it to start again.
import { createServer } from "node:http";
import { readFile, writeFile } from "node:fs/promises";
import { extname, join, normalize } from "node:path";
import { fileURLToPath } from "node:url";

const here = (p) => fileURLToPath(new URL(p, import.meta.url));
const root = here("./public/");
const store = here("./list.json");
const msal = here("../../../FE/node_modules/@azure/msal-browser/lib/msal-browser.min.js");
const port = Number(process.argv[2] ?? 8083);

let state = JSON.parse(await readFile(store, "utf8").catch(() => '{"items":[],"applied":[],"version":0}'));
const save = () => writeFile(store, JSON.stringify(state, null, 2));
const key = (name) => name.trim().toLowerCase().replace(/\s+/g, " ");

export function apply(s, c) {
    if (s.applied.includes(c.changeId)) return "duplicate";
    const item = s.items.find((i) => i.id === c.itemId);
    let outcome = "applied";
    if (c.op === "add") {
        const open = s.items.find((i) => !i.removed && !i.ticked && key(i.name) === key(c.name ?? ""));
        if (item) outcome = "exists";
        else if (open) {
            open.mergedIds = [...(open.mergedIds ?? []), c.itemId];
            outcome = "merged";
        } else s.items.push({ id: c.itemId, name: c.name.trim(), ticked: false, tickAt: null, removed: false, addedAt: c.at });
    } else {
        const target = item ?? s.items.find((i) => i.mergedIds?.includes(c.itemId));
        if (!target) outcome = "unknown item";
        else if (target.removed) outcome = "already removed";
        else if (c.op === "remove") target.removed = true;
        else if (c.op === "tick" || c.op === "untick") {
            if (target.tickAt && target.tickAt > c.at) outcome = "older than the last tick/untick";
            else {
                target.ticked = c.op === "tick";
                target.tickAt = c.at;
            }
        } else outcome = "bad op";
    }
    s.applied.push(c.changeId);
    if (outcome === "applied" || outcome === "merged") s.version++;
    return outcome;
}

const json = (res, status, body) => {
    res.writeHead(status, { "Content-Type": "application/json", "Cache-Control": "no-store" });
    res.end(JSON.stringify(body));
};
const visible = () => state.items.filter((i) => !i.removed);
const types = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".webmanifest": "application/manifest+json", ".png": "image/png", ".css": "text/css" };

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    createServer(async (req, res) => {
        const url = new URL(req.url, "http://localhost");
        if (url.pathname === "/api/list" && req.method === "GET") return json(res, 200, { items: visible(), version: state.version });
        if (url.pathname === "/api/list/changes" && req.method === "POST") {
            let body = "";
            for await (const chunk of req) {
                body += chunk;
                if (body.length > 64 * 1024) return json(res, 413, { error: "too big" });
            }
            let changes;
            try {
                ({ changes } = JSON.parse(body));
            } catch {
                return json(res, 400, { error: "not JSON" });
            }
            if (!Array.isArray(changes) || changes.length > 200) return json(res, 400, { error: "changes must be a list of at most 200" });
            const results = changes.map((c) => ({ changeId: c.changeId, outcome: apply(state, c) }));
            await save();
            console.log(`changes: ${results.map((r) => r.outcome).join(", ")}`);
            return json(res, 200, { results, items: visible(), version: state.version });
        }
        if (url.pathname === "/vendor/msal-browser.min.js") {
            res.writeHead(200, { "Content-Type": "text/javascript" });
            return res.end(await readFile(msal));
        }
        const path = normalize(join(root, url.pathname === "/" ? "index.html" : url.pathname));
        if (!path.startsWith(root)) return json(res, 403, {});
        try {
            const file = await readFile(path);
            res.writeHead(200, {
                "Content-Type": types[extname(path)] ?? "application/octet-stream",
                // As the Static Web App would: the worker and the page always revalidate.
                "Cache-Control": path.endsWith("sw.js") || path.endsWith(".html") ? "no-cache" : "public, max-age=60",
            });
            res.end(file);
        } catch {
            json(res, 404, { error: "not found" });
        }
    }).listen(port, () => console.log(`offline PoC on http://localhost:${port}`));
}
