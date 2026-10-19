#!/usr/bin/env bash
# Differential native-vs-driver parity runner. Requires a prior build of "Debug <ver>".
# Usage: tests/tools/native-parity-diff.sh <EF8|EF9|EF10> <outdir> [--gate]
#   --gate: also exit nonzero when any test regresses (passes under DriverLinq, fails under NativeOnly).
set -euo pipefail
usage() { echo "Usage: $0 <EF8|EF9|EF10> <outdir> [--gate]" >&2; exit 2; }
if [ $# -lt 2 ] || [ $# -gt 3 ]; then usage; fi
gate=()
if [ $# -eq 3 ]; then
  if [ "$3" = "--gate" ]; then gate=(--fail-on-regress); else usage; fi
fi
v="$1"
out="$(mkdir -p "$2" && cd "$2" && pwd)"
mkdir -p "$out/trx"
# Remove stale results so a crashed run cannot be masked by older files.
rm -f "$out/trx/$v-"*.trx "$out/$v-"*-regress.txt "$out/$v-"*-improve.txt "$out/$v-"*-missing.txt
cd "$(dirname "$0")/../.."
unset MONGODB_URI ATLAS_URI
# The NativeOnly alias would override MONGODB_EF_QUERY_MODE for the DriverLinq leg (the resolver now throws on that
# conflict, but never inherit it): each leg's mode comes only from MONGODB_EF_QUERY_MODE below.
unset MONGODB_EF_NATIVE_ONLY
export MONGODB_EF_SKIP_MQL_ASSERTIONS=1
export MONGODB_EF_DIFFERENTIAL=1
for m in DriverLinq NativeOnly; do
  for p in SpecificationTests FunctionalTests; do
    MONGODB_EF_QUERY_MODE=$m dotnet test "tests/MongoDB.EntityFrameworkCore.$p/MongoDB.EntityFrameworkCore.$p.csproj" \
      -c "Debug $v" --no-build --logger "trx;LogFileName=$v-$m-$p.trx" --results-directory "$out/trx" > "$out/$v-$m-$p.log" 2>&1 &
  done
done
wait || true
python3 "tests/tools/native-parity-diff.py" "$out" "$v" ${gate[@]+"${gate[@]}"}
