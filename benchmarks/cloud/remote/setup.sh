# Sourced by the remote scripts: logging, the .NET SDK and runtimes, the machine's description and
# the synthetic fixtures. Expects OUT to be set.
set -euo pipefail
mkdir -p "$OUT"
exec > >(tee -a "$OUT/remote.log") 2>&1
trap 'echo "FAILED at line $LINENO"; touch "$OUT/FAILED"' ERR

echo "== $(date -u +%FT%TZ) setup"
export DEBIAN_FRONTEND=noninteractive
apt-get update -q >/dev/null
apt-get install -y -q git curl libicu74 >/dev/null

# The SDK global.json asks for, and the .NET 10 runtime the shipped net10.0 build runs on.
curl -sSL https://dot.net/v1/dotnet-install.sh -o /root/dotnet-install.sh
bash /root/dotnet-install.sh --channel 11.0 --install-dir /opt/dotnet >/dev/null
bash /root/dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir /opt/dotnet >/dev/null
export PATH=/opt/dotnet:$PATH DOTNET_ROOT=/opt/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

{
  echo "date: $(date -u +%FT%TZ)"
  echo "cpu: $(lscpu | sed -n 's/^Model name: *//p')"
  echo "cores: $(nproc)"
  echo "memory: $(free -g | awk '/Mem:/{print $2}') GB"
  echo "os: $(. /etc/os-release; echo "$PRETTY_NAME"), kernel $(uname -r)"
  echo "runtimes: $(dotnet --list-runtimes | awk '/NETCore.App/{print $2}' | paste -sd' ')"
} | tee "$OUT/machine.txt"

# clone <dir> <ref>: the public repository at a branch, tag or commit.
clone() {
  git clone -q "$REPO_URL" "$1"
  git -C "$1" checkout -q "$2"
  echo "$1: $(git -C "$1" rev-parse --short HEAD) $(git -C "$1" log -1 --format=%s)" | tee -a "$OUT/commits.txt"
}

# fixtures <repo-dir>: the synthetic files of the published shape (benchmarks/TriasDev.Tabular.FixtureGenerator).
fixtures() {
  echo "== $(date -u +%FT%TZ) fixtures"
  dotnet run -c Release --project "$1/benchmarks/TriasDev.Tabular.FixtureGenerator" -- /root/fixtures ${FIXTURE_SCALE:-}
  ls -la /root/fixtures > "$OUT/fixtures.txt"
}

export TABULAR_FIXTURES=/root/fixtures
export TABULAR_FILES="${TABULAR_FILES:-workbook-100k.xlsx,workbook-1m.xlsx,clean-3m.csv,malformed-5m.csv}"
