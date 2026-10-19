# Push notifications proof of concept (spike #75)

A throwaway page, service worker and sender to prove Web Push end to end. **Not part of the app and never deployed.**
Findings and the recommendation are in [`../75-push-notifications.md`](../75-push-notifications.md).

| File | What it is |
|---|---|
| `server.mjs` | A tiny Node server (standard library only): serves `public/`, `GET /config` (the VAPID **public** key), `POST /subscribe` (saves to `subscriptions.json`, git-ignored). |
| `public/index.html` | "Turn on notifications": registers the service worker, asks permission **from the tap**, subscribes with the VAPID public key, posts the subscription. |
| `public/sw.js` | Shows a notification for **every** push (Declarative Web Push JSON), opens or focuses the app on tap, re-subscribes on `pushsubscriptionchange`. |
| `public/manifest.webmanifest`, `icon-*.png` | Makes the page installable (`display: standalone`), which iPhone needs for push. Icons are the app's favicon. |
| `send/` | A .NET 10 console sender with **no packages**: `aes128gcm` encryption (RFC 8291) and VAPID (RFC 8292) with in-box crypto (`WebPushSender.cs`). `dotnet run -- --self-test` checks it against RFC 8291's test vector. |

## Keys (never in the repo)

Generate a throwaway VAPID pair (Apple rejects a made-up subject such as `mailto:…@example.invalid`: use a real `https:` URL or email) into a file **outside the repo** (mode 600):

```bash
node -e '
const c=require("crypto"),k=c.generateKeyPairSync("ec",{namedCurve:"prime256v1"});
const p=k.publicKey.export({format:"jwk"}),d=k.privateKey.export({format:"jwk"}).d,b=s=>Buffer.from(s,"base64url");
require("fs").writeFileSync(process.argv[1],`VAPID_PUBLIC_KEY=${Buffer.concat([Buffer.from([4]),b(p.x),b(p.y)]).toString("base64url")}\nVAPID_PRIVATE_KEY=${d}\nVAPID_SUBJECT=https://kitchen.garymcmonagle.com\n`,{mode:0o600})' ~/push-poc-vapid.env
```

## Desktop (what the agent ran)

```bash
node server.mjs ~/push-poc-vapid.env 8075          # http://localhost:8075 is a secure context
# open it in Chrome, press "Turn on notifications", allow
cd send && dotnet run -- ~/push-poc-vapid.env "Use first today" "Milk and gnocchi need using." "/?filter=use-first"
```

Claude Code's built-in browser pane can't do this (notifications are denied and service workers fail to register),
so the agent drove the Mac's Google Chrome with **a throwaway profile** through `playwright-core`.

## iPhone (needs Gary)

The phone needs a **public HTTPS** URL for the page. The suggested way is a temporary tunnel from the Mac, only while
testing:

```bash
brew install cloudflared                              # once
node server.mjs ~/push-poc-vapid.env 8075            # terminal 1
cloudflared tunnel --url http://localhost:8075       # terminal 2: prints https://<random>.trycloudflare.com
```

1. On the iPhone (iOS 16.4 or later), open the `trycloudflare.com` URL in **Safari**.
2. **Share → Add to Home Screen → Add.** Open **Push PoC** from the Home Screen (not from Safari).
3. Tap **Turn on notifications**, then **Allow**. The page says `Subscribed with web.push.apple.com`.
4. On the Mac: `cd send && dotnet run -- ~/push-poc-vapid.env`. The phone shows "Use first today".
5. Lock the phone and send again; then tap the notification: the PoC opens at `/?from=push`.
6. Stop the tunnel (Ctrl+C), delete the Home Screen icon and `~/push-poc-vapid.env`.
