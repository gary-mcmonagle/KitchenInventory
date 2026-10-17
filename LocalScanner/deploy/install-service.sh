#!/usr/bin/env bash
# Installs or updates the cupboard-pi systemd service and its sudoers rule. Runs ON the Pi, as root.
# Safe to re-run. deploy/pi.sh copies it into each release and says when it's needed (the first deploy, or
# when the unit or the rule changes). Gary runs it and types his own sudo password:
#   ssh -t gary@cupboard-pi.local sudo bash /opt/cupboard-tracker/releases/<sha>/install-service.sh
#
# The deploy itself never needs root beyond the two commands in cupboard-pi.sudoers. Installing the unit
# stays with Gary, because a unit written by the deploy user could run anything as root.
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "Run as root: sudo bash $0" >&2; exit 1; }
HERE=$(cd "$(dirname "$0")" && pwd)

echo "== systemd unit"
install -m 644 -o root -g root "$HERE/cupboard-pi.service" /etc/systemd/system/cupboard-pi.service
systemctl daemon-reload
systemctl enable cupboard-pi.service

echo "== sudoers rule"
tmp=$(mktemp)
install -m 440 "$HERE/cupboard-pi.sudoers" "$tmp"
visudo -cqf "$tmp" || { rm -f "$tmp"; echo "The sudoers rule didn't validate; nothing installed." >&2; exit 1; }
install -m 440 -o root -g root "$tmp" /etc/sudoers.d/cupboard-pi
rm -f "$tmp"

echo "== service"
if [[ ! -e /opt/cupboard-tracker/current ]]; then
  echo "  No release is live yet: enabled, not started. Run deploy/pi.sh again from the MacBook."
elif [[ ! -f /etc/cupboard-tracker/pi.env ]]; then
  echo "  /etc/cupboard-tracker/pi.env is missing: enabled, not started (see LocalScanner/.env.example)."
else
  systemctl restart cupboard-pi.service
  sleep 3
  systemctl --no-pager --lines=0 status cupboard-pi.service | sed 's/^/  /'
fi
