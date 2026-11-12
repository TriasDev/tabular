#!/usr/bin/env bash
# The published comparison (docs/benchmarks.md) of one commit (REF), everything on the same machine:
#   comparison-read.md    TriasDev.Tabular.Comparison over the four synthetic fixtures
#   comparison-write.md   TriasDev.Tabular.WriteComparison, every library, CompressionLevel.Fastest
#   compression-optimal.md  the same for TriasDev.Tabular alone with CompressionLevel.Optimal
#   own-pass<N>.md        TriasDev.Tabular.Benchmarks (reader against full analysis), two passes
# TABULAR_RUNS (pass 3 for the published figures; hetzner.sh's default is 1) is the runs per library
# inside each comparison. Takes about an hour on a ccx33.
OUT=/root/out
REF="${REF:-main}"
REPO_URL="${REPO_URL:-https://github.com/TriasDev/tabular.git}"
source /root/setup.sh

clone /root/tabular "$REF"

echo "== $(date -u +%FT%TZ) build"
for project in TriasDev.Tabular.Comparison TriasDev.Tabular.WriteComparison TriasDev.Tabular.Benchmarks TriasDev.Tabular.FixtureGenerator; do
  dotnet build "/root/tabular/benchmarks/$project" -c Release -v q -nologo
done
fixtures /root/tabular

run() { dotnet "/root/tabular/benchmarks/$1/bin/Release/net10.0/$1.dll"; }

echo "== $(date -u +%FT%TZ) comparison read"
TABULAR_TIMEOUT=1800 run TriasDev.Tabular.Comparison > "$OUT/comparison-read.md" || echo "comparison read exited $?"

echo "== $(date -u +%FT%TZ) comparison write"
TABULAR_OUT=/root/written run TriasDev.Tabular.WriteComparison > "$OUT/comparison-write.md" || echo "comparison write exited $?"

echo "== $(date -u +%FT%TZ) compression optimal"
TABULAR_OUT=/root/written TABULAR_WRITERS=TriasDev.Tabular TABULAR_COMPRESSION=Optimal \
  run TriasDev.Tabular.WriteComparison > "$OUT/compression-optimal.md" || echo "compression exited $?"

for pass in 1 2; do
  echo "== $(date -u +%FT%TZ) own pass $pass"
  run TriasDev.Tabular.Benchmarks > "$OUT/own-pass$pass.md"
done

echo "== $(date -u +%FT%TZ) done"
touch "$OUT/DONE"
