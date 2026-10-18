// Throwaway proof of concept for spike #75 (push notifications). Not part of the app; never deployed.
//
// A tiny static server with two endpoints, standard library only:
//   GET  /config     → { vapidPublicKey } from the environment
//   POST /subscribe  → saves the browser's push subscription to subscriptions.json (git-ignored)
//
// Usage: node server.mjs <env file with VAPID_PUBLIC_KEY> [port=8075]
// The env file must live OUTSIDE the repo. Service workers need a secure context: http://localhost is one.
import { createServer } from "node:http";
import { readFile, writeFile } from "node:fs/promises";
import { extname, join, normalize } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("./public/", import.meta.url));
const store = fileURLToPath(new URL("./subscriptions.json", import.meta.url));
const [envFile, port = "8075"] = process.argv.slice(2);
if (!envFile) {
    console.error("Usage: node server.mjs <env file outside the repo> [port]");
    process.exit(1);
}
const env = Object.fromEntries(
    (await readFile(envFile, "utf8"))
        .split("\n")
        .filter((l) => l.includes("="))
        .map((l) => [l.slice(0, l.indexOf("=")), l.slice(l.indexOf("=") + 1).trim()]),
);
const publicKey = env.VAPID_PUBLIC_KEY;
if (!publicKey) throw new Error("VAPID_PUBLIC_KEY is missing from the env file");

const types = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".webmanifest": "application/manifest+json",
    ".png": "image/png",
    ".svg": "image/svg+xml",
};

createServer(async (req, res) => {
    const url = new URL(req.url, "http://localhost");
    if (url.pathname === "/config") {
        res.writeHead(200, { "Content-Type": "application/json" });
        return res.end(JSON.stringify({ vapidPublicKey: publicKey }));
    }
    if (url.pathname === "/subscribe" && req.method === "POST") {
        // It may be reachable through a temporary tunnel for the phone test: accept only small, sane bodies.
        let body = "";
        for await (const chunk of req) {
            body += chunk;
            if (body.length > 4096) {
                res.writeHead(413);
                return res.end();
            }
        }
        let sub;
        try {
            sub = JSON.parse(body);
        } catch {
            sub = {};
        }
        if (typeof sub.endpoint !== "string" || !sub.endpoint.startsWith("https://") || !sub.keys?.p256dh || !sub.keys?.auth) {
            res.writeHead(400);
            return res.end("not a push subscription");
        }
        const all = JSON.parse(await readFile(store, "utf8").catch(() => "[]")).filter(
            (s) => s.endpoint !== sub.endpoint,
        );
        if (all.length >= 20) {
            res.writeHead(429);
            return res.end("enough subscriptions for a proof of concept");
        }
        all.push({ ...sub, userAgent: req.headers["user-agent"], savedAt: new Date().toISOString() });
        await writeFile(store, JSON.stringify(all, null, 2), { mode: 0o600 });
        // Log the push service's host only: the full endpoint is a capability URL.
        console.log(`subscription saved for ${new URL(sub.endpoint).host} (${all.length} in total)`);
        res.writeHead(201);
        return res.end();
    }
    const path = normalize(join(root, url.pathname === "/" ? "index.html" : url.pathname));
    if (!path.startsWith(root)) {
        res.writeHead(403);
        return res.end();
    }
    try {
        const file = await readFile(path);
        res.writeHead(200, {
            "Content-Type": types[extname(path)] ?? "application/octet-stream",
            // The service worker must never be cached, or updates are missed (as on the Static Web App).
            "Cache-Control": path.endsWith("sw.js") ? "no-cache" : "no-store",
        });
        res.end(file);
    } catch {
        res.writeHead(404);
        res.end("not found");
    }
}).listen(Number(port), () => console.log(`push PoC on http://localhost:${port}`));
