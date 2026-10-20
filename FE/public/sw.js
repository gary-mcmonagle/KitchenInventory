// Cupboard Tracker's service worker (#76). It makes the app installable, takes over on the next open after a deploy,
// shows push notifications (#77), and (#85, spike #83) caches the app shell so the installed app opens with no signal.
//
// Offline caching rules:
// - Only this site's own files. The API (another origin), Microsoft sign-in and SignalR are never touched, so stock is
//   never served stale. The shopping list's offline copy lives in the page's IndexedDB, not here.
// - Pages: network first with a 3-second timeout (a basement's "one bar that never answers"), falling back to the
//   cached app page.
// - The shell (index.html, Vite's hashed JS and CSS, fonts, icons, manifest): from the cache, filled on install.
// - Each deploy has its own versioned cache; the old one is deleted when the new worker takes over.
//
// The build fills in SHELL_VERSION and SHELL_FILES (vite.config.ts). Unbuilt (the dev server), nothing is cached.

const SHELL_VERSION = "__SHELL_VERSION__";
const SHELL_FILES = /* __SHELL_FILES__ */ [];
const CACHE = "cupboard-shell-" + SHELL_VERSION;
const CACHING = !SHELL_VERSION.startsWith("__");
const NAVIGATION_TIMEOUT_MS = 3000;

self.addEventListener("install", (event) => {
    // A new deploy's worker doesn't wait for every tab to close.
    self.skipWaiting();
    if (CACHING) event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(SHELL_FILES)));
});

self.addEventListener("activate", (event) => {
    event.waitUntil(
        (async () => {
            for (const name of await caches.keys()) {
                if (name.startsWith("cupboard-shell-") && name !== CACHE) await caches.delete(name);
            }
            await self.clients.claim();
        })(),
    );
});

self.addEventListener("fetch", (event) => {
    if (!CACHING || event.request.method !== "GET") return;
    const url = new URL(event.request.url);
    if (url.origin !== self.location.origin) return; // the API, sign-in, SignalR, anything else: straight to the network
    if (url.pathname === "/version.json" || url.pathname === "/sw.js") return;
    if (event.request.mode === "navigate") {
        event.respondWith(page(event.request));
    } else if (SHELL_FILES.includes(url.pathname)) {
        event.respondWith(caches.match(url.pathname).then((hit) => hit || fetch(event.request)));
    }
});

/** A page: the network if it answers within the timeout (and refresh the cached copy), else the cached app page. */
async function page(request) {
    const cache = await caches.open(CACHE);
    const network = fetch(request).then((response) => {
        if (response.ok) cache.put("/index.html", response.clone());
        return response;
    });
    const timeout = new Promise((resolve) => setTimeout(() => resolve(null), NAVIGATION_TIMEOUT_MS));
    try {
        const first = await Promise.race([network, timeout]);
        if (first) return first;
    } catch {
        // offline: fall through to the cache
    }
    return (await cache.match("/index.html")) || network;
}

// Push notifications (#77). The server sends Declarative Web Push JSON ({ web_push: 8030, notification: { title,
// body, navigate, tag } }): iOS 18.4+ shows it without this code, and everything else shows it here. EVERY push must
// show a notification: Safari revokes subscriptions that don't (spike #75).
self.addEventListener("push", (event) => {
    let message = {};
    try {
        message = event.data ? event.data.json() : {};
    } catch {
        message = { notification: { body: event.data ? event.data.text() : "" } };
    }
    const n = message.notification || {};
    event.waitUntil(
        self.registration.showNotification(n.title || "Cupboard", {
            body: n.body || "",
            tag: n.tag, // a newer push with the same tag replaces the older one
            icon: "/icons/icon-192.png",
            badge: "/icons/icon-192.png",
            lang: "en-GB",
            data: { navigate: n.navigate || "/" },
        }),
    );
});

// Tapping a notification focuses an open app window and takes it to the tap URL, or opens one.
self.addEventListener("notificationclick", (event) => {
    event.notification.close();
    const target = new URL(
        (event.notification.data && event.notification.data.navigate) || "/",
        self.location.origin,
    );
    const url = target.origin === self.location.origin ? target.href : self.location.origin + "/";
    event.waitUntil(
        (async () => {
            const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
            const open = windows.find((w) => new URL(w.url).origin === self.location.origin);
            if (open) {
                await open.focus();
                if ("navigate" in open) await open.navigate(url);
                return;
            }
            await self.clients.openWindow(url);
        })(),
    );
});
