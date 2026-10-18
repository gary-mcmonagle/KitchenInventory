// Spike #75 proof of concept: the service worker. Every push MUST show a notification (userVisibleOnly):
// Safari revokes the subscription after a few pushes that don't.
//
// Payloads use the Declarative Web Push format ({ web_push: 8030, notification: { title, body, navigate, tag } }),
// so iOS/iPadOS 18.4+ and Safari can show them without this script, and other browsers show them here.

self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", (event) => event.waitUntil(self.clients.claim()));

self.addEventListener("push", (event) => {
    let message = {};
    try {
        message = event.data ? event.data.json() : {};
    } catch {
        message = { notification: { title: "Cupboard", body: event.data?.text() ?? "" } };
    }
    const n = message.notification ?? {};
    event.waitUntil(
        self.registration.showNotification(n.title || "Cupboard", {
            body: n.body ?? "",
            tag: n.tag, // a newer push with the same tag replaces the older one
            icon: "/icon-192.png",
            badge: "/icon-192.png",
            data: { navigate: n.navigate ?? "/" },
        }),
    );
});

self.addEventListener("notificationclick", (event) => {
    event.notification.close();
    const target = new URL(event.notification.data?.navigate ?? "/", self.location.origin).href;
    event.waitUntil(
        (async () => {
            // Reuse an open window (and take it to the target) rather than opening another.
            const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
            for (const client of windows) {
                if ("focus" in client) {
                    await client.focus();
                    if ("navigate" in client) await client.navigate(target);
                    return;
                }
            }
            await self.clients.openWindow(target);
        })(),
    );
});

// The browser refreshed or lost the subscription: subscribe again with the same options and tell the server.
self.addEventListener("pushsubscriptionchange", (event) => {
    event.waitUntil(
        (async () => {
            const options = event.oldSubscription?.options;
            if (!options) return;
            const subscription = await self.registration.pushManager.subscribe(options);
            await fetch("/subscribe", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(subscription),
            });
        })(),
    );
});
