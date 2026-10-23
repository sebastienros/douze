---
title: Installation
description: Install douze on Windows, macOS or Linux, check that it runs, update it, and build a first site.
---

Douze is a single native executable for Windows, macOS and Linux, on x64 and arm64. It needs no runtime: no .NET, no
Node. This page installs it, checks that it runs, and builds a first site.

:::note[Previews only]
There is no stable release of Douze yet. Every push to the `main` branch publishes a preview, and the commands on this
page install the latest one.
:::

## Prerequisites

- **Windows** 10 or 11, x64 or arm64, with PowerShell (Windows PowerShell 5.1 or PowerShell 7).
- **macOS** on Apple silicon or Intel.
- **Linux** on x64 or arm64, with glibc 2.38 or later (Ubuntu 24.04, Debian 13, Fedora 39 or newer) and the ICU
  library (`libicu`). Distributions based on musl, such as Alpine, are not supported.
- For the shell script: `curl` or `wget`, and `unzip`, `bsdtar` or `python3` to extract the package.

Desktop distributions have all of them. A minimal container image may not, and the name of the ICU package depends on
the distribution:

```sh
# Ubuntu 24.04
apt-get update && apt-get install -y curl ca-certificates unzip libicu74
# Debian 13
apt-get update && apt-get install -y curl ca-certificates unzip libicu76
# Fedora
dnf install -y curl unzip libicu
```

- A text editor and a terminal. Douze runs from the command line.

:::tip[The downloaded douze does not run]
The install script checks that the downloaded `douze` starts, and stops with `the downloaded douze does not run` when
it does not. On Linux, the usual causes are a missing ICU library (install it as above), or a glibc older than 2.38,
as on Debian 12: use a newer distribution, or run Douze in a container image such as `ubuntu:24.04`. The .NET tool
installs the same native build, so it needs the same libraries.
:::

## Install with the install script

The install scripts download the native build of your platform, without .NET, into `~/.douze`
(`%LOCALAPPDATA%\douze` on Windows), and add its `bin` folder to your `PATH`.

{% tabs %}

{% tab "macOS and Linux" %}

```sh
curl -fsSL https://raw.githubusercontent.com/sebastienros/douze/main/install.sh | sh -s -- --preview
```

{% endtab %}

{% tab "Windows (PowerShell)" %}

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/sebastienros/douze/main/install.ps1))) -Preview
```

{% endtab %}

{% endtabs %}

The script prints the version it installed and where:

```txt
Downloading douze 1.0.0-preview.36 (linux-x64)...
douze 1.0.0-preview.36 is installed in /home/me/.douze/1.0.0-preview.36, linked from /home/me/.douze/bin.
Added to /home/me/.bashrc:
  export PATH="$HOME/.douze/bin:$PATH"
Open a new shell to run douze, or run that line in this one.
```

On macOS and Linux, `install.sh` adds the `PATH` line to the profile of your shell (`~/.zshrc`, `~/.bashrc`,
`~/.bash_profile`, `~/.profile`, or a `conf.d` file for fish). On Windows, `install.ps1` adds the folder to your user
`PATH`, which new terminals see. Open a new terminal before you run `douze`.

### Script options

Both scripts take the same options. The PowerShell names are in parentheses.

| Option | Meaning |
|---|---|
| `--preview` (`-Preview`) | Install the latest version, previews included. Without it, the scripts install the latest stable version, or the latest preview while there is none. |
| `--version <version>` (`-Version`) | Install this version, for instance `1.0.0-preview.36`. |
| `--install-dir <dir>` (`-InstallDir`) | Where to install. The default is `$DOUZE_INSTALL_DIR`, or `~/.douze` (`%LOCALAPPDATA%\douze` on Windows). |
| `--no-modify-path` (`-NoModifyPath`) | Do not change the `PATH`: the script prints the folder to add instead. |
| `--feed <url>` (`-Feed`) | The NuGet feed (service index) to install from. The default is nuget.org, and the preview feed for previews. |
| `--uninstall` (`-Uninstall`) | Remove douze and the `PATH` line the script added. |
| `-h`, `--help` | Show the options (`install.sh` only; use `Get-Help` for `install.ps1`). |

Options go after `sh -s --` when the script is piped from `curl`, for instance to pin a version:

```sh
curl -fsSL https://raw.githubusercontent.com/sebastienros/douze/main/install.sh | sh -s -- --version 1.0.0-preview.36
```

In PowerShell, pass them to the script block:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/sebastienros/douze/main/install.ps1))) -Version 1.0.0-preview.36
```

The installed files are a folder per version, `<install dir>/<version>`, with the `douze` executable and the native
libraries it loads (libvips for images, Oniguruma for syntax highlighting). `<install dir>/bin` is a link to the
current version.

## Install as a .NET tool

If you have the .NET SDK (10 or later), install Douze as a global tool from the preview feed:

```sh
dotnet tool install -g douze --prerelease --add-source https://f.feedz.io/sebastienros/douze/nuget/index.json
```

On Windows, macOS and Linux (x64 and arm64), the package of your platform is the same native build as the install
scripts install. On any other platform, .NET installs a portable package that runs on the .NET runtime.

The .NET tools folder (`~/.dotnet/tools`, `%USERPROFILE%\.dotnet\tools` on Windows) must be on your `PATH`; the .NET
SDK prints how to add it the first time you install a tool.

## Download an archive

Tagged releases also attach an archive per platform to their
[GitHub release](https://github.com/sebastienros/douze/releases): `douze-<version>-<rid>.tar.gz`, or `.zip` for
Windows, where `<rid>` is one of `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` and `win-arm64`. Check
the download against the release's `SHA256SUMS` file, extract it, and add its folder to your `PATH`:

```sh
tar -xzf douze-<version>-linux-x64.tar.gz
export PATH="$PWD/douze-<version>-linux-x64:$PATH"
```

Keep the files of the folder together: `douze` loads the native libraries next to it.

## Check the installation

Open a new terminal and run:

```sh
douze --version
```

It prints the version, such as `1.0.0-preview.36`. If the shell says `douze: command not found`, the `bin` folder is
not on your `PATH` yet: open a new terminal, or run the `export PATH=...` line that the script printed.

`douze --help` lists the commands and options; the [command line reference](../reference/cli.md) describes them.

### Image features and libvips

The image features (the `image` tag, the `images` and `social-image` steps) use [libvips](https://www.libvips.org).
It ships with Douze, next to the executable, in every native build and in the .NET tool packages: there is nothing
else to install. `douze doctor` checks what the steps of a site need, libvips included, without building it:

```sh
douze doctor
```

## Update Douze

Run the install command again. The script installs the latest version next to the current one, switches the `bin`
link to it, and keeps the previous version so that you can go back with `--version`. Older versions are removed.

For the .NET tool:

```sh
dotnet tool update -g douze --prerelease --add-source https://f.feedz.io/sebastienros/douze/nuget/index.json
```

## Uninstall Douze

{% tabs %}

{% tab "macOS and Linux" %}

```sh
curl -fsSL https://raw.githubusercontent.com/sebastienros/douze/main/install.sh | sh -s -- --uninstall
```

{% endtab %}

{% tab "Windows (PowerShell)" %}

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/sebastienros/douze/main/install.ps1))) -Uninstall
```

{% endtab %}

{% tab ".NET tool" %}

```sh
dotnet tool uninstall -g douze
```

{% endtab %}

{% endtabs %}

If you installed with `--install-dir`, pass the same folder to `--uninstall`. The script removes the folder and the
`PATH` line it added.

## Create your first site

A Douze site is a folder of pages. Create one with a single Markdown page:

```sh
mkdir hello-douze
cd hello-douze
mkdir _content
```

Save this as `_content/index.md`:

```md title="_content/index.md"
---
title: Hello
---
# Hello, Douze

This page was built by Douze.
```

Then start the development server:

```sh
douze serve
```

Douze builds the site into `_site`, serves it on `http://localhost:8080/`, and rebuilds it when a file changes. Open
that address in your browser, edit `index.md`, save it, and the page reloads by itself. Press Ctrl+C to stop the
server.

To build the site once, for publishing, run `douze` without a command. The output in `_site` is plain HTML that any
static host can serve.

## Next steps

- [Build a blog](../tutorial/index.md) step by step with the tutorial.
- See how a project is organized in [Project structure](project-structure.md).
- Learn the commands in [Develop and build](develop-and-build.md).
