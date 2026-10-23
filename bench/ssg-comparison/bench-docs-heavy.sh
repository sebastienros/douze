#!/usr/bin/env bash
# Runs inside a docs-heavy image (its entry point): for each size, RUNS clean builds of the site in /site with the
# pages of /samples/<size>. Before each run it deletes the output, the caches and the pages, then copies the pages and
# the sidebar data in; only the build command is timed (wall clock, bash EPOCHREALTIME), along with the CPU time of
# the build process and its children (user and sys, from bash's `times`). A build is stopped after TIMEOUT seconds
# (default 900). Each run checks the page count, that every page has highlighted code (token spans) and heading ids.
# Generators with an incremental mode also get warm rebuilds (nothing changed). Writes one JSON line per run to
# /out/<generator>.jsonl, the build logs to /out/logs/, the versions to /out/<generator>.versions.txt and one sample page
# to /out/<generator>-sample.html.
#
# Usage: bench-docs-heavy.sh <generator>   (SIZES, RUNS and TIMEOUT from the environment)
set -euo pipefail

gen="$1"
sizes="${SIZES:-1000 4000 16000}"
runs="${RUNS:-3}"
timeout_s="${TIMEOUT:-900}"
out=/out
mkdir -p "$out/logs"
: > "$out/$gen.jsonl"

# Per generator: where pages and data go, what to clean, the build commands, the output folder and the pattern of a
# highlighted token. warm_prepare (untimed) and warm_cmd exist for generators with an incremental mode.
warm_cmd=""
case "$gen" in
  douze)
    pages=_content/docs; data=_data/sidebar.json; out_dir=_site
    clean() { rm -rf _site .douze; }
    cmd="douze --no-cache -q"
    warm_prepare="douze -q"; warm_cmd="douze -q"; warm_clean() { :; }
    token='<span style="--0:#'
    versions() { echo "douze $(douze --version)"; }
    ;;
  hugo)
    pages=content/docs; data=data/sidebar.json; out_dir=public
    clean() { rm -rf public resources/_gen .hugo_build.lock /root/.cache/hugo_cache; }
    cmd="hugo --quiet"
    token='<span style="color:#'
    versions() { hugo version; }
    ;;
  eleventy)
    pages=content/docs; data=_data/sidebar.json; out_dir=_site
    clean() { rm -rf _site .cache node_modules/.cache; }
    cmd="npx @11ty/eleventy --quiet"
    token='<span class="token '
    versions() { echo "node $(node --version)"; npm ls --depth=0 2>/dev/null | tail -n +2; }
    ;;
  astro)
    pages=src/content/docs; data=src/data/sidebar.json; out_dir=dist
    clean() { rm -rf dist .astro node_modules/.astro node_modules/.vite; }
    cmd="npx astro build --silent"
    # The content layer keeps the parsed and rendered Markdown in its data store (node_modules/.astro) between builds.
    warm_prepare="$cmd"; warm_cmd="$cmd"; warm_clean() { :; }
    token='<span style="color:#'
    versions() { echo "node $(node --version)"; echo "astro $(npx astro --version | tr -d '\n')"; npm ls --depth=0 2>/dev/null | tail -n +2; }
    ;;
  jekyll)
    pages=docs; data=_data/sidebar.json; out_dir=_site
    clean() { rm -rf _site .jekyll-cache .sass-cache .jekyll-metadata; }
    cmd="bundle exec jekyll build --quiet"
    # --incremental (experimental in Jekyll) only regenerates the pages whose source changed, from .jekyll-metadata.
    warm_prepare="bundle exec jekyll build --quiet --incremental"; warm_cmd="$warm_prepare"; warm_clean() { :; }
    token='<span class="(k|kd|kn|kt|nt|nb|nx|nf|nc|na|nv|s|s1|s2|p|o|c|c1|mi)">'
    versions() { echo "ruby $(ruby --version)"; bundle exec jekyll --version; bundle list 2>/dev/null | grep -E 'jekyll|kramdown|rouge' || true; }
    ;;
  *) echo "unknown generator $gen" >&2; exit 2 ;;
esac

versions > "$out/$gen.versions.txt" 2>&1
echo "nproc $(nproc), memory $(awk '/MemTotal/ { print $2 " kB" }' /proc/meminfo)" >> "$out/$gen.versions.txt"

# CPU time of the finished children of this shell (user + sys), in seconds. `times` runs in this shell: no subshell.
children_cpu() { # sets cpu_now; must not run in a subshell, whose children are not those of this shell
  times > /tmp/times.txt
  cpu_now=$(awk 'NR == 2 { n = split($1 " " $2, t, " "); s = 0; for (i = 1; i <= n; i++) { split(t[i], p, /[ms]/); s += p[1] * 60 + p[2] } print s }' /tmp/times.txt)
}

run_build() { # command log
  local start end cpu0 cpu1
  children_cpu; cpu0=$cpu_now
  start=$EPOCHREALTIME
  if timeout --kill-after=10 "$timeout_s" bash -c "$1" > "$2" 2>&1; then status=0; else status=$?; fi
  end=$EPOCHREALTIME
  children_cpu; cpu1=$cpu_now
  seconds=$(awk -v s="$start" -v e="$end" 'BEGIN { printf "%.3f", e - s }')
  cpu=$(awk -v a="$cpu0" -v b="$cpu1" 'BEGIN { printf "%.3f", b - a }')
}

record() { # kind size run
  local html docs highlighted ids ok=true timed_out=false
  html=$(find "$out_dir" -name "*.html" 2>/dev/null | wc -l || true)
  docs=$(find "$out_dir/docs" -mindepth 2 -maxdepth 2 -name index.html 2>/dev/null | wc -l || true)
  highlighted=$(find "$out_dir/docs" -mindepth 2 -maxdepth 2 -name index.html -print0 2>/dev/null | xargs -0 -r grep -lE "$token" | wc -l || true)
  ids=$(find "$out_dir/docs" -mindepth 2 -maxdepth 2 -name index.html -print0 2>/dev/null | xargs -0 -r grep -l "<h2 id=\"" | wc -l || true)
  if [ "$status" -eq 124 ] || [ "$status" -eq 137 ]; then timed_out=true; fi
  if [ "$status" -ne 0 ] || [ "$docs" -ne "$2" ] || [ "$highlighted" -ne "$2" ] || [ "$ids" -ne "$2" ]; then ok=false; fi
  printf '{"generator":"%s","kind":"%s","size":%s,"run":%s,"seconds":%s,"cpu":%s,"exit":%s,"timeout":%s,"html":%s,"pages":%s,"highlighted":%s,"ids":%s,"ok":%s}\n' \
    "$gen" "$1" "$2" "$3" "$seconds" "$cpu" "$status" "$timed_out" "$html" "$docs" "$highlighted" "$ids" "$ok" | tee -a "$out/$gen.jsonl"
  [ "$timed_out" = false ]
}

prepare() { # size
  clean
  mkdir -p "$pages" "$(dirname "$data")" && find "$pages" -name "page-*.md" -delete
  cp "/samples/$1/pages/"*.md "$pages/"
  cp "/samples/$1/sidebar.json" "$data"
}

for size in $sizes; do
  for run in $(seq 1 "$runs"); do
    prepare "$size"
    run_build "$cmd" "$out/logs/$gen-$size-$run.log"
    # A build that timed out at a size is not run again, nor at larger sizes.
    record clean "$size" "$run" || break 2
  done
  cp "$out_dir/docs/page-00001/index.html" "$out/$gen-sample.html" 2>/dev/null || true

  if [ -n "$warm_cmd" ]; then
    prepare "$size"
    timeout --kill-after=10 "$timeout_s" bash -c "$warm_prepare" > "$out/logs/$gen-$size-cache.log" 2>&1 || true
    for run in $(seq 1 "$runs"); do
      warm_clean
      run_build "$warm_cmd" "$out/logs/$gen-$size-warm-$run.log"
      record warm "$size" "$run" || break
    done
  fi
done
clean
