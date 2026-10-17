#!/usr/bin/env bash
# Read-only: show the Pi client's journal (journalctl -u cupboard-pi) over SSH.
# Usage: deploy/verify/pi-logs.sh [minutes=30]
#   PI_HOST  SSH target (default gary@cupboard-pi.local)
# The client never logs its auth header value, so nothing secret is shown.
set -euo pipefail
PI_HOST=${PI_HOST:-gary@cupboard-pi.local}
MINUTES=${1:-30}
[[ "$MINUTES" =~ ^[0-9]+$ ]] || { echo "Usage: $0 [minutes]" >&2; exit 2; }
ssh -o BatchMode=yes -o ConnectTimeout=10 "$PI_HOST" \
  "journalctl -u cupboard-pi.service --since '-$MINUTES min' --no-pager -o short-iso"
