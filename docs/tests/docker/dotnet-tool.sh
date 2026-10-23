#!/bin/sh
# The .NET tool route of the installation page: runs its "dotnet tool install" command, then the tutorial with the
# installed tool, then its update and uninstall commands.
#
#   dotnet-tool.sh <pages folder>

set -eu

PAGES="$(cd "${1:?usage: dotnet-tool.sh <pages folder>}" && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
PAGE="$PAGES/guide/install.md"

run_line() {
    line="$(grep -m 1 "$1" "$PAGE")" || { echo "FAIL  the installation page has no line matching $1"; exit 1; }
    echo "\$ $line"
    sh -c "$line"
}

dotnet --version
run_line '^dotnet tool install -g douze '
export PATH="$HOME/.dotnet/tools:$PATH"
tool="$(command -v douze)"
echo "douze: $tool ($(douze --version))"
# The package of the platform is the native build: no managed douze.dll next to it.
if find "$HOME/.dotnet/tools/.store/douze" -name 'douze.dll' | grep -q .; then
    echo "note: the installed package runs on the .NET runtime (no native package for this platform)"
else
    echo "ok    the installed package is the native build"
fi

DOUZE="$tool" sh "$HERE/tutorial.sh" "$PAGES"

run_line '^dotnet tool update -g douze '
run_line '^dotnet tool uninstall -g douze$'
if command -v douze >/dev/null 2>&1 && [ -e "$(command -v douze)" ]; then echo "FAIL  douze is still installed"; exit 1; fi
echo "ok    the .NET tool route: installed, ran the tutorial, updated and uninstalled"
