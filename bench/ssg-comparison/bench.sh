#!/usr/bin/env bash
# Runs inside a generator's container (the entry point of every image): for each size, RUNS clean builds of the site in
# /site with the first <size> Markdown samples of /samples/<size>. Before each run it deletes the output, the caches and
# the posts, then copies the posts in; only the build command is timed (wall clock, bash EPOCHREALTIME). Writes one
# JSON line per run to /out/<generator>.jsonl, the build logs to /out/logs/, and the versions to
# /out/<generator>.versions.txt.
#
# Usage: bench.sh <generator>   (SIZES and RUNS from the environment)
set -euo pipefail

gen="$1"
sizes="${SIZES:-250 500 1000 2000 4000}"
runs="${RUNS:-3}"
out=/out
mkdir -p "$out/logs"
: > "$out/$gen.jsonl"

# Per generator: where posts go, how to clean, the build command, and where the post pages are written.
case "$gen" in
  douze)
    clean() { rm -rf _site .douze; find _content/blog -name '*.md' -delete; }
    copy_posts() { cp "$1"/*.md _content/blog/; }
    build() { douze --no-cache -q; }
    out_dir=_site
    post_pages() { find _site/blog -mindepth 2 -maxdepth 2 -name index.html | wc -l; }
    versions() { echo "douze $(douze --version)"; }
    ;;
  eleventy)
    clean() { rm -rf _site .cache node_modules/.cache; find content/blog -name '*.md' -delete; }
    copy_posts() { cp "$1"/*.md content/blog/; }
    build() { npx @11ty/eleventy --quiet; }
    out_dir=_site
    post_pages() { find _site/blog -mindepth 2 -maxdepth 2 -name index.html | wc -l; }
    versions() { echo "node $(node --version)"; echo "eleventy $(npx @11ty/eleventy --version)"; npm ls --depth=0 2>/dev/null | tail -n +2; }
    ;;
  astro)
    clean() { rm -rf dist .astro node_modules/.astro node_modules/.vite; find src/content/blog -name '*.md' -delete; }
    copy_posts() { cp "$1"/*.md src/content/blog/; }
    build() { npx astro build --silent; }
    out_dir=dist
    post_pages() { find dist/blog -mindepth 2 -maxdepth 2 -name index.html | wc -l; }
    versions() { echo "node $(node --version)"; echo "astro $(npx astro --version | tr -d '\n')"; npm ls --depth=0 2>/dev/null | tail -n +2; }
    ;;
  hugo)
    clean() { rm -rf public resources/_gen .hugo_build.lock /root/.cache/hugo_cache; find content/posts -name '*.md' -delete 2>/dev/null || true; }
    copy_posts() { mkdir -p content/posts; cp "$1"/*.md content/posts/; }
    build() { hugo --quiet; }
    out_dir=public
    post_pages() { find public/posts -mindepth 2 -maxdepth 2 -name index.html | wc -l; }
    versions() { hugo version; }
    ;;
  jekyll)
    clean() { rm -rf _site .jekyll-cache .sass-cache; find _posts -name '*.md' -delete; }
    # Jekyll only reads posts named YYYY-MM-DD-title.md: same files, with a date prefix.
    copy_posts() { for f in "$1"/*.md; do cp "$f" "_posts/2022-07-29-$(basename "$f")"; done; }
    build() { bundle exec jekyll build --quiet; }
    out_dir=_site
    post_pages() { find _site/2022/07/29 -name '*.html' | wc -l; }
    versions() { echo "ruby $(ruby --version)"; bundle exec jekyll --version; bundle list 2>/dev/null | grep -E 'jekyll|minima|sass' || true; }
    ;;
  *) echo "unknown generator $gen" >&2; exit 2 ;;
esac

versions > "$out/$gen.versions.txt" 2>&1

time_build() {
  local log="$1" start end
  start=$EPOCHREALTIME
  if build > "$log" 2>&1; then status=0; else status=$?; fi
  end=$EPOCHREALTIME
  seconds=$(awk -v s="$start" -v e="$end" 'BEGIN { printf "%.3f", e - s }')
}

record() { # kind size run
  local html posts ok=true
  html=$(find "$out_dir" -name '*.html' 2>/dev/null | wc -l)
  posts=$(post_pages 2>/dev/null || echo 0)
  if [ "$status" -ne 0 ] || [ "$posts" -ne "$2" ]; then ok=false; fi
  printf '{"generator":"%s","kind":"%s","size":%s,"run":%s,"seconds":%s,"exit":%s,"html":%s,"posts":%s,"ok":%s}\n' \
    "$gen" "$1" "$2" "$3" "$seconds" "$status" "$html" "$posts" "$ok" | tee -a "$out/$gen.jsonl"
}

for size in $sizes; do
  for run in $(seq 1 "$runs"); do
    clean
    copy_posts "/samples/$size"
    time_build "$out/logs/$gen-$size-$run.log"
    record clean "$size" "$run"
  done

  # Douze only: rebuilds with its disk cache and nothing changed (a first build writes the cache, untimed).
  if [ "$gen" = douze ]; then
    clean
    copy_posts "/samples/$size"
    douze -q > "$out/logs/$gen-$size-cache.log" 2>&1
    for run in $(seq 1 "$runs"); do
      build() { douze -q; }
      time_build "$out/logs/$gen-$size-warm-$run.log"
      record warm "$size" "$run"
      build() { douze --no-cache -q; }
    done
  fi
done
clean
