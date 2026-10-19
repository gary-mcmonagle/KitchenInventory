#!/usr/bin/env bash
# Makes the VAPID key pair for push notifications (#77). Gary runs it once, on the Mac.
#
# Usage: deploy/vapid-keys.sh
#
# Prints ONLY the public key (not secret: it goes in infra/main.bicepparam as vapidPublicKey). The private key is never
# shown or written to disk: it goes straight to the clipboard, for pasting into Key Vault in the portal as the secret
# `vapid-private-key` (Key vaults -> kv-cupboard-<suffix> -> Secrets -> Generate/Import). Then the clipboard is cleared.
#
# Rotating the pair later makes every device turn notifications on again (subscriptions are tied to the public key).
set -euo pipefail
command -v node >/dev/null || { echo "ERROR: needs node" >&2; exit 1; }
command -v pbcopy >/dev/null || { echo "ERROR: needs pbcopy (macOS)" >&2; exit 1; }

node -e '
const c = require("crypto");
const k = c.generateKeyPairSync("ec", { namedCurve: "prime256v1" });
const pub = k.publicKey.export({ format: "jwk" });
const b = (s) => Buffer.from(s, "base64url");
require("child_process").execSync("pbcopy", { input: k.privateKey.export({ format: "jwk" }).d });
console.log("VAPID public key (not secret, for infra/main.bicepparam):");
console.log(Buffer.concat([Buffer.from([4]), b(pub.x), b(pub.y)]).toString("base64url"));
'
echo
echo "The PRIVATE key is on the clipboard. Paste it now into Key Vault as the secret 'vapid-private-key'."
read -r -p "Press Enter once it's saved, to clear the clipboard... " _
pbcopy < /dev/null
echo "Clipboard cleared."
