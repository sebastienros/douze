---
title: Configuration overview
description: What douze.yml configures, how to print the effective workflow, use another workflow file, and read validation errors.
liquid: false
---

A Douze site needs no configuration. When it needs some, it goes in `douze.yml`, at the root of the source folder.
The file has four sections, all optional: `site` for the folders and site settings, `vars` for the variables of the
site, `markdown` for how Markdown renders, and `steps` for the workflow. This page gives an overview of each; the
[douze.yml reference](../reference/configuration.md) lists every key.

## `site`

```yaml title="douze.yml"
site:
  url: https://example.com   # site.url in templates
  content: docs              # default: _content if it exists, else the source folder
  data: data                 # default: _data if it exists, else none
  includes: templates        # default: _includes if it exists, else the source folder
  shortcodes: templates/components   # default: shortcodes in the includes folder
  output: public             # relative to the source folder; -o wins
  parallelism: 8             # -p wins
```

- The folders are relative to the source folder. A folder set here is used whether or not it exists.
- `url` is the only setting of `site` that templates see, as `site.url`. It is what `absolute_url` prepends. A site data file
  `_data/site.json` can hold the other site values (`site.title`): its keys are layered over the settings, so
  `{{ site.title }}` comes from the data file and `{{ site.url }}` from `douze.yml`, unless the data file defines `url`
  too.
- `locales` configures the languages of a multilingual site. See [Internationalization](internationalization.md).

## `vars`

```yaml title="douze.yml"
vars:
  og: true
  label: Preview
```

`vars` declares the variables of the site with their default values. Templates read them as `vars.og`, and so do the
`enabled` expressions of the steps. The environment variable `DOUZE_VARS__og`, or `--var og=false` on the command
line, sets a variable for one build without editing the file:

```sh
douze --var og=false
```

Only declared variables can be set, and a value takes the type of the default: `false` is a boolean here, not a
string. See [vars](../reference/configuration.md#vars) and
[Environment variables](environment-variables.md#workflow-variables).

## `markdown`

```yaml title="douze.yml"
markdown:
  extensions: [pipetables, footnotes, tasklists, definitionlists, attributes, autolinks, alerts, customcontainers]
  links: true
  code:
    passthrough: [mermaid]
```

Without the section, Markdown is CommonMark with no extension. With it, `extensions` defaults to the list above. The
section also configures asides, links between `.md` files, code fences and the defaults of the image features. See
[Markdown](markdown.md).

## `steps`

Without `steps`, Douze runs the default workflow with the folders of `site`. With it, the steps of the file are the
whole workflow: the default steps are not added.

```yaml title="douze.yml"
steps:
  pages:
    inputs: "_content/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  collections:
    needs: permalink
    uses: collect
  render:
    needs: [permalink, collections]
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  headings:
    needs: markdown
    uses: headings
    with:
      anchors: true
  layout:
    needs: headings
    uses: layout
  write:
    needs: layout
    uses: write
```

Step names are lowercase letters, digits and dashes. A step has `uses` (a built-in step, with its options in `with`)
or `run` (a command, see [Command steps](command-steps.md)), and gets its files from `inputs` (globs of source files)
and `needs` (other steps). See [Workflow architecture](workflow-architecture.md) for how files flow.

The easiest way to write a workflow is to start from the default one:

```sh
douze --print-workflow > douze.yml
```

Then edit it: add a step between two others by changing the `needs` of the next one, or remove one by pointing its
dependents at what it needed. `enabled: false` leaves a step out without deleting it: the steps that need it then
need what it needs.

`enabled` can also be a Liquid expression, evaluated when the file is loaded, to run a step only in some builds:

```yaml title="douze.yml"
steps:
  og:
    needs: pages
    uses: social-image
    enabled: "vars.og and watch == false"
```

The expression sees `os`, `arch`, `env` (the environment variables), `watch` (true for `douze watch` and
`douze serve`) and `vars`. See [enabled](../reference/configuration.md#enabled).

## Print the effective workflow

`--print-workflow` prints the steps a build runs, the default ones included, and exits without building. A step that
is not enabled is left out, with the steps that needed it pointed at what it needed:

```sh
douze --print-workflow
```

For a site with `_content` and `_data`, the output is this workflow, with one line per step:

```yaml
steps:
  site-data:
    inputs: "_data/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: site
  folder-data:
    inputs: "_content/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: folder
      root: _content
  pages:
    inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"]
    needs: [site-data, folder-data]
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: _content
  collections:
    needs: permalink
    uses: collect
  paginate:
    needs: [permalink, collections]
    uses: paginate
  render:
    needs: paginate
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  layout:
    needs: markdown
    uses: layout
  write:
    needs: layout
    uses: write
```

The output is valid `steps` YAML: loading it gives the same workflow. Fields with their default value are left out.

## Use another workflow file

`-w` (`--workflow`) reads another file instead of `douze.yml`, relative to the current folder:

```sh
douze -w douze.production.yml
```

The file replaces `douze.yml`: the two are not merged, so it holds every section the build needs. Keep it outside
the content folder, since data files of the content folder are folder data (with `_content`, the root of the source
folder is fine). When builds differ by a few values, [variables](#vars) and `enabled` expressions are simpler than
a file per environment; see [Environment variables](environment-variables.md#environment-specific-values).

## Validation errors

Douze checks the whole file before it builds: unknown keys and fields, wrong types, invalid globs and durations,
unknown `needs` and `uses`, cycles, and the options of built-in steps. Every error is printed with its position, and
the build stops with exit code `2`:

```txt
douze.yml(3,3): error: unknown key 'titel' in 'site'; expected 'content', 'data', 'includes', 'shortcodes', 'output', 'url', 'parallelism' or 'locales'
douze.yml(6,43): error: unknown field 'if' on step 'render'
```

```txt
douze.yml(6,3): error: step 'markdown' needs unknown step 'markdwn'; did you mean 'markdown'?
```

Errors about the shape of the file point at the value they are about. Errors about how steps fit together (unknown
`needs` or `uses`, cycles, step options) point at the step's name. A workflow without a `write` step is valid, with a
warning: `no 'write' step: nothing will be written to the output folder`.

## See also

- [douze.yml reference](../reference/configuration.md)
- [Command line reference](../reference/cli.md)
- [Workflow architecture](workflow-architecture.md)
- [Built-in steps](../reference/steps/index.md)
- [Editor setup](editor-setup.md)
