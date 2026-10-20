#!/usr/bin/env bash
# Fetch the MongoDB crypt_shared library and the mongocryptd binary the same way Evergreen does (drivers-evergreen-tools'
# mongodl.py) and print the shell lines that export CRYPT_SHARED_LIB_PATH and MONGODB_BINARIES, so the encryption tests
# run instead of skipping (the tests use crypt_shared for one provider and mongocryptd for the other).
# Usage: eval "$(tests/tools/fetch-crypt-shared.sh [version])"     (version defaults to latest-release, e.g. 8.0.11)
# Env:   CRYPT_SHARED_CACHE  cache directory (default ~/.cache/mongo-efcore-provider/crypt_shared)
#        PYTHON              Python interpreter (default: python3, python, py)
set -euo pipefail
version="${1:-latest-release}"
cache="${CRYPT_SHARED_CACHE:-$HOME/.cache/mongo-efcore-provider/crypt_shared}"
py="${PYTHON:-}"
if [ -z "$py" ]; then
  for c in python3 python py; do command -v "$c" >/dev/null 2>&1 && { py="$c"; break; }; done
fi
[ -n "$py" ] || { echo "No Python interpreter found (tried python3, python, py); set PYTHON=<cmd>." >&2; exit 2; }
command -v git >/dev/null 2>&1 || { echo "git is required." >&2; exit 2; }

mkdir -p "$cache"
tools="$cache/drivers-evergreen-tools"
if [ -d "$tools/.git" ]; then
  git -C "$tools" pull -q --ff-only >&2 || echo "Could not update drivers-evergreen-tools; using the cached copy." >&2
else
  git clone -q --depth 1 https://github.com/mongodb-labs/drivers-evergreen-tools.git "$tools" >&2
fi

out="$cache/$version"
# Evergreen's resolution: --version falls back to latest-release when no exact crypt_shared match exists. mongodl.py
# imports a sibling module, so it can't run with -I; run it from the cache dir so the caller's directory can't shadow it.
(cd "$cache" && "$py" "$tools/.evergreen/mongodl.py" --component crypt_shared --version "$version" --out "$out" >&2)
lib="$(find "$out" -type f \( -name 'mongo_crypt_v1.dll' -o -name 'mongo_crypt_v1.so' -o -name 'mongo_crypt_v1.dylib' \) | head -n 1)"
[ -n "$lib" ] || { echo "mongo_crypt_v1 library not found under $out" >&2; exit 1; }

# mongocryptd ships only in the full server archive; extract just that one file (the archive download is cached).
bin="$cache/mongocryptd-$version"
if [ ! -x "$bin/mongocryptd" ] && [ ! -x "$bin/mongocryptd.exe" ]; then
  (cd "$cache" && "$py" "$tools/.evergreen/mongodl.py" --component archive --edition enterprise --version "$version" \
    --out "$bin" --only '*/bin/mongocryptd*' --strip-path-components 2 >&2)
fi
[ -e "$bin/mongocryptd" ] || [ -e "$bin/mongocryptd.exe" ] || { echo "mongocryptd not found under $bin" >&2; exit 1; }
echo "export CRYPT_SHARED_LIB_PATH='$lib'"
echo "export MONGODB_BINARIES='$bin'"
