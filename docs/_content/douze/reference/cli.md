---
title: Command line
description: Every command and option of the douze command line, with defaults, interactions and exit codes.
---

`douze` takes an optional command, then options in any order. Without a command it builds the site once.

```text
douze [build] [options]          build once (default command)
douze watch [options]            build, then rebuild on changes
douze serve [options]            watch + dev server with live reload
douze doctor [options]           check what the steps need (libvips, tools, commands) without building
douze tools install [options]    download the tools the steps need into the cache folder
douze worker-helper              print douze-worker.mjs to stdout
```

`douze --help` prints this text and the list of options, `douze --version` prints the version.

## Commands

| Command | What it does |
|---|---|
| `build` | Builds the site once. This is the default: `douze -s site` is `douze build -s site`. |
| `watch` | Builds the site, then builds again after every change of its sources, until Ctrl+C. |
| `serve` | Does what `watch` does and serves the output folder on `http://localhost:8080/` with live reload. |
| `doctor` | Loads the workflow and checks the requirements of every step (libvips, downloaded tools, the executables of command steps) without building. |
| `tools install` | Downloads every tool the steps of the workflow download on first use into the tools folder of the cache. |
| `worker-helper` | Prints `douze-worker.mjs`, the Node helper for [worker command steps](commands.md#the-worker-helper), to stdout. |

The command must be the first argument. `tools` needs its subcommand: `douze tools install`.

### build

```bash
douze                              # build the current folder into ./_site
douze build -s site -o public      # build ./site into ./public
douze -s site --print-workflow     # print the steps the build would run
```

A build loads the workflow (`douze.yml` or the file of `-w`), checks the requirements of its steps, runs it and,
once it succeeds, deletes the output files that previous builds wrote and this one did not. Errors are printed as
`error: <step>: <file>(<line>,<col>): <message>`:

```text
error: render: /a/index.md(1,1): boom
```

### watch and serve

```bash
douze watch -s site
douze serve -s site --port 3000
```

Both take the options of `build`. The first build uses the same progress view as `build`; each rebuild then prints
one line, such as `_content/index.md changed: 1 file written in 0.01 s, 8 units executed, 64 cached` (one line per
step with `--verbose`, errors only with `--quiet`).

- A rebuild runs only what the change affects, like a build from the cache.
- Changes are collected until none came for 150 ms, so a save of several files or a checkout is one rebuild.
- The output folder, the cache folder, `.douze` and `.git` are not watched. A workflow file given with `-w` outside
  the source folder is watched too.
- An edited workflow file takes effect at the next rebuild. While it is invalid, its errors are printed and the
  session waits for the next change. A failed build prints its errors and the session goes on.
- With `--trace` or `--timeline`, the files are written again after every build.
- An invalid workflow when the session starts exits with code 2. Ctrl+C ends the session with code 0.

`serve` listens on `http://localhost:<port>/` (GET and HEAD). It serves `index.html` for folder URLs, redirects
`/blog/a` to `/blog/a/`, serves the site's `/404.html` for missing files when there is one, and tells browsers not to
cache anything. It adds a live reload script before `</body>` in HTML pages: after each build a page reloads when it
changed or was deleted, or when a script changed; when only stylesheets changed, they are swapped without a reload.
When the port is taken, `serve` exits with code 2:

```text
error: cannot listen on http://localhost:8080/: <reason> (choose another port with --port)
```

See [Develop and build](../guide/develop-and-build.md).

### doctor

```bash
douze doctor -s site
```

Prints one row per requirement of the workflow's steps, then a summary:

```text
STEP      REQUIREMENT       STATUS    DETAILS
images    libvips           ok        8.18.7
twoslash  typescript 7.0.2  download  is not installed: it is downloaded for linux-x64 on first use, or now by 'douze tools install'
minify    esbuild           missing   is not on PATH: install esbuild and add its folder to PATH, or change the 'run' line of step 'minify'

1 requirement missing: builds fail until it is fixed
```

| Status | Meaning |
|---|---|
| `ok` | Found, with its version or path when known. |
| `download` | A tool Douze downloads on first use (or now, with `douze tools install`). |
| `missing` | Not found; the details say what to install or configure. |
| `unchecked` | Only known during the build, such as a `run` line that starts with a template or a variable. |

Without requirements it prints `No step of the workflow has requirements.` It runs nothing to check: the executable
of a command is looked up on `PATH` (and `PATHEXT` on Windows), or relative to the step's `cwd` when it is a path. It
does not open the cache, so it does not wait for a build that holds it. It exits with 1 when a requirement is missing.

### tools install

```bash
douze tools install -s site --cache-dir .cache/douze
```

Downloads every tool that a step downloads on first use (the engine of `hover-typescript`, the Roslyn language server
of `hover-csharp`, gopls of `hover-go`, the `tool` of command steps) into `<cache>/tools/`, and prints one line per
tool: `<tool>: installed at <path>` or `<tool>: already installed at <path>`. A CI cache of that folder, or a machine
that goes offline, then has them. Without such tools it prints `No step of the workflow downloads tools.` It exits
with 1 when a tool cannot be installed.

### worker-helper

```bash
douze worker-helper > scripts/douze-worker.mjs
```

Writes the helper to stdout. It reads no site and ignores the options.

## Options

| Option | Value | Default | Commands | What it does |
|---|---|---|---|---|
| `-s`, `--source` | folder | `.` | all but `worker-helper` | The source folder of the site. |
| `-o`, `--output` | folder | `site.output`, else `_site` | `build`, `watch`, `serve` | The output folder. |
| `-w`, `--workflow` | file | `<source>/douze.yml` when it exists | all but `worker-helper` | The workflow file. |
| `-p`, `--parallelism` | positive integer | `site.parallelism`, else the processor count | `build`, `watch`, `serve` | The maximum number of units that run at once. |
| `--keep-going` | | | `build`, `watch`, `serve` | A failing step processes its other units and reports every error. |
| `--fail-fast` | | | `build`, `watch`, `serve` | The whole build stops on the first error. |
| `--trace` | file | | `build`, `watch`, `serve` | Writes a Chrome trace event file. |
| `--timeline` | file | | `build`, `watch`, `serve` | Writes a self-contained `timeline.html`. |
| `--trace-units` | | | `build`, `watch`, `serve` | Adds one span per file or group to the trace and the timeline. |
| `--plain` | | | `build`, `watch`, `serve` | One line per step instead of the live view. |
| `-q`, `--quiet` | | | all but `worker-helper` | Prints errors only. |
| `-v`, `--verbose` | | | all but `worker-helper` | One line per file in the plain view, stack traces, the stdout of batch commands, and keeps their temporary folders. |
| `--print-workflow` | | | `build`, `watch`, `serve` | Prints the effective workflow as YAML and exits. |
| `--no-cache` | | | `build`, `watch`, `serve` | Neither reads nor writes the disk cache. |
| `--cache-dir` | folder | `<source>/.douze` | all but `worker-helper` | The cache folder. |
| `--port` | positive integer | `8080` | `serve` | The port of the dev server. |
| `-h`, `--help` | | | | Prints the usage and exits. |
| `--version` | | | | Prints the version and exits. |

Every command parses every option, so an option that a command does not use is accepted and ignored.

### Source, output and workflow

```bash
douze -s site                          # site/douze.yml if it exists, output ./_site
douze -s site -o site/_site            # output inside the source folder: it is not read as a source
douze -s site -w ci.yml                # another workflow file, relative to the current folder
```

- `-s` is relative to the current folder. A missing source folder is an error:
  `error: the source folder '<path>' does not exist` (exit code 2).
- `-o` is relative to the current folder, `site.output` in `douze.yml` to the source folder, and the default `_site`
  to the current folder. `-o` wins over `site.output`.
- `-w` is relative to the current folder. A missing file is an error:
  `error: the workflow file '<file>' does not exist` (exit code 2). Without `-w`, `douze.yml` at the root of the
  source folder is used when it exists, and the [default workflow](configuration.md#default-workflow) runs otherwise.
- The output folder and the cache folder are never read as sources when they are inside the source folder.

### Parallelism

```bash
douze -p 4
```

`-p` wins over `site.parallelism`, which wins over the processor count. A unit that runs holds its working set (a page,
an image), so a lower value builds slower and holds less memory. See
[Memory and parallelism](../guide/memory-and-parallelism.md).

### Failures

```bash
douze --keep-going      # report every error
douze --fail-fast       # stop at the first one
```

By default, a failing step stops, the steps that need it are skipped, and the steps that do not depend on it finish.
`--keep-going` lets the failing step process its other units, so that every error is reported. `--fail-fast` stops
the whole build on the first error. The two cannot be used together.

### Progress views

```bash
douze --plain
douze -q
douze -v --plain
```

In a terminal, a build shows a live table of the running steps, then a summary table. Douze prints one line per step
(the plain view) instead when any of these is true:

- `--plain` is given,
- the output is redirected,
- `CI` is set to anything but an empty string, `0` or `false` (any case),
- `TERM` is `dumb`.

`NO_COLOR` keeps the live view, without colors. `--quiet` prints errors only, whatever the view. `--verbose` adds stack
traces, and in the plain view one line per unit. `--quiet` and `--verbose` cannot be used together. See
[Progress and tracing](../guide/tracing.md).

### Trace and timeline

```bash
douze --trace build.json
douze --timeline build.html
douze --trace-units --timeline build.html
```

`--trace` writes [Chrome trace events](https://ui.perfetto.dev) for Perfetto or `chrome://tracing`, `--timeline` a
single HTML file with the timeline of the build that works offline. Both are written even when the build fails or is
cancelled, and their folders are created. Step and command spans are always recorded, unit spans only with
`--trace-units`. A file that cannot be written is an error and the exit code is 1.

### Cache

```bash
douze --no-cache
douze --cache-dir .cache/douze
```

The cache folder holds the cache of results (`cache/`), the temporary folders of batch commands (`tmp/`) and the
downloaded tools (`tools/`). `--no-cache` builds without reading or writing results, and without the record of previous
builds, so stale output files are not deleted. See [Cache and incremental builds](../guide/cache.md).

### Print the workflow

```bash
douze --print-workflow -s site
```

Prints the steps a build runs, the default ones included, as `douze.yml` YAML, and exits without building. With
`watch` or `serve` it does the same.

## Validation errors

Usage errors print the message and `Run 'douze --help' for usage.`, and exit with code 2:

| Message | Cause |
|---|---|
| `unknown command '<name>'` | The first argument is not a command or an option. |
| `'tools' needs a subcommand: 'tools install'` | `douze tools` without `install`. |
| `unknown tools subcommand '<name>'` | `douze tools <name>`. |
| `unknown option '<option>'` | An option that does not exist. |
| `option '<option>' needs a value` | The option is last, or followed by another option. A value can't start with `-` (except `-` alone). |
| `option '<option>' needs a positive number, not '<value>'` | `-p` or `--port` with something else than an integer of 1 or more. |
| `'--keep-going' and '--fail-fast' cannot be used together` | |
| `'--quiet' and '--verbose' cannot be used together` | |

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. Also the end of a `watch` or `serve` session with Ctrl+C. |
| `1` | The build has errors, a requirement is missing (`doctor`, or before a build), a tool cannot be installed (`tools install`), or a trace file cannot be written. |
| `2` | Invalid command line, missing source folder or workflow file, invalid `douze.yml` or shortcode, or a dev server port that is taken. |
| `130` | The build was cancelled with Ctrl+C or SIGTERM. A second Ctrl+C ends the process. |

## Environment variables

| Variable | Effect |
|---|---|
| `CI` | Set to anything but `0` or `false`: the plain view. |
| `TERM` | `dumb`: the plain view. |
| `NO_COLOR` | The live view without colors. |
| `PATH`, `PATHEXT` | Where the executables of command steps are looked up. |
| `DOTNET_HOST_PATH` | The `dotnet` of the `hover-csharp` step, when its `dotnet` option is not set. |

Command steps get `DOUZE_STEP`, `DOUZE_SITE` and `DOUZE_SOURCE`, see [Command steps](commands.md#environment).
