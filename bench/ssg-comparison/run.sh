#!/usr/bin/env bash
# Reproduces Zach Leatherman's "Which generator builds Markdown the fastest?" benchmark
# (https://github.com/zachleat/bench-framework-markdown) in Docker, for Douze, Hugo, Eleventy, Astro and Jekyll.
#
#   ./run.sh                     every generator, sizes 250 500 1000 2000 4000, 3 runs each
#   ./run.sh douze hugo          some generators
#   SIZES="250 1000" RUNS=1 ./run.sh
#
# Douze is built from the HEAD commit of this repository (git archive), as native AOT, inside Docker.
# Results: results.json and results.md next to this script; raw runs, logs and versions in .work/out/.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(git -C "$here" rev-parse --show-toplevel)"
source "$here/versions.env"
work="$here/.work"
generators=("$@")
[ ${#generators[@]} -gt 0 ] || generators=(douze hugo eleventy astro jekyll)
export SIZES="${SIZES:-250 500 1000 2000 4000}"
export RUNS="${RUNS:-3}"

mkdir -p "$work/out"

# The benchmark repository at the pinned commit: Markdown samples and the Hugo scenario.
if [ ! -d "$work/bench-framework-markdown/.git" ]; then
  git clone -q "$BENCH_REPO" "$work/bench-framework-markdown"
fi
git -C "$work/bench-framework-markdown" checkout -q "$BENCH_COMMIT"
samples="$work/bench-framework-markdown/_markdown-samples"

# Douze sources: the committed HEAD of this repository, without bin/obj.
rm -rf "$work/douze-src" && mkdir -p "$work/douze-src"
git -C "$repo" archive HEAD | tar -x -C "$work/douze-src"
git -C "$repo" rev-parse HEAD > "$work/out/douze-commit.txt"

build_args=()
while IFS='=' read -r key value; do
  [[ "$key" =~ ^[A-Z_]+$ ]] && build_args+=(--build-arg "$key=$value")
done < "$here/versions.env"

for gen in "${generators[@]}"; do
  contexts=(--build-context "samples=$samples" --build-context "bench=$here")
  case "$gen" in
    douze) contexts+=(--build-context "src=$work/douze-src" --build-context "scenario=$here/scenarios/douze") ;;
    hugo) contexts+=(--build-context "scenario=$work/bench-framework-markdown/hugo/quickstart") ;;
  esac
  echo "[bench] building image ssgbench-$gen"
  docker build -q -f "$here/docker/$gen.Dockerfile" "${build_args[@]}" "${contexts[@]}" -t "ssgbench-$gen" "$here/docker" > /dev/null

  echo "[bench] running $gen (sizes: $SIZES, runs: $RUNS)"
  mkdir -p "$work/out/$gen"
  # Same resources for every generator: all the CPUs and memory of the Docker VM. The site lives in the container's
  # own file system; only the results folder is mounted.
  docker run --rm -e SIZES -e RUNS -v "$work/out/$gen:/out" "ssgbench-$gen"
done

# Environment
{
  echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host: $(sysctl -n machdep.cpu.brand_string 2>/dev/null || uname -m), $(sysctl -n hw.ncpu 2>/dev/null || nproc) cores, $(( $(sysctl -n hw.memsize 2>/dev/null || echo 0) / 1073741824 )) GB"
  echo "host os: $(sw_vers -productName 2>/dev/null) $(sw_vers -productVersion 2>/dev/null) $(uname -r)"
  docker info --format 'docker: {{.ServerVersion}} {{.OperatingSystem}} {{.KernelVersion}} {{.Architecture}}, {{.NCPU}} CPUs, {{.MemTotal}} bytes'
  echo "douze commit: $(cat "$work/out/douze-commit.txt")"
  echo "benchmark commit: $BENCH_COMMIT"
} > "$work/out/environment.txt"

docker run --rm -v "$here:/bench" -w /bench "$NODE_IMAGE" node report.mjs .work/out
echo "[bench] wrote $here/results.json and $here/results.md"
