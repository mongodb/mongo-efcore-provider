#!/usr/bin/env bash
# Differential native-vs-driver parity runner. Requires a prior build of "Debug <ver>".
# Usage: tests/tools/native-parity-diff.sh <EF8|EF9|EF10> <outdir>
set -euo pipefail
if [ $# -ne 2 ]; then echo "Usage: $0 <EF8|EF9|EF10> <outdir>" >&2; exit 2; fi
v="$1"
out="$(mkdir -p "$2" && cd "$2" && pwd)"
mkdir -p "$out/trx"
# Remove stale results so a crashed run cannot be masked by older files.
rm -f "$out/trx/$v-"*.trx "$out/$v-"*-regress.txt "$out/$v-"*-improve.txt "$out/$v-"*-missing.txt
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
python3 "tests/tools/native-parity-diff.py" "$out" "$v"
