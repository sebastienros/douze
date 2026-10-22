#!/usr/bin/env bash
# Usage: bench/fetch-api-data.sh <git checkout> <destination> <folder>...
# Copies the JSON folders (paths in the repository, e.g. src/data/pkgs) of the checkout's origin/main into the
# destination, each under its own name (destination/pkgs). The api-reference scenario reads bench/api-reference/data.
# It reads from git objects, so the checkout's working tree and branch are left alone; only origin is fetched.
set -euo pipefail
if [ $# -lt 3 ]; then
  echo "usage: $0 <git checkout> <destination> <folder>..." >&2
  exit 2
fi
repo=$1
dest=$2
shift 2
git -C "$repo" fetch -q origin main
rm -rf "$dest"
mkdir -p "$dest"
summary=()
for folder in "$@"; do
  folder=${folder%/}
  parents=$(echo "$folder" | tr -cd / | wc -c | tr -d ' ')
  git -C "$repo" archive origin/main "$folder" | tar -x -C "$dest" --strip-components="$parents"
  name=$(basename "$folder")
  summary+=("$(ls "$dest/$name" | wc -l | tr -d ' ') files in $name")
done
echo "$(IFS=,; echo "${summary[*]}" | sed 's/,/, /g') from $(git -C "$repo" rev-parse --short origin/main) in $dest ($(du -sh "$dest" | cut -f1))"
