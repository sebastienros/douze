#!/bin/sh
# Installs douze on macOS and Linux from its NuGet packages, without the .NET SDK: the native build of the machine's
# platform (douze.<rid>), downloaded from the feed's flat container and extracted to <install dir>/<version>, with
# <install dir>/bin linking to the current version.
#
#   curl -fsSL https://github.com/sebastienros/douze/releases/latest/download/install.sh | sh
#   curl -fsSL https://github.com/sebastienros/douze/releases/latest/download/install.sh | sh -s -- --preview
#
# Run with --help for the options.

set -eu

NUGET_FEED="https://api.nuget.org/v3/index.json"
PREVIEW_FEED="https://f.feedz.io/sebastienros/douze/nuget/index.json"
PROFILE_MARKER="# Added by the douze installer"

usage() {
    cat <<'EOF'
Installs douze, or updates it in place.

Usage: install.sh [options]

Options:
      --preview            install the latest version, previews included (default: the latest stable)
      --version <version>  install this version
      --install-dir <dir>  where to install (default: $DOUZE_INSTALL_DIR, or ~/.douze)
      --no-modify-path     do not add <install dir>/bin to PATH in the shell profile
      --feed <url>         the NuGet feed (service index) to install from (default: nuget.org, and the preview feed
                           for previews or while nuget.org has no stable version)
      --uninstall          remove douze and what the installer added to the shell profile
  -h, --help               show this help
EOF
}

say() { printf '%s\n' "$*"; }
fail() { printf 'install.sh: %s\n' "$*" >&2; exit 1; }

preview=0
version=""
install_dir="${DOUZE_INSTALL_DIR:-}"
modify_path=1
feed=""
uninstall=0

while [ $# -gt 0 ]; do
    case "$1" in
        --preview) preview=1 ;;
        --version) [ $# -ge 2 ] || fail "--version needs a value"; version="$2"; shift ;;
        --version=*) version="${1#*=}" ;;
        --install-dir) [ $# -ge 2 ] || fail "--install-dir needs a value"; install_dir="$2"; shift ;;
        --install-dir=*) install_dir="${1#*=}" ;;
        --no-modify-path) modify_path=0 ;;
        --feed) [ $# -ge 2 ] || fail "--feed needs a value"; feed="$2"; shift ;;
        --feed=*) feed="${1#*=}" ;;
        --uninstall) uninstall=1 ;;
        -h|--help) usage; exit 0 ;;
        *) fail "unknown option '$1' (see --help)" ;;
    esac
    shift
done

[ -n "$install_dir" ] || install_dir="$HOME/.douze"
case "$install_dir" in
    /*) ;;
    *) install_dir="$(pwd)/$install_dir" ;;
esac
bin_dir="$install_dir/bin"

# --- downloads ---

if command -v curl >/dev/null 2>&1; then
    downloader=curl
elif command -v wget >/dev/null 2>&1; then
    downloader=wget
else
    downloader=""
fi

# fetch <url> <file>: fails on any HTTP error.
fetch() {
    case "$downloader" in
        curl) curl -fsSL --retry 3 --proto '=https,http' -o "$2" "$1" ;;
        wget) wget -q --tries=3 -O "$2" "$1" ;;
        *) fail "curl or wget is required" ;;
    esac
}

# --- versions ---

# Reads versions on stdin, prints the highest by NuGet's order: numbers, then a version without a prerelease label
# above one with it, labels compared part by part (numeric parts as numbers, below the others, which compare
# case-insensitively).
max_version() {
    LC_ALL=C awk '
        function num(x, y) { x += 0; y += 0; return x < y ? -1 : (x > y ? 1 : 0) }
        function isnum(s) { return s ~ /^[0-9]+$/ }
        function cmp(a, b,   i, c, ar, br, ap, bp, an, bn, n, m, x, y) {
            sub(/\+.*/, "", a); sub(/\+.*/, "", b)
            ar = a; ap = ""; i = index(a, "-"); if (i) { ar = substr(a, 1, i - 1); ap = substr(a, i + 1) }
            br = b; bp = ""; i = index(b, "-"); if (i) { br = substr(b, 1, i - 1); bp = substr(b, i + 1) }
            an = split(ar, A, "."); bn = split(br, B, ".")
            for (i = 1; i <= 4; i++) { c = num(i <= an ? A[i] : 0, i <= bn ? B[i] : 0); if (c) return c }
            if (ap == "" || bp == "") return ap == bp ? 0 : (ap == "" ? 1 : -1)
            n = split(ap, P, "."); m = split(bp, Q, ".")
            for (i = 1; i <= n && i <= m; i++) {
                if (isnum(P[i]) && isnum(Q[i])) c = num(P[i], Q[i])
                else if (isnum(P[i])) c = -1
                else if (isnum(Q[i])) c = 1
                else { x = tolower(P[i]); y = tolower(Q[i]); c = x < y ? -1 : (x > y ? 1 : 0) }
                if (c) return c
            }
            return n < m ? -1 : (n > m ? 1 : 0)
        }
        NF { if (best == "" || cmp($0, best) > 0) best = $0 }
        END { if (best != "") print best }'
}

# The package base address (flat container) of a feed's service index.
package_base() {
    fetch "$1" "$tmp/index.json" || fail "cannot read the feed $1"
    base="$(tr -d ' \t\r\n' < "$tmp/index.json" | tr '{' '\n' | tr '}' '\n' | grep '"@type":"PackageBaseAddress/3.0.0"' \
        | sed -n 's/.*"@id":"\([^"]*\)".*/\1/p' | head -n 1)"
    [ -n "$base" ] || fail "the feed $1 has no PackageBaseAddress/3.0.0 resource"
    case "$base" in */) ;; *) base="$base/" ;; esac
    printf '%s\n' "$base"
}

# The versions of a package in a flat container, one per line; none when the package is not there.
package_versions() {
    rm -f "$tmp/versions.json"
    fetch "$1$2/index.json" "$tmp/versions.json" 2>/dev/null || return 0
    tr -d ' \t\r\n' < "$tmp/versions.json" | sed -n 's/.*"versions":\[\([^]]*\)\].*/\1/p' | tr ',' '\n' | tr -d '"'
}

# Looks for the version to install in a feed: sets found_base and found_version.
find_version() {
    found_base="$(package_base "$1")" || return 1
    versions="$(package_versions "$found_base" "$package")"
    if [ -n "$version" ]; then
        wanted="$(printf '%s' "$version" | tr '[:upper:]' '[:lower:]')"
        found_version="$(printf '%s\n' "$versions" | grep -ix -F -- "$wanted" | head -n 1 || true)"
    elif [ "$2" = 1 ]; then
        found_version="$(printf '%s\n' "$versions" | max_version)"
    else
        found_version="$(printf '%s\n' "$versions" | grep -v -- '-' | max_version || true)"
    fi
    [ -n "$found_version" ]
}

# --- platform ---

detect_rid() {
    case "$(uname -s)" in
        Linux) os=linux ;;
        Darwin) os=osx ;;
        *) fail "unsupported operating system $(uname -s): douze ships for Linux, macOS and Windows (install.ps1)" ;;
    esac
    case "$(uname -m)" in
        x86_64|amd64) arch=x64 ;;
        aarch64|arm64) arch=arm64 ;;
        *) fail "unsupported architecture $(uname -m): douze ships for x64 and arm64" ;;
    esac
    # An x64 shell under Rosetta on Apple silicon: the arm64 build is the native one.
    if [ "$os" = osx ] && [ "$arch" = x64 ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || true)" = 1 ]; then
        arch=arm64
    fi
    if [ "$os" = linux ]; then
        if ls /lib/ld-musl-* >/dev/null 2>&1 || (ldd --version 2>&1 || true) | grep -qi musl; then
            fail "musl based Linux distributions (Alpine) are not supported: douze ships for glibc"
        fi
    fi
    printf '%s-%s\n' "$os" "$arch"
}

# --- shell profile ---

profile_file() {
    case "$(basename "${SHELL:-sh}")" in
        zsh) printf '%s\n' "${ZDOTDIR:-$HOME}/.zshrc" ;;
        bash)
            # Terminals on macOS start login shells, which read the first of these that exists.
            if [ "$(uname -s)" != Darwin ]; then
                printf '%s\n' "$HOME/.bashrc"
            elif [ ! -f "$HOME/.bash_profile" ] && [ ! -f "$HOME/.bash_login" ] && [ -f "$HOME/.profile" ]; then
                printf '%s\n' "$HOME/.profile"
            elif [ ! -f "$HOME/.bash_profile" ] && [ -f "$HOME/.bash_login" ]; then
                printf '%s\n' "$HOME/.bash_login"
            else
                printf '%s\n' "$HOME/.bash_profile"
            fi ;;
        fish) printf '%s\n' "${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d/douze.fish" ;;
        *) printf '%s\n' "$HOME/.profile" ;;
    esac
}

# The bin folder as a profile writes it: under $HOME when it is.
profile_path() {
    case "$bin_dir" in
        "$HOME"/*) printf '%s\n' "\$HOME${bin_dir#"$HOME"}" ;;
        *) printf '%s\n' "$bin_dir" ;;
    esac
}

add_to_path() {
    case ":${PATH:-}:" in
        *":$bin_dir:"*) return 0 ;;
    esac
    profile="$(profile_file)"
    if [ "$modify_path" = 0 ]; then
        say "Add $bin_dir to PATH to run douze, for instance in $profile."
        return 0
    fi
    if [ -f "$profile" ] && grep -qF "$PROFILE_MARKER" "$profile"; then
        say "$profile already adds $bin_dir to PATH: open a new shell to run douze."
        return 0
    fi
    mkdir -p "$(dirname "$profile")"
    case "$profile" in
        *.fish) line="fish_add_path -g \"$(profile_path)\"" ;;
        *) line="export PATH=\"$(profile_path):\$PATH\"" ;;
    esac
    printf '\n%s\n%s\n' "$PROFILE_MARKER" "$line" >> "$profile"
    say "Added to $profile:"
    say "  $line"
    say "Open a new shell to run douze, or run that line in this one."
}

remove_from_path() {
    for profile in "${ZDOTDIR:-$HOME}/.zshrc" "$HOME/.bashrc" "$HOME/.bash_profile" "$HOME/.bash_login" "$HOME/.profile" \
        "${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d/douze.fish"; do
        if [ ! -f "$profile" ] || ! grep -qF "$PROFILE_MARKER" "$profile"; then
            continue
        fi
        # The marker, the line after it, and the empty line the installer wrote before them.
        awk -v marker="$PROFILE_MARKER" '
            skip { skip = 0; next }
            $0 == marker { skip = 1; held = 0; next }
            held { print ""; held = 0 }
            $0 == "" { held = 1; next }
            { print }
            END { if (held) print "" }' "$profile" > "$profile.douze-tmp"
        # A profile the installer created (only its lines) goes away with them.
        if [ -s "$profile.douze-tmp" ]; then
            cat "$profile.douze-tmp" > "$profile"
        else
            rm -f "$profile"
        fi
        rm -f "$profile.douze-tmp"
        say "Removed douze from PATH in $profile."
    done
}

# --- install ---

# The version folders of the install folder (a folder with a douze executable), one per line.
version_folders() {
    for folder in "$install_dir"/*/; do
        folder="${folder%/}"
        [ -d "$folder" ] && [ ! -L "$folder" ] && [ -f "$folder/douze" ] && basename "$folder"
    done
    return 0
}

do_uninstall() {
    [ -d "$install_dir" ] || { say "douze is not installed in $install_dir."; remove_from_path; return 0; }
    [ -L "$bin_dir" ] && rm -f "$bin_dir"
    for name in $(version_folders); do
        rm -rf "${install_dir:?}/$name"
    done
    rmdir "$install_dir" 2>/dev/null || say "Kept $install_dir, which has other files."
    remove_from_path
    say "douze was uninstalled from $install_dir."
}

extract_zip() {
    if command -v unzip >/dev/null 2>&1; then
        unzip -tq "$1" >/dev/null || fail "the downloaded package is not a valid zip file"
        unzip -q -o "$1" -d "$2"
    elif command -v bsdtar >/dev/null 2>&1; then
        bsdtar -xf "$1" -C "$2" || fail "the downloaded package is not a valid zip file"
    elif command -v python3 >/dev/null 2>&1; then
        python3 -I -m zipfile -e "$1" "$2" || fail "the downloaded package is not a valid zip file"
    else
        fail "unzip, bsdtar or python3 is required to extract the package"
    fi
}

tmp="$(mktemp -d 2>/dev/null || mktemp -d -t douze)"
trap 'rm -rf "$tmp"' EXIT
trap 'exit 130' INT TERM

if [ "$uninstall" = 1 ]; then
    do_uninstall
    exit 0
fi

rid="$(detect_rid)"
package="douze.$rid"

if [ -n "$feed" ]; then
    find_version "$feed" "$preview" || fail "no ${version:+version $version of }${version:-version of }$package in $feed"
elif [ -n "$version" ]; then
    find_version "$NUGET_FEED" 1 || find_version "$PREVIEW_FEED" 1 \
        || fail "no version $version of $package on nuget.org or the preview feed"
elif [ "$preview" = 1 ]; then
    find_version "$PREVIEW_FEED" 1 || fail "no version of $package in the preview feed"
elif ! find_version "$NUGET_FEED" 0; then
    say "No stable version of douze on nuget.org yet: installing the latest preview, from $PREVIEW_FEED."
    find_version "$PREVIEW_FEED" 1 || fail "no version of $package in the preview feed"
fi

target="$install_dir/$found_version"
if [ -f "$target/douze" ] && "$target/douze" --version >/dev/null 2>&1; then
    say "douze $found_version ($rid) is already installed."
else
    say "Downloading douze $found_version ($rid)..."
    fetch "$found_base$package/$found_version/$package.$found_version.nupkg" "$tmp/package.nupkg" \
        || fail "cannot download $package $found_version"
    mkdir "$tmp/package"
    extract_zip "$tmp/package.nupkg" "$tmp/package"
    tool="$(find "$tmp/package/tools" -type f -name douze -path "*/$rid/douze" 2>/dev/null | head -n 1)"
    [ -n "$tool" ] || fail "the package has no tools/*/$rid/douze"
    tool="$(dirname "$tool")"
    rm -f "$tool/DotnetToolSettings.xml"
    chmod +x "$tool/douze"
    "$tool/douze" --version >/dev/null 2>&1 || fail "the downloaded douze does not run ($tool/douze --version failed)"

    mkdir -p "$install_dir"
    rm -rf "$target" "$target.tmp"
    cp -R "$tool" "$target.tmp"
    mv "$target.tmp" "$target"
fi

previous=""
if [ -L "$bin_dir" ]; then
    previous="$(basename "$(readlink "$bin_dir")")"
elif [ -e "$bin_dir" ]; then
    fail "$bin_dir exists and is not a link the installer made"
fi
if [ "$previous" != "$found_version" ]; then
    ln -sfn "$found_version" "$bin_dir"
    # Keep the previous version, remove the older ones.
    for name in $(version_folders); do
        if [ "$name" != "$found_version" ] && [ "$name" != "$previous" ]; then
            rm -rf "${install_dir:?}/$name"
        fi
    done
fi

say "douze $("$bin_dir/douze" --version) is installed in $target, linked from $bin_dir."
add_to_path
