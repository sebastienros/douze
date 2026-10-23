#!/bin/sh
# Validates the installation page and the tutorial in clean Linux containers. See README.md.
#
#   docs/tests/docker/validate.sh [options]
#
# Options:
#   --images "<image> ..."  the images of the install script run (default: ubuntu:24.04 debian:trixie-slim)
#   --platform <platform>   the platform of the containers, such as linux/amd64 (default: the machine's)
#   --douze <folder>        test the douze of a folder (a linux build of the sources: douze and its native libraries)
#                           instead of installing the latest preview; skips the .NET tool and unsupported checks
#   --no-dotnet             skip the .NET tool route
#   --no-unsupported        skip the check that an older glibc (debian:bookworm-slim) is refused as the page says
#
# Environment:
#   PAGES_DIR  the folder of the documentation pages, the one that holds guide/install.md and tutorial/
#              (default: the first of docs/_content/douze, docs/_content and docs that has them)

set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
DOCS="$(cd "$HERE/../.." && pwd)"

images="ubuntu:24.04 debian:trixie-slim"
platform=""
douze_dir=""
dotnet=1
unsupported=1

while [ $# -gt 0 ]; do
    case "$1" in
        --images) images="$2"; shift ;;
        --platform) platform="$2"; shift ;;
        --douze) douze_dir="$(cd "$2" && pwd)"; shift ;;
        --no-dotnet) dotnet=0 ;;
        --no-unsupported) unsupported=0 ;;
        -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
        *) echo "validate.sh: unknown option $1" >&2; exit 2 ;;
    esac
    shift
done

if [ -z "${PAGES_DIR:-}" ]; then
    for candidate in "$DOCS/_content/douze" "$DOCS/_content" "$DOCS"; do
        if [ -f "$candidate/guide/install.md" ] && [ -f "$candidate/tutorial/1-setup.md" ]; then
            PAGES_DIR="$candidate"
            break
        fi
    done
fi
[ -n "${PAGES_DIR:-}" ] || { echo "validate.sh: no guide/install.md and tutorial/ under $DOCS; set PAGES_DIR" >&2; exit 2; }
PAGES_DIR="$(cd "$PAGES_DIR" && pwd)"
echo "Pages: $PAGES_DIR"

platform_option=""
[ -n "$platform" ] && platform_option="--platform $platform"

results=""
status=0
record() {
    results="$results
  $1  $2"
    [ "$1" = pass ] || status=1
}

# run <image tag> <command...>: in a container, with the pages and the scripts read-only.
run() {
    tag="$1"; shift
    extra=""
    [ -n "$douze_dir" ] && extra="-v $douze_dir:/douze:ro -e DOUZE=/douze/douze"
    # shellcheck disable=SC2086
    docker run --rm $platform_option $extra -v "$PAGES_DIR:/pages:ro" -v "$HERE:/tests:ro" "$tag" "$@"
}

for image in $images; do
    tag="douze-docs-test:$(printf '%s' "$image" | tr ':/' '--')"
    echo
    echo "### $image: install script and tutorial"
    # shellcheck disable=SC2086
    if ! docker build -q $platform_option --build-arg BASE="$image" -t "$tag" -f "$HERE/Dockerfile" "$HERE" >/dev/null; then
        record fail "$image: docker build"
        continue
    fi
    log="$(mktemp)"
    run "$tag" sh /tests/tutorial.sh /pages 2>&1 | tee "$log"
    result="$(tail -n 50 "$log" | grep -E '^[0-9]+ passed, [0-9]+ failed' | tail -n 1)"
    if grep -qE '^[0-9]+ passed, 0 failed' "$log"; then
        record pass "$image: $result"
    else
        record fail "$image: ${result:-did not finish}"
    fi
    rm -f "$log"
done

if [ -z "$douze_dir" ] && [ $unsupported = 1 ]; then
    image="debian:bookworm-slim"
    tag="douze-docs-test:debian-bookworm-slim"
    echo
    echo "### $image: refused, as the installation page says (glibc 2.36)"
    # shellcheck disable=SC2086
    docker build -q $platform_option --build-arg BASE="$image" -t "$tag" -f "$HERE/Dockerfile" "$HERE" >/dev/null
    command="$(grep -m 1 '^curl -fsSL .*/install\.sh | sh -s -- --preview$' "$PAGES_DIR/guide/install.md")"
    out="$(run "$tag" sh -c "$command" 2>&1)"
    code=$?
    printf '%s\n' "$out"
    if [ $code -ne 0 ] && printf '%s' "$out" | grep -q 'the downloaded douze does not run'; then
        record pass "$image: refused with the message of the page"
    else
        record fail "$image: expected the installer to fail with 'the downloaded douze does not run'"
    fi
fi

if [ -z "$douze_dir" ] && [ $dotnet = 1 ]; then
    tag="douze-docs-test:dotnet"
    echo
    echo "### .NET tool route (mcr.microsoft.com/dotnet/sdk:10.0-noble)"
    # shellcheck disable=SC2086
    if docker build -q $platform_option -t "$tag" -f "$HERE/Dockerfile.dotnet" "$HERE" >/dev/null; then
        log="$(mktemp)"
        run "$tag" sh /tests/dotnet-tool.sh /pages 2>&1 | tee "$log"
        if grep -q '^ok    the .NET tool route' "$log"; then
            record pass ".NET tool: $(grep -E '^[0-9]+ passed, [0-9]+ failed' "$log" | tail -n 1)"
        else
            record fail ".NET tool: $(grep -E '^[0-9]+ passed, [0-9]+ failed|FAIL' "$log" | tail -n 1)"
        fi
        rm -f "$log"
    else
        record fail ".NET tool: docker build"
    fi
fi

echo
echo "Results:$results"
exit $status
