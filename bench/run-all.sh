#!/usr/bin/env bash
# Usage: bench/run-all.sh [repeat]   (default: 3)
# Publishes the native AOT douze to tool-out, generates every scenario of bench/README.md under bench/*/data and
# prints one Markdown table row per scenario (progress goes to stderr). The API reference scenarios run
# when bench/api-reference/data/pkgs exists (bench/fetch-api-data.sh).
set -euo pipefail
cd "$(dirname "$0")/.."
repeat=${1:-3}
bench=bench/out/bin/Douze.Bench

dotnet publish Douze -c Release -o tool-out >&2
dotnet build bench/Douze.Bench -c Release -o bench/out/bin >&2

header=1
run() { # label site [douze options]
  local label=$1 site=$2
  shift 2
  local row
  row=$("$bench" run --site "$site" --out "bench/out/site" --repeat "$repeat" --label "$label" -- "$@")
  if [ $header = 1 ]; then echo "$row"; header=0; else echo "$row" | tail -1; fi
}

for pages in 1000 4000 8000 16000 32000; do
  "$bench" synthetic --pages $pages --out bench/synthetic/data/$pages >&2
  run "synthetic $pages" bench/synthetic/data/$pages
done

for nav in index none; do
  "$bench" synthetic --pages 32000 --nav $nav --out bench/synthetic/data/32000-$nav >&2
  run "synthetic 32000, nav $nav" bench/synthetic/data/32000-$nav
done

run "synthetic 32000, nav index, -p 4" bench/synthetic/data/32000-index -p 4
run "synthetic 32000, no cache" bench/synthetic/data/32000 --no-cache

# Builds from the disk cache: no change, one page edited, the tags of one page edited.
cached() { # label site mode
  local row
  row=$("$bench" run --site "$2" --out "bench/out/site" --repeat "$repeat" --label "$1" --cache "$3")
  echo "$row" | tail -1
}

cached "synthetic 32000, cache, no change" bench/synthetic/data/32000 warm
cached "synthetic 32000, cache, one page edited" bench/synthetic/data/32000 edit
cached "synthetic 32000, cache, one tag edited" bench/synthetic/data/32000 tag

if [ -d bench/api-reference/data/pkgs ]; then
  "$bench" api-reference --data bench/api-reference/data --out bench/api-reference/data/site-data --mode data >&2
  run "API reference data only" bench/api-reference/data/site-data
  "$bench" api-reference --data bench/api-reference/data --out bench/api-reference/data/site >&2
  run "API reference pages" bench/api-reference/data/site
  cached "API reference pages, cache, no change" bench/api-reference/data/site warm
fi
