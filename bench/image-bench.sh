#!/usr/bin/env bash
# NetVips against SkiaSharp, buffers against streams (bench/README.md, "Images: NetVips or SkiaSharp, buffers or
# streams"). macOS: /usr/bin/time -l gives the peak memory footprint of each run, in its own process.
#
#   bench/image-bench.sh <sources folder> <single sources folder> <svg> <scratch folder>
#
# <sources folder>: the PNG and JPEG files of a site (searched recursively); <single sources folder>: a few files run one
# at a time (per operation rows); <svg>: an SVG of 1200x630 whose text contains {{n}} (replaced by the index of the
# card); outputs go under <scratch folder> and are deleted after each comparison.
set -euo pipefail

sources=$1
singles=$2
svg=$3
scratch=$4
root=$(cd "$(dirname "$0")/.." && pwd)
bin="$root/bench/out/imagebench/Douze.ImageBench"

dotnet build "$root/bench/Douze.ImageBench" -c Release -o "$root/bench/out/imagebench" -v q -nologo >/dev/null

# Runs the bench once under /usr/bin/time; prints its line and the footprint /usr/bin/time measured.
run() {
  local log
  log=$(mktemp)
  local line
  line=$(/usr/bin/time -l "$bin" "$@" 2>"$log")
  local footprint
  footprint=$(awk '/peak memory footprint/ { printf "%.1f MB", $1 / 1048576 }' "$log")
  rm -f "$log"
  echo "$line | time -l footprint $footprint"
}

echo "## Per operation (-p 1, 5 repeats)"
for file in "$singles"/*; do
  for variant in "webp 640" "webp 1280" "jpeg 1280"; do
    set -- $variant
    for lib in vips skia; do
      for io in buffer stream; do
        rm -rf "$scratch/op"
        echo "$(basename "$file") $1 $2: $(run variants --lib "$lib" --io "$io" --inputs "$file" --out "$scratch/op" --parallel 1 --widths "$2" --formats "$1" --repeat 5)"
      done
    done
  done
done
rm -rf "$scratch/op"

echo "## Batch: every source to 640 and 1280 wide WebP"
for parallel in 4 14; do
  for lib in vips skia; do
    for io in buffer buffer+memo stream; do
      rm -rf "$scratch/batch-$lib-$io"
      memo=()
      [[ $io == buffer+memo ]] && memo=(--memo)
      run variants --lib "$lib" --io "${io%+memo}" ${memo[@]+"${memo[@]}"} --inputs "$sources" --out "$scratch/batch-$lib-$io" --parallel "$parallel"
    done
  done
  echo "Outputs, vips buffer against vips stream: $(diff -rq "$scratch/batch-vips-buffer" "$scratch/batch-vips-stream" >/dev/null && echo identical || echo different)"
  echo "Outputs, skia buffer against skia stream: $(diff -rq "$scratch/batch-skia-buffer" "$scratch/batch-skia-stream" >/dev/null && echo identical || echo different)"
  echo "vips against skia: $("$bin" compare "$scratch/batch-vips-buffer" "$scratch/batch-skia-buffer")"
  rm -rf "$scratch"/batch-*
done

echo "## SVG to 1200x630 PNG, 100 cards"
for parallel in 4 14; do
  for lib in vips skia; do
    rm -rf "$scratch/svg-$lib"
    run svg --lib "$lib" --svg "$svg" --out "$scratch/svg-$lib" --count 100 --parallel "$parallel"
  done
  echo "vips against skia: $("$bin" compare "$scratch/svg-vips" "$scratch/svg-skia")"
  rm -rf "$scratch"/svg-*
done
