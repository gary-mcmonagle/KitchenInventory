// Cupboard Tracker's service worker (#76). It makes the app installable and takes over on the next open after a deploy.
// It deliberately has NO fetch handler: nothing is cached, so stock is never served stale and sign-in redirects are
// never touched (spike #75). It shows push notifications (#77). Offline shell caching is spike #83's recommendation
// for a later card.

self.addEventListener("install", () => {
    // A new deploy's worker doesn't wait for every tab to close.
    self.skipWaiting();
});

self.addEventListener("activate", (event) => {
    event.waitUntil(self.clients.claim());
});

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
