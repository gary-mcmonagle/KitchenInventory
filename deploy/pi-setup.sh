#!/usr/bin/env bash
# One-off (and safe to re-run) setup of the Cupboard Tracker Raspberry Pi. Runs ON the Pi, as root.
#
# Gary runs it from the MacBook (it prompts for his sudo password on the Pi; nobody else types it):
#   scp deploy/pi-setup.sh gary@cupboard-pi.local:/tmp/ && ssh -t gary@cupboard-pi.local sudo bash /tmp/pi-setup.sh
#
# It: updates the OS; installs git, python3, python3-venv, python3-pip, evtest and unattended-upgrades;
# turns on unattended security upgrades; creates /opt/cupboard-tracker (owned by the deploying user, who
# copies releases into it: deploy/pi.sh, #33), /var/lib/cupboard-tracker (owned by the service user, for the
# scan queue, outside releases so deploys can't wipe it) and /etc/cupboard-tracker (root-owned, 750, for the
# env file that is never in the repo); sets the timezone and NTP; and disables password SSH logins (key-based only).
# git is installed but no longer used (deployment is by file copy, #33); harmless, so it stays.
set -euo pipefail

[[ $EUID -eq 0 ]] || { echo "Run as root: sudo bash $0" >&2; exit 1; }
DEPLOY_USER=${SUDO_USER:-gary}
export DEBIAN_FRONTEND=noninteractive

step() { echo; echo "== $*"; }

step "Updating the OS"
apt-get update -q
apt-get -y -q full-upgrade

step "Installing packages"
apt-get -y -q install git python3 python3-venv python3-pip evtest unattended-upgrades

step "Turning on unattended security upgrades"
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'CONF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
CONF
systemctl enable --now unattended-upgrades

step "Creating directories"
install -d -m 755 -o "$DEPLOY_USER" -g "$DEPLOY_USER" /opt/cupboard-tracker
# The service runs as the deploying user (in the input group, so it can read the scanners: spike #27).
install -d -m 750 -o "$DEPLOY_USER" -g "$DEPLOY_USER" /var/lib/cupboard-tracker
install -d -m 750 -o root -g root /etc/cupboard-tracker

step "Timezone and clock"
timedatectl set-timezone Europe/London
timedatectl set-ntp true

step "SSH: key-based logins only"
# Only safe because key login already works for $DEPLOY_USER (the agent checked before this script was run).
[[ -s "/home/$DEPLOY_USER/.ssh/authorized_keys" ]] || { echo "No authorized_keys for $DEPLOY_USER: not disabling passwords." >&2; exit 1; }
cat > /etc/ssh/sshd_config.d/10-cupboard-key-only.conf <<'CONF'
# Cupboard Tracker: key-based SSH only (deploy/pi-setup.sh).
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
CONF
sshd -t
systemctl reload ssh

step "Summary"
dpkg -s git python3-venv python3-pip evtest unattended-upgrades 2>/dev/null | grep -E "^(Package|Version):" | paste - - | sed 's/^/  /'
ls -ld /opt/cupboard-tracker /var/lib/cupboard-tracker /etc/cupboard-tracker | sed 's/^/  /'
sshd -T 2>/dev/null | grep -E "^(passwordauthentication|kbdinteractiveauthentication|permitrootlogin) " | sed 's/^/  /'
timedatectl | grep -E "Time zone|synchronized|NTP" | sed 's/^/  /'
if [[ -f /var/run/reboot-required ]]; then
  echo; echo "A reboot is needed to finish the upgrade: sudo reboot"
fi
