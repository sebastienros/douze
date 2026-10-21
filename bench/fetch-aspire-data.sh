#!/usr/bin/env bash
# Usage: bench/fetch-aspire-data.sh [aspire.dev checkout] [destination]
#   (defaults: ~/github/aspire.dev, bench/aspire/data)
# Copies the API reference data (pkgs/*.json, ts-modules/*.json) of aspire.dev's origin/main into the destination.
# It reads from git objects, so the checkout's working tree and branch are left alone; only origin is fetched.
set -euo pipefail
repo=${1:-$HOME/github/aspire.dev}
dest=${2:-$(dirname "$0")/aspire/data}
data=src/frontend/src/data
git -C "$repo" fetch -q origin main
rm -rf "$dest"
mkdir -p "$dest"
git -C "$repo" archive origin/main "$data/pkgs" "$data/ts-modules" | tar -x -C "$dest" --strip-components=4
echo "$(ls "$dest/pkgs" | wc -l | tr -d ' ') packages and $(ls "$dest/ts-modules" | wc -l | tr -d ' ') TypeScript modules from $(git -C "$repo" rev-parse --short origin/main) in $dest ($(du -sh "$dest" | cut -f1))"
