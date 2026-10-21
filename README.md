# douze
Stupidly simple, blazing fast static site generator

# installation

```
dotnet tool install -g douze --prerelease
```

# usage

```
douze [build] [options]          build once (default command)

Options:
  -s, --source <dir>             source folder (default: .)
  -o, --output <dir>             output folder (default: _site)
  -p, --parallelism <n>          max parallel units (default: processor count)
      --keep-going               report every error instead of stopping the failing step
      --fail-fast                stop everything on the first error
      --plain                    one line per step, no live view
  -q, --quiet                    errors only
  -v, --verbose                  one line per file, stack traces
      --print-workflow           print the effective workflow as YAML and exit
  -h, --help                     show the help
      --version                  show the version
```

`douze --help` lists the options of features that are not available yet (workflow files, watch mode, dev server,
trace files, cache).

The exit code is 0 on success, 1 when the build has errors (each one is printed as
`error: <step>: <file>(<line>,<col>): <message>`), 2 for an invalid command line or workflow, and 130 when the
build is cancelled with Ctrl+C.

# site layout

| Folder | Default | Contains |
|---|---|---|
| content | `_content` if it exists, else the source folder | pages, data files that apply to a folder, and any other file to publish |
| data | `_data` if it exists, else none | site data |
| includes | `_includes` if it exists, else the source folder | layouts, includes and templates |

The output folder is skipped when it is inside the source folder.

# how a site is built

Douze runs a workflow: a graph of steps that files flow through. `douze --print-workflow` prints it. The default
workflow:

1. Reads the data files (`.yml`, `.yaml`, `.json`, `.xml`) of the data folder as site data, and the ones of the
   content folder as folder data.
2. Moves the YAML front matter of `.md`, `.html` and `.liquid` files into their data, and sets `page.date` to the
   file's modification time.
3. Renders Markdown to HTML (`post.md` becomes `post.html`).
4. Expands paginated pages.
5. Computes output paths: `blog/post.html` is written to `blog/post/index.html`, `index.html` stays in place,
   `permalink` overrides the path, and `permalink: false` skips the file. The content folder is removed from the
   path, and `url` holds the result (`/blog/post/index.html`).
6. Builds `collections.all` and `collections.<tag>` from the `tags` of every page.
7. Renders every HTML and JSON file as a Liquid template.
8. Applies layouts.
9. Writes every file to the output folder. Files that no step claims (images, stylesheets, scripts) are copied as
   they are.

## data

Each file of the data folder is a site value named after the file: `_data/colors.json` is `colors`, whatever it
contains (a list, an object or a single value), and `_data/pkgs/Aspire.Hosting.json` is `pkgs["Aspire.Hosting"]`.
Two files cannot define the same name.

The top-level keys of the data files of a content folder (`articles/articles.yml`) apply to the pages of that folder
and its subfolders.

A name in a template resolves to the first of: the page's own data (front matter, `page.date`, `url`, captured
variables), the data of its folder, then of the parent folders, the site data, and the front matter of its layouts.
For instance a page can set `layout`, or inherit it from its folder or from `_data/layout.yml`.

## pagination

```yaml
---
pagination:
  data: products     # a name, or a dotted path into one such as site.products
  size: 2
  alias: items       # optional: the items of the page
  resolve: values    # optional, for objects: paginate the values instead of the keys
products: [a, b, c]
---
```

Each page has `pagination.items`, `pagination.pageNumber`, `pagination.hrefs` (the path of every page),
`pagination.pages` and `pagination.page.next`, `previous`, `first`, `last` (each one is `items`, `pageNumber` and
`href`). A `permalink` is rendered for each page, so it can use the alias or `pagination.pageNumber` (not the
`href` values, which come from it); without one, pages are written to `products/index.html`,
`products/1/index.html`... Pagination runs before collections are built, so `collections.<tag>` cannot be paginated
by the default workflow.

## layouts

`layout` names a file of the includes folder. The layout is rendered with the page's data and `content`, and can
itself have a `layout` in its front matter.

# workflow

`douze.yml` at the root of the source folder, or the file given with `--workflow`, configures a site. Both of its
sections are optional, and a site without the file builds with the default workflow.

```yaml
site:
  content: docs          # default: _content if it exists, else the source folder
  data: data             # default: _data if it exists, else none
  includes: templates    # default: _includes if it exists, else the source folder
  output: public         # relative to the source folder; -o wins
  url: https://example.com
  parallelism: 8         # -p wins

steps:
  site-data: { inputs: "data/*.json", uses: data, with: { scope: site } }
  pages:     { inputs: "docs/**", needs: site-data, uses: frontmatter }
  markdown:  { needs: pages, uses: markdown }
  permalink: { needs: markdown, uses: permalink, with: { root: docs } }
  layout:    { needs: permalink, uses: layout }

  packages:  { inputs: "data/packages/*.json", uses: parse, with: { as: package } }
  package-pages:
    needs: [packages, site-data]
    uses: liquid
    group-by: file
    with: { template: package.liquid, permalink: "packages/{{ group.files[0].data.package.name }}/" }

  write:     { needs: [layout, package-pages], uses: write }
```

Paths are relative to the source folder. Without `steps`, the default workflow runs with the folders of `site`.
`douze --print-workflow` prints the steps that a build runs, the default ones included, in the same format.

## steps

`steps` maps step names (lowercase letters, digits and dashes) to steps. Source files enter the steps whose
`inputs` match them; the files that come out of a step enter the steps that `need` it. A step only consumes the
entering files that its `match` selects, and the others pass through it unchanged.

| Field | Default | Meaning |
|---|---|---|
| `needs` | none | a step name or a list: the steps whose files, and data, enter this step |
| `inputs` | none | a glob or a list: the source files that enter this step |
| `match` | the step's own | a glob or a list: the entering files the step consumes |
| `uses` | | a built-in step: `data`, `frontmatter`, `parse`, `markdown`, `paginate`, `permalink`, `collect`, `liquid`, `layout`, `write` |
| `with` | none | the options of the built-in step |
| `group-by` | none | `file`, `dir`, or a Liquid template: processes the files in groups (`liquid` with a `template`) |
| `incremental` | `true` | `false` runs the step on every build |
| `run`, `mode`, `outputs`, `sources`, `version`, `env`, `cwd`, `timeout`, `stderr`, `shell` | | command steps, see "commands" |

A step has either `uses` or `run`. Steps run as soon as their files are available, in parallel; their order in the
file does not matter.

Globs: `*` matches within a folder, `**` across folders, `?` one character, `{a,b}` alternatives, `[a-z]` and
`[!a-z]` character classes, and `\` escapes. A leading `!` excludes: in a list, the last matching glob decides,
so `["docs/**", "!**/*.yml"]` is every file of `docs` but the YAML files.

Durations (`timeout`) are a number and a unit: `500ms`, `30s`, `5m`, `1h`.

## errors

Every error of the file is reported before the build stops with exit code 2, with the position of the value it is
about, or of the step's name for the errors about how steps fit together (unknown `needs` or `uses`, cycles,
options of built-in steps):

```
douze.yml(12,5): error: unknown field 'if' on step 'render'
douze.yml(14,3): error: step 'render' needs unknown step 'markdwn'; did you mean 'markdown'?
```

# commands

Not available yet: `run:` steps call external tools (filter, batch and worker modes).

# progress and tracing

Not available yet: a live console view, `--trace <file.json>` (Chrome trace events) and `--timeline <file.html>`.
