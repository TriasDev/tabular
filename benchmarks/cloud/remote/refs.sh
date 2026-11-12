#!/usr/bin/env bash
# Two commits against each other on the same machine, in alternating passes — the check for a change
# that may touch speed. BASE (default main) and HEAD (required): branches, tags or commits pushed to
# the repository. Both are built as they stand, on the .NET 10 runtime.
# SUITE=read (default) runs TriasDev.Tabular.Benchmarks over the synthetic fixtures, generated once
# by HEAD's generator so both read the same bytes; SUITE=write runs TriasDev.Tabular.WriteComparison
# restricted to TriasDev.Tabular's own writers (TABULAR_RUNS inside each pass, default 1).
# PASSES (default 5) repetitions of both. BASE=HEAD measures the noise floor of the comparison itself.
OUT=/root/out
BASE="${BASE:-main}"
HEAD="${HEAD:?HEAD is required: the branch, tag or commit to compare with BASE}"
REPO_URL="${REPO_URL:-https://github.com/TriasDev/tabular.git}"
SUITE="${SUITE:-read}"
source /root/setup.sh

clone /root/base "$BASE"
clone /root/head "$HEAD"

if [ "$SUITE" = write ]; then PROJECT=TriasDev.Tabular.WriteComparison; else PROJECT=TriasDev.Tabular.Benchmarks; fi

echo "== $(date -u +%FT%TZ) build"
for tree in base head; do
  dotnet build "/root/$tree/benchmarks/$PROJECT" -c Release -v q -nologo
done

if [ "$SUITE" = write ]; then
  export TABULAR_WRITERS=TriasDev.Tabular TABULAR_RUNS="${TABULAR_RUNS:-1}" TABULAR_OUT=/root/written
else
  dotnet build /root/head/benchmarks/TriasDev.Tabular.FixtureGenerator -c Release -v q -nologo
  fixtures /root/head
fi

for pass in $(seq 1 "${PASSES:-5}"); do
  for tree in base head; do
    echo "== $(date -u +%FT%TZ) pass $pass $tree"
    dotnet "/root/$tree/benchmarks/$PROJECT/bin/Release/net10.0/$PROJECT.dll" > "$OUT/pass$pass-$tree.md"
  done
done

echo "== $(date -u +%FT%TZ) done"
touch "$OUT/DONE"
