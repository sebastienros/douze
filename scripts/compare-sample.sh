#!/usr/bin/env bash
# Builds the Sample with the base ref's Douze and with the working tree's Douze, and diffs the outputs.
# Usage: scripts/compare-sample.sh [base-ref]   (default: main)
set -euo pipefail
base=${1:-main}
tmp=$(mktemp -d)
# --detach: the base branch may be checked out in another worktree.
git worktree add -q --detach "$tmp/base" "$base"
trap 'git worktree remove --force "$tmp/base" 2>/dev/null || true' EXIT
cp -R "$tmp/base/Sample" "$tmp/base-sample"
cp -R Sample "$tmp/head-sample"
# page.date drives a permalink in posts/post1.md and the "Content modified on" comment.
find "$tmp/base-sample" "$tmp/head-sample" -exec touch -t 202001010000 {} +
dotnet run -c Release --project "$tmp/base/Douze" -- -s "$tmp/base-sample" -o "$tmp/base-out"
dotnet run -c Release --project Douze -- -s "$tmp/head-sample" -o "$tmp/head-out" --plain
git worktree remove --force "$tmp/base"
diff -r -I 'Generated on' "$tmp/base-out" "$tmp/head-out" && echo "Sample output identical"
