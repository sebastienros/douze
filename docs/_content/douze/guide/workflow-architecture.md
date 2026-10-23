---
title: Workflow architecture
description: How Douze builds a site, with virtual files that flow through a graph of steps, and how that makes builds parallel and incremental.
liquid: false
---

Douze builds a site by running a workflow: a graph of steps that files flow through. Each source file becomes a
virtual file in memory, enters the steps that select it, changes on its way (front matter moved to its data, a new
path, HTML instead of Markdown), and reaches the `write` step, which writes it to the output folder. This page
explains the pieces of that model, since every feature of Douze, and every `douze.yml`, is expressed with them.

## Virtual files

A virtual file has a path, a content (text or bytes) and its own data. Source files start with their path in the
source folder (`/_content/blog/post.md`) and no data. Steps never change a file: they return a new one, or pass the
file on unchanged.

- The `frontmatter` step moves the YAML front matter of a page into its data, and sets `page.date`.
- The `permalink` step gives it its output path (`/blog/post/index.md`) and sets `url`.
- The `liquid` step renders its content as a Liquid template.
- The `markdown` step renders it to HTML and changes its extension to `.html`.
- The `layout` step wraps it in its layouts.
- The `write` step writes it to `_site/blog/post/index.html`.

A step can also drop a file (`permalink: false`), emit several (`paginate` emits one file per page, `fork` emits a
copy), or emit new files (`images` emits the variants of an image, a batch command emits what the command wrote).

## Steps and how files reach them

A workflow is a list of named steps. Each step either uses a built-in step (`uses: markdown`) or runs a command
(`run: ...`, see [command steps](command-steps.md)). Three fields connect steps:

| Field | Meaning |
|---|---|
| `inputs` | Globs of the source files that enter the step. A step with `inputs` reads from the source folder. |
| `needs` | The steps whose output files enter the step, and whose data it sees. |
| `match` | Globs of the entering files the step consumes. The others pass through it unchanged. |

```yaml title="douze.yml"
steps:
  pages:
    inputs: "_content/**"
    uses: frontmatter
  markdown:
    needs: pages
    uses: markdown
  write:
    needs: markdown
    uses: write
```

Every built-in step has a default `match`: `markdown` consumes `**/*.md`, `layout` consumes `**/*.html`, and so on
(the [step reference](../reference/steps/index.md) lists them). A stylesheet that enters `markdown` is not consumed:
it passes through and reaches `write` unchanged. That is how a file that no step claims is copied as it is.

A step runs as soon as files reach it, and its order in `douze.yml` does not matter. Two steps that do not need each
other run at the same time. When a step needs several steps, the files of all of them enter it. A path that arrives
from two steps must be the same file (a diamond, where both passed it through); two different files with one path
are an error:

```txt
error: write: /index.html: step 'write' received '/index.html' from both 'api' and 'render'
```

The files that come out of a step that no other step needs are dropped. Only what reaches `write` is written.

## Shapes

Each step has a shape, which says what one unit of its work is:

| Shape | Unit | Examples |
|---|---|---|
| map | one file | `frontmatter`, `permalink`, `liquid`, `markdown`, `layout`, `write`, `filter` and `worker` commands |
| group | a group of files | `liquid` with a `template` and `group-by`, a batch command with `group-by` |
| reduce | every file the step consumes | `data`, `collect`, `image-map`, the `hover-*` steps, batch commands |

A map step starts on the first file as soon as it arrives, and its files go on to the next step one by one: a page
can be written while others are still being parsed. `group-by` turns a step into a group step: `file` makes one group
per file, `dir` one per folder, and a Liquid template one per distinct value.

## Barriers

A reduce step waits for every file it consumes before it runs: it is a barrier. `collect` needs every page to build
`collections.all`, so nothing that needs it can start before the last page has reached it. In the default workflow,
`paginate` needs `collections`, so every page waits there; after it, pages stream again through `render`,
`markdown`, `layout` and `write`.

Barriers cost memory, since the pages that wait are held (as UTF-8). Keep the steps that every page needs before the
barrier, and the expensive ones after it, as the default workflow does. See
[Memory and parallelism](memory-and-parallelism.md).

## Scopes of data

Data is not copied into every file. A file carries its own data (front matter, `url`, `page.date`), and the rest lives
in scopes that the steps contribute:

| Scope | Contributed by | Seen as |
|---|---|---|
| Site data | `data` with `scope: site` | `colors` for `_data/colors.json` |
| Folder data | `data` with `scope: folder` | the keys of `_content/blog/blog.yml`, in `blog` and its subfolders |
| Collections | `collect` | `collections.all`, `collections.post`, `collections.urls` |
| Images | `image-map` | `images` |
| Settings | the `site` section of `douze.yml` | `site.url`, `site.locales` |

A name in a template resolves to the first of: the file's own data, its folder data from the nearest folder up, the
site data, the settings, then the front matter of its layouts. A step sees only the contributions of the steps it
needs, directly or not: a `liquid` step that does not need `collect` has no `collections`. This is why the default
workflow's `paginate` needs both `permalink` (for the files) and `collections` (for the data).

## The default workflow

Without `steps` in `douze.yml`, Douze runs this workflow, with the folders it detected. For a site with `_content`
and `_data`, it is the following (`douze --print-workflow` prints it with one line per step):

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

As a graph, with the barriers marked:

```txt
_data/**            ──► site-data ─────┐  (reduce)
_content/**/*.yml…  ──► folder-data ───┤  (reduce)
                                       ▼
_content/** (other files) ──────────► pages ──► permalink ──┬────────────────────► paginate ──► render ──► markdown ──► layout ──► write
                                                            └──► collections ─────┘
                                                                 (reduce, barrier)
```

`site-data` and `folder-data` contribute scopes and emit no files. `pages` waits for their data, then streams.
`collections` passes the pages through and contributes `collections`; `paginate` receives each page from both
`permalink` and `collections`, which is the same file, so it enters once.

## Incremental units and provenance

Every unit of work has a result key: the hashes of the files it received, the step's options, and Douze's version.
Each result also records what the unit read while it ran: the layouts, includes and templates, the data keys and
folder data it resolved, the collections. Each output file records its provenance: the step that produced it and the
files it came from.

On the next build, a unit whose key and recorded dependencies did not change is not run: its outputs are replayed
from the disk cache. A replayed file is the same file as before, with the same hashes, so the units after it are
replayed too, and a change stops propagating as soon as a step produces the same output as before. Editing the body
of a post runs that post again, and the pages that list it only when the data they read (its title, tags or date)
changed.

`incremental: false` on a step runs it on every build. See [Cache and incremental builds](cache.md).

## See also

- [Configuration overview](configuration.md)
- [Command steps](command-steps.md)
- [douze.yml reference](../reference/configuration.md)
- [Built-in steps](../reference/steps/index.md)
- [Template data](../reference/template-data.md)
