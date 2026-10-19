// Cupboard Tracker's service worker (#76). It makes the app installable and takes over on the next open after a deploy.
// It deliberately has NO fetch handler: nothing is cached, so stock is never served stale and sign-in redirects are
// never touched (spike #75). Push notifications add their `push` and `notificationclick` handlers here
// ([Task] Web Push plumbing); offline shell caching is spike #83's recommendation for a later card.

self.addEventListener("install", () => {
    // A new deploy's worker doesn't wait for every tab to close.
    self.skipWaiting();
});

self.addEventListener("activate", (event) => {
    event.waitUntil(self.clients.claim());
});

// Push handlers go here ([Task] Web Push plumbing).
