#!/usr/bin/env bash
# Build, test and deploy the Pi client (LocalScanner/) to the Raspberry Pi by file copy over SSH, then smoke-check it.
#
# Usage: deploy/pi.sh [--force] [--rollback]
#   --force      allow a dirty tree, a branch other than main, or a commit not pushed to origin/main
#   --rollback   point `current` back at the previous release and restart (no build)
#   PI_HOST      SSH target (default gary@cupboard-pi.local)
#
# On the Pi: /opt/cupboard-tracker/releases/<sha>/ holds the wheel, its own venv, the systemd unit and DEPLOYED_SHA;
# `current` points at the live release and `previous` at the one before. The last 3 releases are kept.
# Never reads, writes or prints /etc/cupboard-tracker/pi.env, and never needs a password: the only root commands
# are the two in LocalScanner/deploy/cupboard-pi.sudoers. Installing the unit is Gary's (install-service.sh).
set -euo pipefail

cd "$(dirname "$0")/.."
PI_HOST=${PI_HOST:-gary@cupboard-pi.local}
APP=/opt/cupboard-tracker
KEEP=3

FORCE=false
ROLLBACK=false
for arg in "$@"; do
  case "$arg" in
    --force) FORCE=true ;;
    --rollback) ROLLBACK=true ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

fail() { echo "ERROR: $*" >&2; exit 1; }
pi() { ssh -o BatchMode=yes -o ConnectTimeout=10 "$PI_HOST" "$@"; }

# Runs on the Pi: point `current` at a release, restart, and check this invocation logged the expected SHA and is
# still running ~10 s later. Exits non-zero (with the invocation's journal) if not.
REMOTE_LIB=$(cat <<'LIB'
set -euo pipefail
APP=/opt/cupboard-tracker
link() { ln -sfn "$2" "$APP/.$1.tmp" && mv -Tf "$APP/.$1.tmp" "$APP/$1"; }   # atomic symlink swap
restart_and_check() {
  local sha=$1 id state
  sudo -n systemctl restart cupboard-pi.service
  id=$(systemctl show -p InvocationID --value cupboard-pi.service)
  sleep 10
  state=$(systemctl show -p ActiveState -p SubState -p InvocationID --value cupboard-pi.service | paste -sd' ' -)
  if [[ "$state" == "active running $id" ]] \
     && journalctl -q --no-pager _SYSTEMD_INVOCATION_ID="$id" | grep -q "cupboard-pi starting: sha=$sha "; then
    journalctl -q --no-pager -o short-iso _SYSTEMD_INVOCATION_ID="$id" | grep "cupboard-pi starting" | tail -1
    return 0
  fi
  echo "Smoke check failed (state: $state). Journal for this start:"
  journalctl -q --no-pager -o short-iso -u cupboard-pi.service --since "-1 min" | tail -30
  return 1
}
LIB
)
remote() { pi "bash -s" <<<"$REMOTE_LIB
$1"; }

pi true 2>/dev/null || fail "Can't SSH to $PI_HOST without a password. Check the Pi is on and your key works."

# --rollback: swap current and previous, restart, check.
if $ROLLBACK; then
  echo "== Rolling back on $PI_HOST"
  remote '
    cur=$(readlink "$APP/current" || true); prev=$(readlink "$APP/previous" || true)
    [[ -n "$prev" && -d "$prev" ]] || { echo "No previous release to roll back to."; exit 1; }
    link current "$prev"; link previous "$cur"
    sha=$(cat "$APP/current/DEPLOYED_SHA")
    echo "current -> $prev (was $cur)"
    restart_and_check "$sha"' || fail "Rollback smoke check failed."
  echo "Rolled back."
  exit 0
fi

# 1. Only deploy committed, pushed code from main (unless --force).
if [[ -n "$(git status --porcelain)" ]]; then
  $FORCE || fail "Uncommitted changes. Commit them, or use --force."
  echo "WARNING: deploying with uncommitted changes (--force)."
fi
git fetch -q origin main
BRANCH=$(git rev-parse --abbrev-ref HEAD)
if [[ "$BRANCH" != "main" && "$(git rev-parse HEAD)" != "$(git rev-parse origin/main)" ]]; then
  $FORCE || fail "On '$BRANCH', not main. Merge to main first, or use --force."
  echo "WARNING: deploying from '$BRANCH' (--force)."
fi
if ! git merge-base --is-ancestor HEAD origin/main; then
  $FORCE || fail "HEAD isn't pushed to origin/main. Push first, or use --force."
  echo "WARNING: HEAD isn't on origin/main (--force)."
fi
SHA=$(git rev-parse --short HEAD)
RELEASE="$APP/releases/$SHA"
echo "Deploying $SHA to $PI_HOST"
echo

# 2. Tests must pass.
PY=LocalScanner/.venv/bin/python
[[ -x "$PY" ]] || fail "No LocalScanner/.venv. Create it: cd LocalScanner && python3.11 -m venv .venv && .venv/bin/pip install -e '.[dev]'"
echo "== pytest"
(cd LocalScanner && .venv/bin/python -m pytest) || fail "Tests failed. Nothing was deployed."
echo

# 3. Build the wheel from a clean copy (so the build leaves nothing in the repo), and stage the release.
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
echo "== Building the wheel"
rsync -a --exclude .venv --exclude '.*_cache' --exclude '*.egg-info' --exclude __pycache__ --exclude build --exclude dist \
  LocalScanner/ "$WORK/src/"
"$PY" -m build --wheel --outdir "$WORK/release" "$WORK/src" >"$WORK/build.log" 2>&1 \
  || { tail -20 "$WORK/build.log"; fail "Wheel build failed."; }
cp LocalScanner/deploy/cupboard-pi.service LocalScanner/deploy/cupboard-pi.sudoers LocalScanner/deploy/install-service.sh \
  "$WORK/release/"
echo "$SHA" >"$WORK/release/DEPLOYED_SHA"
ls "$WORK/release" | sed 's/^/  /'
echo

# 4. Copy and install the release (its own venv). A release that's already live is only restarted.
echo "== Copying to $PI_HOST:$RELEASE"
LIVE=$(pi "readlink $APP/current || true")
if [[ "$LIVE" == "$RELEASE" ]]; then
  echo "  $SHA is already the live release: not replacing it, just restarting."
else
  pi "rm -rf '$RELEASE' && mkdir -p '$RELEASE'"
  rsync -a -e "ssh -o BatchMode=yes" "$WORK/release/" "$PI_HOST:$RELEASE/"
  pi "python3 -m venv '$RELEASE/venv' && '$RELEASE/venv/bin/pip' install -q --no-index --no-deps --disable-pip-version-check \
      '$RELEASE'/*.whl && touch '$RELEASE'" || fail "Installing the wheel on the Pi failed. The live release is unchanged."
  echo "  installed into $RELEASE/venv"
fi
echo

# 5. Preflight: the unit and sudoers rule are installed and current, and pi.env exists. If not, stop before switching.
echo "== Preflight"
INSTALL_CMD="ssh -t $PI_HOST sudo bash $RELEASE/install-service.sh"
if ! pi "cmp -s '$RELEASE/cupboard-pi.service' /etc/systemd/system/cupboard-pi.service"; then
  echo "The systemd unit isn't installed, or it changed in this release. Gary: run this once (it asks for your"
  echo "sudo password), then run deploy/pi.sh again:"
  echo "  $INSTALL_CMD"
  echo "The live release is unchanged."
  exit 3
fi
if ! pi "sudo -n -l /usr/bin/test -f /etc/cupboard-tracker/pi.env >/dev/null 2>&1 && sudo -n -l /usr/bin/systemctl restart cupboard-pi.service >/dev/null 2>&1"; then
  echo "The deploy's sudoers rule is missing or out of date. Gary: run this once, then run deploy/pi.sh again:"
  echo "  $INSTALL_CMD"
  exit 3
fi
if ! pi "sudo -n /usr/bin/test -f /etc/cupboard-tracker/pi.env"; then
  echo "/etc/cupboard-tracker/pi.env is missing. Gary: create it on the Pi (root-owned, mode 600) with the variables"
  echo "in LocalScanner/.env.example:"
  grep -E '^[A-Z_]+=' LocalScanner/.env.example | cut -d= -f1 | sed 's/^/  /'
  echo "e.g.  ssh -t $PI_HOST 'sudo install -m 600 /dev/null /etc/cupboard-tracker/pi.env && sudo nano /etc/cupboard-tracker/pi.env'"
  echo "Then run deploy/pi.sh again. The live release is unchanged."
  exit 3
fi
echo "  unit, sudoers rule and pi.env are in place"
echo

# 6. Switch `current`, restart, smoke check. On failure, put the previous release back.
echo "== Switching to $SHA and restarting"
if ! remote "
  old=\$(readlink \"\$APP/current\" || true); old_prev=\$(readlink \"\$APP/previous\" || true)
  link current '$RELEASE'
  [[ -n \"\$old\" && \"\$old\" != '$RELEASE' ]] && link previous \"\$old\"
  if restart_and_check '$SHA'; then exit 0; fi
  if [[ -n \"\$old\" && \"\$old\" != '$RELEASE' ]]; then
    echo \"Rolling back to \$old\"
    link current \"\$old\"
    if [[ -n \"\$old_prev\" ]]; then link previous \"\$old_prev\"; else rm -f \"\$APP/previous\"; fi
    restart_and_check \"\$(cat \"\$old/DEPLOYED_SHA\")\" || echo 'The previous release failed its check too.'
  fi
  exit 1"; then
  fail "Smoke check failed for $SHA; rolled back to the previous release (if there was one)."
fi
echo

# 7. Keep the newest $KEEP releases (never current or previous). Deletes only under $APP/releases/.
echo "== Pruning old releases"
pi "cd '$APP/releases' && keep=\"\$(readlink -f ../current) \$(readlink -f ../previous 2>/dev/null)\"
  ls -1t | tail -n +$((KEEP + 1)) | while read -r r; do
    [[ -n \"\$r\" && \" \$keep \" != *\" $APP/releases/\$r \"* ]] && rm -rf -- \"$APP/releases/\$r\" && echo \"  removed \$r\"
  done; true"
echo

echo "== Summary"
pi "echo \"  current:  \$(readlink $APP/current)\"; echo \"  previous: \$(readlink $APP/previous 2>/dev/null || echo none)\"
    echo \"  releases: \$(ls -1t $APP/releases | paste -sd' ' -)\"
    echo \"  service:  \$(systemctl is-active cupboard-pi.service)\""
