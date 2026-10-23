---
title: Troubleshooting
description: Check what a workflow needs with douze doctor, read Douze's errors, and fix the errors that come up most.
liquid: false
---

This page covers the tools that tell you what is wrong (`douze doctor`, the error format, verbose output, exit codes),
then the errors that come up most, with their fixes.

## Check the requirements

Some steps need something outside Douze: libvips for the image steps, the .NET SDK for `hover-csharp`, Go for
`hover-go`, the program a command step runs. Every build checks them before any unit runs, and fails at once when one
is missing. `douze doctor` checks them all without building:

```sh
douze doctor
```

```
STEP        REQUIREMENT       STATUS    DETAILS
images      libvips           ok        8.18.7
typescript  typescript 7.0.2  download  is not installed: it is downloaded for osx-arm64 on first use, or now by 'douze tools install'
minify      esbuild           missing   is not on PATH: install esbuild and add its folder to PATH, or change the 'run' line of step 'minify'

1 requirement missing: builds fail until it is fixed
```

A requirement is `ok`, `download` (a tool Douze downloads on first use), `missing`, or `unchecked` when it is only known
during the build (a `run` line that starts with a template). Douze runs nothing to check: a command's program is
looked up on the `PATH`, or relative to the step's `cwd` when it is a path, and its version is not checked. `doctor`
exits with 1 when a requirement is missing, so a CI job can run it first, and with 2 when `douze.yml` is invalid. It takes `-s`, `-w` and `--cache-dir`.

`douze tools install` downloads the tools that steps would download on first use (the TypeScript engine, the Roslyn
language server, gopls, the `tool` of command steps) into `<cache>/tools/`, for an offline machine or a CI cache:

```sh
douze tools install --cache-dir .cache/douze
```

## Read an error

A build error names the step, the file and, when it is known, the line and column:

```
error: render: /docs/a.md(12,3): Unknown tag 'tabss'; did you mean 'tabs'?
```

The path is the file's path in the site at that step: after the `permalink` step, `docs/a.md` is
`/docs/a/index.md`. An error in a shortcode names the shortcode and the page. Errors of `douze.yml` name its line and
column, and stop the build before it starts, with exit code 2:

```
douze.yml(3,3): error: step 'render' needs unknown step 'pagse'; did you mean 'pages'?
douze.yml(4,3): error: unknown built-in step 'writ'; did you mean 'write'?
```

By default, a failing step stops, the steps that need it are skipped, and the other steps finish, so you see every
error of the build. `--keep-going` makes the failing step process its other files too. `--fail-fast` stops everything
on the first error.

`-v` (`--verbose`) adds stack traces, a line per unit in the plain view, and the output of batch commands, whose working
folders under `.douze/tmp/` it keeps. `--print-workflow` prints the workflow the build runs, the default steps
included, to check what a `douze.yml` really does.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | the build succeeded |
| 1 | the build has errors, or a requirement is missing |
| 2 | invalid command line or workflow file |
| 130 | the build was cancelled with Ctrl+C |

## Common errors

### Unknown step or field

```
douze.yml(3,41): error: unknown field 'if' on step 'render'
douze.yml(4,3): error: unknown built-in step 'writ'; did you mean 'write'?
```

`uses` takes the name of a built-in step (see the [list of steps](../reference/steps/index.md)), and a step takes only
the fields of the [configuration reference](../reference/configuration.md). Option names under `with` are checked the
same way.

### Unknown tool

```
douze.yml(3,63): error: unknown tool 'pagefnd' on step 'search'; Douze knows 'lightningcss' or 'pagefind'
```

`tool` takes the name of a tool Douze downloads, optionally with a version (`pagefind@1.5.2`). Any other program goes
in the `run` line, without `tool`. See [Command steps](../reference/commands.md#tool).

### Unknown variable

```
error: unknown variable 'og' in environment variable 'DOUZE_VARS__og'; declare it in 'vars'
```

A `DOUZE_VARS__<name>` environment variable, `--var` or `--env` sets a variable that the `vars` section of
`douze.yml` does not declare. Declare it, or unset the environment variable: one left in the shell applies to every
site you build there. See [Environment variables](environment-variables.md#workflow-variables).

### A command is not on the PATH

```
error: minify: esbuild is not on PATH: install esbuild and add its folder to PATH, or change the 'run' line of step 'minify'
```

Install the program, or start the `run` line with its path. On Windows, run `npx` and other `.cmd` scripts through
the shell (the default, `shell: true`). A tool that Douze can download, such as Pagefind, can use `tool:` instead and
needs nothing installed (see [Command steps](command-steps.md)).

### A fork permalink has an empty file name

```
error: twins: /docs/a.md: the permalink of step 'twins' is '/docs/.md' for '/docs/a.md': it has an empty file name, a value of the template is probably undefined
```

The `permalink` of a [fork step](../reference/steps/fork.md) reads a value that the file does not define. Fix the
name, or set `strict: true` on the step to get `undefined variable '<name>'` instead.

### Liquid in Markdown text

```
error: render: /docs/a/index.md(1,26): Invalid 'if' tag
```

A Markdown page is a Liquid template, and only its fenced code blocks are protected. Liquid syntax in inline code or
in text runs. Wrap it in `{% raw %}...{% endraw %}`, or set `liquid: false` in the page's front matter, or in its
folder's data file for a whole folder. See [Markdown](markdown.md#liquid-before-markdown).

### An undefined variable shows nothing

An undefined variable renders as nothing, and so does an unknown filter: `{{ "x" | nosuchfilter }}` renders `x`. To
catch typos in variable names, set `strict: true` on the `liquid` step:

```yaml
render:
  needs: paginate
  uses: liquid
  with:
    strict: true
```

```
error: render: /index.md: undefined variable 'nme'
```

`strict` does not catch unknown filters. Check filter names against the [Liquid reference](../reference/liquid.md).

### Invalid front matter

```
error: pages: /index.md: (Lin: 1, Col: 0, Chr: 12) - (Lin: 1, Col: 0, Chr: 12): While parsing a flow sequence, did not find expected ',' or ']'.
```

The YAML of the front matter does not parse; the position is inside the front matter. Quote values that start with
`[`, `{`, `*` or `&`, or that contain `:`.

### Two files have the same output

```
error: write: /index.html: output '/index.html' is produced by both '/_content/index.html' and '/_content/index.md'
```

Two sources resolve to the same path: `index.md` and `index.html` in one folder, or two `permalink`s that collide.
Rename one, or set its `permalink`.

### A JSON or YAML file is not published

In the default workflow, every `.json`, `.yml`, `.yaml` and `.xml` file of the content folder is folder data, read and
not copied. Name a published JSON file `name.json.liquid`, or put it in a folder that the default `inputs` do not
read. See [Data files](data.md#folder-data).

### A code sample fails the build

A fence that a hover step checks has a compile error. Fix the sample, or list the expected error
(`// @errors: 2322` in TypeScript), or set `errors: warn` on the step. See [Code hovers](code-hovers.md#errors).

### The cache is used by another build

```
warning: the cache '.../.douze/cache' is used by another build: building without it
```

A `douze serve` or `douze watch` session holds the cache. The build works, without it. Stop the session, or pass
another `--cache-dir`.

### Fontconfig error on macOS

```
Fontconfig error: Cannot load default config file: File not found
```

Pango prints this when a `social-image` step runs on a machine without a Fontconfig configuration. The build still
succeeds; set `fontfile` on each text block so that the fonts do not depend on the system.

## Still stuck

Build again with `-v --plain --no-cache` and look at the first error. If you found a bug in Douze, open an issue at
[github.com/sebastienros/douze](https://github.com/sebastienros/douze/issues) with the command, the output and a
small site that shows it.

## See also

- [Command line](../reference/cli.md)
- [Configuration reference](../reference/configuration.md)
- [Progress and tracing](tracing.md)
- [Cache and incremental builds](cache.md)
- [Installation](install.md)
