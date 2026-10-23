#!/usr/bin/env bash
# The docs-heavy scenario: documentation pages of about 8 KB with headings, a table and 3 to 6 code fences, built by
# Douze, Hugo, Eleventy, Astro and Jekyll with build-time highlighting and heading ids, in Docker.
#
#   ./run-docs-heavy.sh                   every generator, sizes 1000 4000 16000, 3 runs each
#   ./run-docs-heavy.sh douze hugo        some generators
#   SIZES="1000" RUNS=1 ./run-docs-heavy.sh
#
# The pages come from scenarios/docs-heavy/generate.mjs (seeded). Douze is built from the HEAD commit of this
# repository (git archive), as native AOT, inside Docker. Results: results-docs-heavy.json and results-docs-heavy.md
# next to this script; raw runs, logs and versions in .work/docs-heavy/out/.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(git -C "$here" rev-parse --show-toplevel)"
source "$here/versions.env"
work="$here/.work/docs-heavy"
generators=("$@")
[ ${#generators[@]} -gt 0 ] || generators=(douze hugo eleventy astro jekyll)
export SIZES="${SIZES:-1000 4000 16000}"
export RUNS="${RUNS:-3}"
export TIMEOUT="${TIMEOUT:-900}"

mkdir -p "$work/out"

# The pages, one folder per size (page i only depends on i).
rm -rf "$work/content"
for size in $SIZES; do
  docker run --rm -v "$here/scenarios/docs-heavy:/gen:ro" -v "$work/content:/content" "$NODE_IMAGE" \
    node /gen/generate.mjs "$size" "/content/$size/pages"
done

# Douze sources: the committed HEAD of this repository, without bin/obj.
rm -rf "$work/douze-src" && mkdir -p "$work/douze-src"
git -C "$repo" archive HEAD | tar -x -C "$work/douze-src"
git -C "$repo" rev-parse HEAD > "$work/out/douze-commit.txt"

build_args=()
while IFS='=' read -r key value; do
  [[ "$key" =~ ^[A-Z_]+$ ]] && build_args+=(--build-arg "$key=$value")
done < "$here/versions.env"

for gen in "${generators[@]}"; do
  contexts=(--build-context "samples=$work/content" --build-context "bench=$here" --build-context "scenario=$here/scenarios/docs-heavy/$gen")
  [ "$gen" = douze ] && contexts+=(--build-context "src=$work/douze-src")
  echo "[bench] building image ssgbench-docs-$gen"
  docker build -q -f "$here/docker/docs-heavy/$gen.Dockerfile" "${build_args[@]}" "${contexts[@]}" -t "ssgbench-docs-$gen" "$here/docker" > /dev/null

  echo "[bench] running $gen (sizes: $SIZES, runs: $RUNS)"
  rm -rf "$work/out/$gen" && mkdir -p "$work/out/$gen"
  # Every container gets all the CPUs and memory of the Docker VM; the site is in the container's file system.
  docker run --rm -e SIZES -e RUNS -e TIMEOUT -v "$work/out/$gen:/out" "ssgbench-docs-$gen"
done

{
  echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host: $(sysctl -n machdep.cpu.brand_string 2>/dev/null || uname -m), $(sysctl -n hw.ncpu 2>/dev/null || nproc) cores, $(( $(sysctl -n hw.memsize 2>/dev/null || echo 0) / 1073741824 )) GB"
  echo "host os: $(sw_vers -productName 2>/dev/null) $(sw_vers -productVersion 2>/dev/null) $(uname -r)"
  docker info --format 'docker: {{.ServerVersion}} {{.OperatingSystem}} {{.KernelVersion}} {{.Architecture}}, {{.NCPU}} CPUs, {{.MemTotal}} bytes'
  echo "douze commit: $(cat "$work/out/douze-commit.txt")"
} > "$work/out/environment.txt"

docker run --rm -v "$here:/bench" -w /bench "$NODE_IMAGE" node report-docs-heavy.mjs .work/docs-heavy/out
echo "[bench] wrote $here/results-docs-heavy.json and $here/results-docs-heavy.md"
