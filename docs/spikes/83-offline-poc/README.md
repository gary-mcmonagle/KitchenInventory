# Offline shopping list proof of concept (spike #83)

A throwaway installable page that keeps a shared shopping list working with no signal. **Not part of the app and never
deployed.** Findings and the recommendation are in [`../83-offline-list.md`](../83-offline-list.md).

| File | What it is |
|---|---|
| `server.mjs` | A tiny Node server (standard library only): the list API (`GET /api/list`, `POST /api/list/changes`, idempotent by change ID, with the spike's conflict rules) and the static files. State in `list.json` (git-ignored). Also serves the app's own MSAL build for `msal-offline.html`. |
| `public/sw.js` | Caches the app shell on install (versioned cache), serves pages network-first with a 3-second timeout and the cache as fallback, never touches `/api/`. |
| `public/app.js` | The list: IndexedDB (`items` and an ordered `outbox`), every change applied locally and queued in one transaction, replayed in order on open, `online`, `visibilitychange` and every 30 s while visible. The banner shows Offline, "n changes waiting to sync" and Synced. |
| `public/msal-offline.html` | Runs MSAL's start-up (`initialize`, `handleRedirectPromise`, accounts) with a made-up client ID, to see whether it needs the network. No sign-in. |

## Desktop (what the agent ran)

```bash
node server.mjs 8083
# then playwright-core drives the Mac's Chrome with throwaway profiles: two "phones" share the list, one goes
# offline (context.setOffline), closes and reopens the app, ticks and adds, the other changes things online,
# then the first comes back and syncs.
```

## iPhone (needs Gary)

The phone needs a public HTTPS URL. As in the push spike, a temporary Cloudflare quick tunnel from the Mac, only while
testing:

```bash
node server.mjs 8083                                  # terminal 1
cloudflared tunnel --url http://localhost:8083       # terminal 2: prints https://<random>.trycloudflare.com
```

1. Open the URL in **Safari**, **Share → Add to Home Screen** (with "Open as Web App" on), and open **Offline PoC** from
   the Home Screen. Add two items. The meta line says `Installed app: true`; note what "Persistent storage" says.
2. Close the app (swipe it away), turn on **Airplane Mode**, and open it from the Home Screen: the list should show.
3. Tick one item, add two, remove one. The banner says "Offline · n changes waiting to sync".
4. Close it, turn Airplane Mode off, open it: the banner goes to "Synced", and the agent checks the server got each
   change once.
5. Optional, later: leave it a few days without opening it, then open it offline and see whether the list survived.
6. Stop the tunnel, delete the Home Screen icon, and delete `list.json`.
