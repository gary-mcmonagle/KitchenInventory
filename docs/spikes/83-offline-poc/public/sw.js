// Spike #83 proof of concept: the service worker. It caches the app shell so the installed app opens with no signal,
// and never touches the API (the page's outbox handles data). Bump VERSION to roll out a new shell.
const VERSION = "shell-v1";
const SHELL = ["/", "/index.html", "/app.js", "/app.css", "/manifest.webmanifest", "/icon-192.png", "/icon-512.png"];

self.addEventListener("install", (event) => {
    // Cache the whole shell before taking over, so a half-cached shell is never used.
    event.waitUntil(caches.open(VERSION).then((cache) => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
    event.waitUntil(
        (async () => {
            for (const name of await caches.keys()) if (name !== VERSION) await caches.delete(name);
            await self.clients.claim();
        })(),
    );
});

self.addEventListener("fetch", (event) => {
    const url = new URL(event.request.url);
    if (event.request.method !== "GET" || url.origin !== self.location.origin) return;
    if (url.pathname.startsWith("/api/") || url.pathname.startsWith("/vendor/")) return; // data: never from the cache

    // Network first with a short timeout (a basement can have one bar that never answers), then the cache.
    // Successful responses refresh the cache, so the next offline open has the latest shell.
    event.respondWith(
        (async () => {
            const cache = await caches.open(VERSION);
            try {
                const response = await Promise.race([
                    fetch(event.request),
                    new Promise((_, reject) => setTimeout(() => reject(new Error("slow")), 3000)),
                ]);
                if (response.ok) cache.put(event.request.mode === "navigate" ? "/index.html" : event.request, response.clone());
                return response;
            } catch {
                const cached = await cache.match(event.request.mode === "navigate" ? "/index.html" : event.request);
                return cached ?? new Response("Offline and not cached", { status: 503 });
            }
        })(),
    );
});
