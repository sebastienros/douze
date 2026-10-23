---
title: Environment variables
description: The environment variables Douze reads, the ones it passes to command steps, and how to build a site differently per environment.
liquid: false
---

Douze reads a few environment variables to choose how it prints progress and where it finds external programs. It
does not read environment variables in `douze.yml` or in templates: settings that change per environment go in
separate workflow files or on the command line. Command steps, on the other hand, get the environment of Douze plus
variables that describe the step and the file.

## Variables Douze reads

| Variable | Effect |
|---|---|
| `CI` | When set to anything but `false` or `0`, the live progress view is replaced by one line per step, as with `--plain`. |
| `TERM` | `TERM=dumb` also selects the plain view. |
| `NO_COLOR` | Keeps the live view, without colors. |
| `PATH` | Where the program a command step's `run` line starts with is looked up, before the build (`douze doctor` does the same). |
| `PATHEXT` | On Windows, the extensions tried for that program (default `.COM;.EXE;.BAT;.CMD`). |
| `ComSpec` | On Windows, the shell of command steps (default `cmd.exe`). |
| `DOTNET_HOST_PATH` | The `dotnet` executable of the `hover-csharp` step, when its `dotnet` option is not set; else the one on the `PATH`. |

The view is also plain when the output is redirected, and `--quiet` prints errors only whatever the variables say.

```sh
CI=true douze build        # one line per step
NO_COLOR=1 douze serve     # live view without colors
```

Steps that run external programs set some variables for them: `hover-go` runs `go` with `GOWORK=off` and without
`GOFLAGS`, so that the settings of your own module do not apply to the snippets; `hover-csharp` runs `dotnet` with
`DOTNET_CLI_TELEMETRY_OPTOUT`, `DOTNET_NOLOGO` and `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` set to `1`.

## Variables of command steps

A command step's process inherits the environment of Douze, and gets these variables on top:

| Variable | Modes | Value |
|---|---|---|
| `DOUZE_STEP` | all | The step name |
| `DOUZE_SITE` | all | The output folder (absolute) |
| `DOUZE_SOURCE` | all | The source folder (absolute) |
| `DOUZE_PATH` | `filter` | The file's virtual path (`/blog/a/index.html`) |
| `DOUZE_CONTENT_TYPE` | `filter` | The file's content type (`text/html`) |
| `DOUZE_METADATA` | `filter` | The file's own data as JSON |
| `DOUZE_METADATA_FILE` | `filter` | The path of a file holding that JSON, instead of `DOUZE_METADATA`, when it is over 32 KB |

A worker gets the step's options, the output folder and the source folder in its `hello` message instead. See
[Command steps and workers](../reference/commands.md).

### `env`

`env` adds variables to a command step. Its values are Liquid templates, rendered with the same values as the `run`
line (`{{ step }}`, `{{ site }}`, `{{ cwd }}`, `{{ inputs }}`, `{{ outputs }}`, `{{ file.path }}` in filter mode,
`{{ tool }}`), and they are never quoted:

```yaml title="douze.yml"
  minify:
    needs: layout
    run: "node scripts/minify.mjs"
    mode: filter
    match: "**/*.html"
    env:
      NODE_NO_WARNINGS: "1"
      MINIFY_REPORT: "{{ cwd }}/reports/{{ step }}.txt"
```

`env` is applied first, then the `DOUZE_*` variables, which are always set: a step cannot replace them.

With `shell: true` (the default), the `run` line goes through `/bin/sh -c` (`cmd.exe` on Windows), so it can read
variables itself:

```yaml
deploy-info:
  needs: write
  run: 'echo "built for $DEPLOY_TARGET" > "$DOUZE_SITE/build.txt"'
```

## Environment-specific values

Since `douze.yml` cannot read the environment, a site that builds differently per environment uses the command
line, or one workflow file per environment.

The command line covers the output folder, the parallelism, the cache and the view:

```sh
douze build -o dist -p 4 --cache-dir /tmp/douze-cache
```

For settings, such as a `site.url` per environment, keep one workflow file per environment and pick it with `-w`.
The file given with `-w` replaces `douze.yml`: it is not merged with it, so each file holds every section.

```yaml title="douze.yml"
site:
  url: http://localhost:8080
```

```yaml title="douze.production.yml"
site:
  url: https://www.example.com
```

```sh
douze serve                          # douze.yml: links point at localhost
douze build -w douze.production.yml  # absolute URLs point at the production site
```

In templates, build absolute URLs from `site.url` with the `absolute_url` filter, so they follow the file:

```liquid
<link rel="canonical" href="{{ url | replace: 'index.html', '' | absolute_url }}">
```

:::caution
Keep the extra workflow files outside the content folder. In a site without `_content`, the source folder is the
content folder, and a `douze.production.yml` there would be read as folder data and apply its keys to every page.
:::

A CI job then selects the file with a variable of its own:

```yaml title=".github/workflows/site.yml"
      - run: douze build -w "douze.${{ vars.SITE_ENV }}.yml"
```

## See also

- [Command steps](command-steps.md)
- [Configuration overview](configuration.md)
- [Develop and build](develop-and-build.md)
- [Command line reference](../reference/cli.md)
- [Command steps and workers](../reference/commands.md)
