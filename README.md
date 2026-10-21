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
  data: products     # a name, or a path such as collections.post
  size: 2
  alias: items       # optional: the items of the page
  resolve: values    # optional, for objects: paginate the values instead of the keys
products: [a, b, c]
---
```

Each page has `pagination.items`, `pagination.pageNumber`, `pagination.hrefs` (the path of every page),
`pagination.pages` and `pagination.page.next`, `previous`, `first`, `last` (each one is `items`, `pageNumber` and
`href`). A `permalink` is rendered for each page, so it can use the alias or `pagination.pageNumber`; without one,
pages are written to `products/index.html`, `products/1/index.html`...

## layouts

`layout` names a file of the includes folder. The layout is rendered with the page's data and `content`, and can
itself have a `layout` in its front matter.
