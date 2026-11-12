#!/usr/bin/env bash
# One commit (REF), three ways, in alternating passes on the same machine:
#   build10-rt10  the shipped net10.0 build on the .NET 10 runtime
#   build10-rt11  the same build on .NET 11 — what a consumer gets by upgrading the runtime
#   build11-rt11  library and harness compiled for net11.0 — what a net11.0 target would add
# SUITE=read (default) runs TriasDev.Tabular.Benchmarks over the synthetic fixtures; SUITE=write runs
# TriasDev.Tabular.WriteComparison restricted to TriasDev.Tabular's own writers (TABULAR_RUNS inside
# each pass, default 1). PASSES (default 5) repetitions of all three.
OUT=/root/out
REF="${REF:-main}"
REPO_URL="${REPO_URL:-https://github.com/TriasDev/tabular.git}"
SUITE="${SUITE:-read}"
source /root/setup.sh

clone /root/tabular "$REF"
cp -r /root/tabular /root/tabular11
cd /root/tabular11
sed -i 's#<TargetFrameworks>net10.0</TargetFrameworks>#<TargetFrameworks>net11.0</TargetFrameworks>#' src/Directory.Build.props
sed -i 's#<TargetFramework>net10.0</TargetFramework>#<TargetFramework>net11.0</TargetFramework>#' Directory.Build.props
git diff --stat

if [ "$SUITE" = write ]; then PROJECT=TriasDev.Tabular.WriteComparison; else PROJECT=TriasDev.Tabular.Benchmarks; fi

echo "== $(date -u +%FT%TZ) build"
dotnet build "/root/tabular/benchmarks/$PROJECT" -c Release -v q -nologo
dotnet build "/root/tabular11/benchmarks/$PROJECT" -c Release -v q -nologo -p:EnablePackageValidation=false
BUILD10="/root/tabular/benchmarks/$PROJECT/bin/Release/net10.0/$PROJECT.dll"
BUILD11="/root/tabular11/benchmarks/$PROJECT/bin/Release/net11.0/$PROJECT.dll"

if [ "$SUITE" = write ]; then
  export TABULAR_WRITERS=TriasDev.Tabular TABULAR_RUNS="${TABULAR_RUNS:-1}" TABULAR_OUT=/root/written
else
  dotnet build /root/tabular/benchmarks/TriasDev.Tabular.FixtureGenerator -c Release -v q -nologo
  fixtures /root/tabular
fi

for pass in $(seq 1 "${PASSES:-5}"); do
  echo "== $(date -u +%FT%TZ) pass $pass build10-rt10"
  dotnet "$BUILD10" > "$OUT/pass$pass-build10-rt10.md"
  echo "== $(date -u +%FT%TZ) pass $pass build10-rt11"
  DOTNET_ROLL_FORWARD=LatestMajor dotnet "$BUILD10" > "$OUT/pass$pass-build10-rt11.md"
  echo "== $(date -u +%FT%TZ) pass $pass build11-rt11"
  dotnet "$BUILD11" > "$OUT/pass$pass-build11-rt11.md"
done

echo "== $(date -u +%FT%TZ) done"
touch "$OUT/DONE"
