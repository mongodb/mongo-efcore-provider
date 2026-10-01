#!/usr/bin/env bash
# Differential native-vs-driver parity runner. Requires a prior build of "Debug <ver>".
# Usage: tests/tools/native-parity-diff.sh EF10 /path/to/outdir
set -euo pipefail
v="$1"; out="$2"; mkdir -p "$out/trx"
cd "$(dirname "$0")/../.."
unset MONGODB_URI ATLAS_URI
export MONGODB_EF_SKIP_MQL_ASSERTIONS=1
export MONGODB_EF_DIFFERENTIAL=1
for m in DriverLinq NativeOnly; do
  for p in SpecificationTests FunctionalTests; do
    MONGODB_EF_QUERY_MODE=$m dotnet test "tests/MongoDB.EntityFrameworkCore.$p/MongoDB.EntityFrameworkCore.$p.csproj" \
      -c "Debug $v" --no-build --logger "trx;LogFileName=$v-$m-$p.trx" --results-directory "$out/trx" > "$out/$v-$m-$p.log" 2>&1 &
  done
done
wait || true
python3 "$(dirname "$0")/native-parity-diff.py" "$out" "$v"
