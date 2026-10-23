---
title: Develop and build
description: Serve a site with live reload while you write it, build it for production, and run builds in CI.
---

Douze has three commands that build a site: `douze build` builds it once, `douze watch` builds it again after every
change, and `douze serve` also serves it with live reload. They take the same options, and all three use the disk
cache, so they only run what changed since the previous build.

## Serve while you write

```sh
douze serve
```

`douze serve` builds the site, serves the output folder on `http://localhost:8080/`, and watches the source folder:

```txt
Build succeeded: 10 steps, 6 files written in 0.03 s, 37 units executed
Serving _site at http://localhost:8080/
Watching . for changes (Ctrl+C to stop)
_content/blog/first.md changed: 1 file written in 0.01 s, 10 units executed, 27 cached
```

- `--port` picks another port. When the port is taken, `douze serve` stops with
  `cannot listen on http://localhost:8080/: ... (choose another port with --port)`.
- The server serves `index.html` for folder URLs (`/blog/a/`), redirects `/blog/a` to `/blog/a/`, and serves the
  site's `404.html`, when it has one, for missing files. Responses are sent with `Cache-Control: no-cache`.
- Ctrl+C stops the session.

### Live reload

The server adds a script to every HTML page it serves, before `</body>`. After each rebuild, it sends the pages the
URLs of the files the build wrote or deleted, and each page decides:

- it reloads when it is one of them, or when a script (`.js`, `.mjs`) changed;
- otherwise, it swaps in place the stylesheets that changed, without reloading, so the scroll position and the state
  of the page stay;
- it does nothing when neither applies: editing another page does not reload this one.

Pages also reload when they reconnect to a restarted server. The script is only added by the dev server: the files
in the output folder are not changed.

The pages connect to the server with a WebSocket, at `/__douze/socket`: any number of pages can stay open without
slowing down the server's other requests. A proxy in front of `douze serve`, such as the one of a dev container or a
tunnel, must pass WebSockets through for live reload to work.

### What a change rebuilds

A rebuild runs only what the change affects, like a build from the cache:

- Editing a page renders that page again, and the pages that list it when its title, tags or date changed.
- Editing an include or a layout renders the pages that use it.
- Editing a data file renders the pages that read what changed.
- Editing `douze.yml` (or the file of `--workflow`) takes effect at the next rebuild. While it is invalid, its errors
  are printed and the session waits for the next change.

Changes are collected until none came for 150 ms, so saving several files, or switching branches, is one rebuild. The
output folder, the `.douze` folder and `.git` are not watched. A build that fails prints its errors, and the session
goes on. When the system loses change events, or reports a path the watcher cannot map to the source folder, the
next rebuild checks every source file instead of failing.

The cache stays open during the session. Another `douze build` of the same site meanwhile builds without it, with a
warning.

### Skip slow steps while editing

`enabled` can be a Liquid expression that sees `watch`, true for `douze watch` and `douze serve`. A step that only
matters for the published site, such as a type check or a minifier, can stay out of the session:

```yaml title="douze.yml"
steps:
  minify:
    needs: layout
    run: "npx html-minifier-terser --collapse-whitespace"
    mode: filter
    match: "**/*.html"
    enabled: "watch == false"
```

The steps that need `minify` then need `layout` while you edit. See
[enabled](../reference/configuration.md#enabled).

## Watch without a server

```sh
douze watch
```

`douze watch` does what `douze serve` does, without the server: use it with another server, or when another tool
reads the output folder.

## Build

```sh
douze            # the same as douze build
douze build -s site -o public
```

`douze build` is the default command. It builds the site once and prints a summary. The main options:

| Option | Meaning |
|---|---|
| `-s`, `--source <dir>` | The source folder (default: the current folder) |
| `-o`, `--output <dir>` | The output folder, relative to the current folder (default: `_site`) |
| `-w`, `--workflow <file>` | Another workflow file than `douze.yml` |
| `-p`, `--parallelism <n>` | The maximum number of units that run at once (default: the processor count) |
| `--keep-going` | Report every error of a failing step instead of stopping it |
| `--fail-fast` | Stop the whole build on the first error |
| `--no-cache` | Build without reading or writing the cache |
| `--plain` | One line per step instead of the live view |
| `-q`, `--quiet` / `-v`, `--verbose` | Errors only / one line per file and stack traces |

The [command line reference](../reference/cli.md) lists every option.

### The output folder

The output folder is `_site` in the current folder by default. `-o` sets another one, relative to the current folder;
`site.output` in `douze.yml` sets one relative to the source folder, and `-o` wins over it.

Douze never empties the output folder. Once a build succeeds, it deletes the files that previous builds wrote and this
one did not: the pages of deleted or renamed sources, the pages a pagination no longer has, and the folders they
leave empty. It keeps:

- the files that something else changed since Douze wrote them;
- the files that Douze did not write, such as the ones a command wrote directly into the output folder;
- everything, when the build fails or runs with `--no-cache`, which has no record of the previous builds.

To start from an empty folder, delete it before the build.

### Errors and exit codes

Each error names the step, the file and, when known, the position:

```txt
error: render: /blog/first/index.md(3,1): '{% endif %}' was expected
```

By default, a failing step stops, the steps that need it are skipped, and the independent steps finish. `--keep-going`
lets the failing step process its other files and report every error; `--fail-fast` stops everything at once.

| Exit code | Meaning |
|---|---|
| `0` | The build succeeded |
| `1` | The build has errors, or a requirement of a step is missing (see `douze doctor`) |
| `2` | Invalid command line or workflow file |
| `130` | The build was cancelled with Ctrl+C |

`douze watch` and `douze serve` exit with `0` when they are stopped with Ctrl+C, and with `2` when the workflow is
invalid at start or the port is taken.

## Build in CI

In CI, Douze prints one line per step instead of the live view: it does when `CI` is set (to anything but `false` or
`0`), when the output is redirected, or with `--plain`. A GitHub Actions job:

```yaml title=".github/workflows/site.yml"
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Install douze
        run: |
          curl -fsSL https://raw.githubusercontent.com/sebastienros/douze/main/install.sh | sh -s -- --preview
          echo "$HOME/.douze/bin" >> "$GITHUB_PATH"
      - uses: actions/cache@v4
        with:
          path: .douze
          key: douze-${{ github.sha }}
          restore-keys: douze-
      - run: douze build
```

- Caching the `.douze` folder between runs makes CI builds incremental, and keeps the tools that steps download.
  Without it, every CI build is a full build, which is correct, only slower.
- `douze doctor` checks what the steps need (libvips, tools, commands) without building, and exits with `1` when
  something is missing.
- `douze tools install` downloads the tools of the workflow ahead of time, for a cached or offline build.

See [Deploy to GitHub Pages](../recipes/github-pages.md) and [Build your site with Docker](../recipes/docker.md).

## See also

- [Command line reference](../reference/cli.md)
- [Cache and incremental builds](cache.md)
- [Progress and tracing](tracing.md)
- [Troubleshooting](troubleshooting.md)
- [Environment variables](environment-variables.md)
