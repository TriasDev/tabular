#!/usr/bin/env bash
# Runs a benchmark script on a rented Hetzner Cloud server with dedicated cores, fetches its results,
# and deletes the server and its SSH key on every exit path — success, failure or Ctrl-C.
#
#   benchmarks/cloud/hetzner.sh <remote-script> [results-dir]
#
# <remote-script> is one of benchmarks/cloud/remote/*.sh; it runs as root on a fresh Ubuntu, writes
# what it measures to /root/out and touches /root/out/DONE (or FAILED). Everything the script reads
# comes from the public repository at REF — push a branch before measuring it.
#
# Environment:
#   HCLOUD_TOKEN   Hetzner Cloud API token; if unset, read from .env at the repository root
#                  (HCLOUD_TOKEN=… or HETZNER_API_KEY=…). Never commit it: .env is git-ignored.
#   TYPE           server type (default ccx33: 8 dedicated AMD vCPU, 32 GB). Keep to the CCX line —
#                  the shared-core types are as noisy as a busy laptop.
#   LOCATION       default nbg1
#   PASSES, REF, BASE, HEAD, SUITE, TABULAR_RUNS, FIXTURE_SCALE
#                  passed to the remote script; see each script's header
#   REPO_URL       default https://github.com/TriasDev/tabular.git
#
# Every server and key it creates carries the label purpose=tabular-bench, so a leftover is easy to
# find: hcloud server list -l purpose=tabular-bench.
set -euo pipefail

REMOTE_SCRIPT="${1:?usage: hetzner.sh <remote-script> [results-dir]}"
ROOT="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
NAME="tabular-bench-$(date +%Y%m%d-%H%M%S)"
RESULTS="${2:-$ROOT/artifacts/cloud-bench/$NAME}"
TYPE="${TYPE:-ccx33}"
LOCATION="${LOCATION:-nbg1}"

if [ -z "${HCLOUD_TOKEN:-}" ] && [ -f "$ROOT/.env" ]; then
  HCLOUD_TOKEN="$(grep -E '^(HCLOUD_TOKEN|HETZNER_API_KEY)=' "$ROOT/.env" | head -1 | cut -d= -f2- | tr -d "\"' \r")"
fi
export HCLOUD_TOKEN="${HCLOUD_TOKEN:?no Hetzner token: set HCLOUD_TOKEN or put it in .env}"
command -v hcloud >/dev/null || { echo "needs the hcloud CLI (brew install hcloud)"; exit 1; }

KEYS="$(mktemp -d)"
KEY="$KEYS/$NAME"
mkdir -p "$RESULTS"

cleanup() {
  # Delete whatever happens to the output: a closed pipe must not stop the deletes below.
  set +e
  trap '' PIPE
  echo "cleanup: deleting server and key $NAME"
  hcloud server delete "$NAME" >/dev/null 2>&1 || true
  hcloud ssh-key delete "$NAME" >/dev/null 2>&1 || true
  rm -rf "$KEYS"
  echo "cleanup: servers left with purpose=tabular-bench: $(hcloud server list -l purpose=tabular-bench -o noheader | wc -l | tr -d ' ')"
}
trap cleanup EXIT
# A signal would otherwise end the script without the EXIT trap, leaving the server running — SIGPIPE
# included, which a closed output (`hetzner.sh … | grep …` with grep gone) sends on the next echo.
trap 'exit 130' INT TERM HUP PIPE

ssh-keygen -q -t ed25519 -N "" -C "$NAME" -f "$KEY"
hcloud ssh-key create --name "$NAME" --public-key-from-file "$KEY.pub" --label purpose=tabular-bench >/dev/null
hcloud server create --name "$NAME" --type "$TYPE" --image ubuntu-24.04 --location "$LOCATION" \
  --ssh-key "$NAME" --label purpose=tabular-bench >/dev/null
IP="$(hcloud server ip "$NAME")"
echo "server $NAME ($TYPE, $LOCATION) at $IP"

OPTS=(-i "$KEY" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -o ConnectTimeout=10 -o ServerAliveInterval=30)
SSH=(ssh "${OPTS[@]}" "root@$IP")
for _ in $(seq 1 60); do "${SSH[@]}" true 2>/dev/null && break; sleep 5; done

scp "${OPTS[@]}" "$REMOTE_SCRIPT" "root@$IP:/root/remote.sh"
scp "${OPTS[@]}" "$(dirname "$0")/remote/setup.sh" "root@$IP:/root/setup.sh"
VARS="PASSES=${PASSES:-5} REF=${REF:-main} BASE=${BASE:-} HEAD=${HEAD:-} SUITE=${SUITE:-read} TABULAR_RUNS=${TABULAR_RUNS:-1} FIXTURE_SCALE=${FIXTURE_SCALE:-} REPO_URL=${REPO_URL:-https://github.com/TriasDev/tabular.git}"
"${SSH[@]}" "$VARS nohup bash /root/remote.sh </dev/null >/dev/null 2>&1 &"
echo "remote script started $(date +%T)"

# Wait for DONE or FAILED, at most 90 minutes, printing each new stage the script announces.
seen=""
for _ in $(seq 1 540); do
  sleep 10
  stage="$("${SSH[@]}" "grep '^== ' /root/out/remote.log 2>/dev/null | tail -1" 2>/dev/null || true)"
  if [ -n "$stage" ] && [ "$stage" != "$seen" ]; then echo "$stage"; seen="$stage"; fi
  if "${SSH[@]}" "test -e /root/out/DONE -o -e /root/out/FAILED" 2>/dev/null; then break; fi
done

scp -r "${OPTS[@]}" "root@$IP:/root/out/." "$RESULTS/"
if [ -e "$RESULTS/FAILED" ]; then echo "REMOTE FAILED — $RESULTS/remote.log:"; tail -20 "$RESULTS/remote.log"; exit 1; fi
[ -e "$RESULTS/DONE" ] || { echo "TIMED OUT after 90 minutes"; exit 1; }
echo "results in $RESULTS"
python3 "$(dirname "$0")/summarize.py" "$RESULTS" | tee "$RESULTS/summary.md"
