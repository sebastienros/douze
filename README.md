# douze

[![Build](https://github.com/sebastienros/douze/actions/workflows/build.yml/badge.svg)](https://github.com/sebastienros/douze/actions/workflows/build.yml)
[![feedz.io](https://img.shields.io/endpoint?url=https%3A%2F%2Ff.feedz.io%2Fsebastienros%2Fdouze%2Fshield%2FDouze%2Flatest)](https://feedz.io/org/sebastienros/repository/douze/packages/Douze)

Stupidly simple, blazing fast static site generator

# installation

Douze is a native executable for Windows, Linux and macOS (x64 and arm64), with libvips for the image features (see
"images"). There is no stable release yet: every push to `main` publishes a preview.

As a .NET tool, from the preview feed (the package of the machine's platform is the native build; elsewhere it runs on
the .NET 11 runtime):

```
dotnet tool install -g douze --prerelease --add-source https://f.feedz.io/sebastienros/douze/nuget/index.json
dotnet tool update -g douze --prerelease --add-source https://f.feedz.io/sebastienros/douze/nuget/index.json
```

Without .NET, with the install scripts, which download the native build into `~/.douze` (`%LOCALAPPDATA%\douze` on
Windows) and add it to the `PATH`; running them again updates douze:

```
curl -fsSL https://raw.githubusercontent.com/sebastienros/douze/main/install.sh | sh -s -- --preview
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/sebastienros/douze/main/install.ps1))) -Preview
```

They take `--preview` (`-Preview`), `--version <version>`, `--install-dir <dir>`, `--no-modify-path`, `--feed <url>`
and `--uninstall`. Without `--preview` they install the latest stable version (the latest preview while there is
none), so `irm <url of install.ps1> | iex` works too. Each release also carries the scripts of its version, and once a
stable release exists they are at `https://github.com/sebastienros/douze/releases/latest/download/install.sh` (and
`install.ps1`): GitHub's latest release skips previews.

Or download the archive of your platform from the [releases](https://github.com/sebastienros/douze/releases) page
(`douze-<version>-<rid>.tar.gz`, `.zip` for Windows, checked by `SHA256SUMS`) and put its folder on the `PATH`.

# usage

```
douze [build] [options]          build once (default command)
douze watch [options]            build, then rebuild on changes
douze serve [options]            watch + dev server with live reload
douze doctor [options]           check what the steps need (libvips, tools, commands) without building
douze tools install [options]    download the tools the steps need into the cache folder
douze worker-helper              print douze-worker.mjs to stdout

Options:
  -s, --source <dir>             source folder (default: .)
  -o, --output <dir>             output folder (default: _site)
  -w, --workflow <file>          workflow file (default: <source>/douze.yml when it exists)
  -p, --parallelism <n>          max parallel units (default: processor count)
      --keep-going               report every error instead of stopping the failing step
      --fail-fast                stop everything on the first error
      --trace <file>             write a Chrome trace event file
      --timeline <file>          write a self-contained timeline.html
      --trace-units              include one span per file or group in the trace
      --plain                    one line per step, no live view
  -q, --quiet                    errors only
  -v, --verbose                  one line per file, stack traces
      --print-workflow           print the effective workflow as YAML and exit
      --no-cache                 ignore and do not write the disk cache
      --cache-dir <dir>          cache location (default: <source>/.douze)
      --port <n>                 dev server port (serve, default 8080)
  -h, --help                     show the help
      --version                  show the version
```

The exit code is 0 on success, 1 when the build has errors (each one is printed as
`error: <step>: <file>(<line>,<col>): <message>`) or a requirement is missing (see "doctor"), 2 for an invalid command line or workflow, and 130 when the
build is cancelled with Ctrl+C.

# cache

A build stores the result of each unit of work (a file in a step, a group, a whole step for collections and data) in
`<source>/.douze/cache/`, and the next build reuses every result whose inputs and dependencies did not change: a second
build without changes runs nothing (`0 units executed`), and after an edit only the units that the edit affects run.
A result depends on the files the unit received, the step's options, the layouts, includes and templates it read, the
data it resolved (folder data, site data, collections, `site:` settings) and, for a command, its `version` and the files
its `sources` match. The `write` step writes a file again when it is missing from the output folder or was changed
there. `--no-cache` builds without reading or writing the cache, `--cache-dir` moves it. Add `.douze/` to your
`.gitignore`.

Once a build succeeds, it deletes the output files that the previous builds wrote and it did not: the pages of deleted
or renamed sources, the pages a pagination no longer has, and the folders they leave empty. A file that something else
changed since Douze wrote it stays, and so does everything when the build fails or runs with `--no-cache` (no record
of the previous builds).

# watch and serve

`douze watch` builds the site, then again after every change of its sources, until Ctrl+C. `douze serve` does the same
and serves the output folder on `http://localhost:8080/` (`--port`), with live reload. Both take the options of
`douze build`.

```
douze serve -s Sample
```

- A rebuild runs only what the change affects, like a build from the cache: editing a page renders that page again
  (and the pages that list it, when its title, tags or date changed), editing an include renders the pages that use
  it, editing a data file the pages that read what changed. Each rebuild prints one line:
  `_content/index.md changed: 1 file written in 0.01 s, 8 units executed, 64 cached`.
- Changes are collected until none came for 150 ms, so saving several files, or a checkout, is one rebuild. The output
  folder, the `.douze` folder and `.git` are not watched.
- An edited workflow file (`douze.yml`, or the file of `--workflow`) takes effect at the next rebuild; while it is
  invalid, its errors are printed and the session waits for the next change. A build that fails prints its errors
  and the session goes on.
- The cache stays open during the session and its index is written when the session ends: another `douze build` of
  the site meanwhile builds without it (with a warning).
- The server serves `index.html` for folder URLs (`/blog/a/`), redirects `/blog/a` to `/blog/a/`, and serves the
  site's `404.html`, if it has one, for missing files. Browsers do not cache what it serves.
- Live reload: the server adds a script to every HTML page before `</body>`. After each build, a page reloads when it
  changed (or was deleted), or when a script changed; when only stylesheets changed, they are swapped in place without
  reloading. Pages reload too when they reconnect to a restarted server.

# doctor

Some steps need something outside Douze: the image steps need libvips, `twoslash` its engine, `csharp` the .NET SDK's
`dotnet` and the Roslyn language server, `go` the `go` executable and gopls, a command step the executable its `run`
line starts with. Every build checks them before any unit runs, and fails at once when one is
missing, with what to install or configure:

```
error: minify: esbuild is not on PATH: install esbuild and add its folder to PATH, or change the 'run' line of step 'minify'
```

`douze doctor` (with `-s`, `-w`, `--cache-dir`) loads the workflow and checks every requirement without building:

```
STEP      REQUIREMENT       STATUS    DETAILS
images    libvips           ok        8.18.7
twoslash  typescript 7.0.2  download  is not installed: it is downloaded for linux-x64 on first use, or now by 'douze tools install'
minify    esbuild           missing   is not on PATH: install esbuild and add its folder to PATH, or change the 'run' line of step 'minify'
og        node              ok        /usr/bin/node

1 requirement missing: builds fail until it is fixed
```

It exits with 1 when a requirement is missing. A requirement is `ok` (with its version or path when known),
`download` (a tool Douze downloads on first use), `missing`, or `unchecked` when it is only known during the build
(a `run` line that starts with a template or a variable). Douze runs nothing to check: the executable of a command is
looked up on `PATH` (and `PATHEXT` on Windows), or, when it is a path (`./scripts/build.sh`), relative to the step's
`cwd`, which must exist; commands of the shell (`echo`, `cd`) need nothing. Its version is not checked: a command that
needs a given version can check it itself.

`douze tools install` downloads every tool the workflow downloads on first use (the `twoslash` engine, the Roslyn
language server of `csharp`, gopls of `go`, built with `go install`) into the cache
folder (`<cache>/tools/`, `--cache-dir`), so that a CI cache of that folder, or a machine that goes offline, has them:

```
douze tools install -s site --cache-dir .cache/douze
```

# site layout

| Folder | Default | Contains |
|---|---|---|
| content | `_content` if it exists, else the source folder | pages, data files that apply to a folder, and any other file to publish |
| data | `_data` if it exists, else none | site data |
| includes | `_includes` if it exists, else the source folder | layouts, includes and templates |
| shortcodes | `shortcodes` in the includes folder | shortcodes, see "shortcodes" |

The output folder and the `.douze` folder are skipped when they are inside the source folder.

# how a site is built

Douze runs a workflow: a graph of steps that files flow through. `douze --print-workflow` prints it. The default
workflow:

1. Reads the data files (`.yml`, `.yaml`, `.json`, `.xml`) of the data folder as site data, and the ones of the
   content folder as folder data.
2. Moves the YAML front matter of `.md`, `.html` and `.liquid` files into their data, and sets `page.date` to the
   file's modification time.
3. Computes output paths: `blog/post.html` and `blog/post.md` are written to `blog/post/index.html`, `index.html`
   and `index.md` stay in place, `sitemap.xml.liquid` is written to `sitemap.xml`, `permalink` overrides the path,
   and `permalink: false` skips the file. The content folder is removed from the path, and `url` holds the result
   (`/blog/post/index.html`).
4. Builds `collections.all` and `collections.<tag>` from the `tags` of every page, and `collections.urls`, the
   same pages by url (`collections.urls["/blog/post/"]`, without `index.html`).
5. Expands paginated pages, which can paginate a collection (`pagination.data: collections.post`).
6. Renders every Markdown, HTML, JSON, XML and text file, and every `.liquid` file, as a Liquid template, unless
   its `liquid` is `false`.
7. Renders Markdown to HTML.
8. Applies layouts.
9. Writes every file to the output folder. Files that no step claims (images, stylesheets, scripts) are copied as
   they are.

Liquid runs before Markdown, as in Eleventy: a Markdown page can use tags and variables, and Markdown renders what
they produce. The fenced code blocks of a Markdown page are not Liquid: `{{ }}` in a code sample is kept as it is. A
page whose Markdown contains other `{{` or `{%` that are not Liquid (inline code) uses `{% raw %}`, or sets
`liquid: false` in its front matter, or its folder's data file sets it for every page of the folder. A variable
captured in a Markdown page (`{% capture footer %}`) holds Markdown source, which is not rendered: write it in HTML.
A Markdown page whose `permalink` is not HTML (`feed.xml`) is not rendered to HTML, and a page whose `markdown` is
`false` is not rendered either.

## data

Each file of the data folder is a site value named after the file: `_data/colors.json` is `colors`, whatever it
contains (a list, an object or a single value), and `_data/pkgs/Contoso.Hosting.json` is `pkgs["Contoso.Hosting"]`.
Two files cannot define the same name.

The top-level keys of the data files of a content folder (`articles/articles.yml`) apply to the pages of that folder
and its subfolders.

A name in a template resolves to the first of: the page's own data (front matter, `page.date`, `url`, captured
variables), the data of its folder, then of the parent folders, the site data, the `site` section of `douze.yml`
(`site.url`), and the front matter of its layouts. For instance a page can set `layout`, or inherit it from its
folder or from `_data/layout.yml`. `site.url` is merged with a `_data/site.json`: `{{ site.title }}` can come from
the data file and `{{ site.url }}` from `douze.yml`, unless the data file defines `url` too.

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
`products/1/index.html`... Each page has its own `url`. Collections are built before pagination, so a page can
paginate `collections.<tag>`; the paginated page itself is in `collections.all` once, with the `url` of its first
page.

## layouts

`layout` names a file of the includes folder. The layout is rendered with the page's data and `content`, and can
itself have a `layout` in its front matter.

# workflow

`douze.yml` at the root of the source folder, or the file given with `--workflow`, configures a site. Its
sections are optional, and a site without the file builds with the default workflow.

```yaml
site:
  content: docs          # default: _content if it exists, else the source folder
  data: data             # default: _data if it exists, else none
  includes: templates    # default: _includes if it exists, else the source folder
  shortcodes: templates/components  # default: shortcodes in the includes folder
  output: public         # relative to the source folder; -o wins
  url: https://example.com  # site.url in templates
  parallelism: 8         # -p wins

markdown:                # see "markdown"
  extensions: [pipetables, footnotes, alerts, customcontainers, attributes]

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
| `uses` | | a built-in step: `data`, `frontmatter`, `parse`, `markdown`, `headings`, `paginate`, `permalink`, `collect`, `liquid`, `layout`, `write` |
| `uses` | | a built-in step: `data`, `frontmatter`, `parse`, `markdown`, `paginate`, `permalink`, `collect`, `localize`, `replace`, `fork`, `liquid`, `layout`, `write` |
| `uses` | | a built-in step: `data`, `frontmatter`, `parse`, `markdown`, `headings`, `paginate`, `permalink`, `collect`, `liquid`, `layout`, `images`, `image-map`, `pictures`, `social-image`, `hover-typescript`, `hover-csharp`, `hover-go`, `highlight`, `write` |
| `with` | none | the options of the built-in step |
| `group-by` | none | `file`, `dir`, or a Liquid template: processes the files in groups (`liquid` with a `template`) |
| `incremental` | `true` | `false` runs the step on every build |
| `enabled` | `true` | `false` leaves the step out: the steps that need it need what it needs (a switch between two steps) |
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

# markdown

The `markdown` section of `douze.yml` configures how Markdown renders, for the `markdown` step and everything else
that renders Markdown:

```yaml
markdown:
  extensions: [pipetables, footnotes, tasklists, definitionlists, attributes, autolinks, alerts, customcontainers]
  aside: { tag: aside, class: aside, kinds: [note, tip, caution, danger], titles: { note: Note }, icons: { note: "<svg>...</svg>" } }
  links: true                                   # .md links to page urls
  code: { class-prefix: language-, passthrough: [mermaid] }
  images: { widths: [640, 1280], formats: [webp], quality: 80, output: assets/images, loading: lazy }   # defaults of the image features
```

Without the section, Markdown is CommonMark with no extension. With it, `extensions` defaults to the list above; it
takes Markdig's names, and `advanced`, Markdig's set without `autoidentifiers` (heading ids are the `headings`
step's). An unknown name is an error (`unknown Markdown extension 'tabels'; did you mean 'pipetables'?`). The
`extensions` option of a `markdown` step replaces the extensions for that step and keeps the other settings.

- Asides: `:::note` ... `:::` containers (`customcontainers`) and `> [!NOTE]` alerts (`alerts`) render as
  `<aside aria-label="Note" class="aside aside--note"><p class="aside__title" aria-hidden="true">Note</p><div class="aside__content">...</div></aside>`,
  with the classes derived from `class` and the icon of the kind before the title. `:::tip[Before you start]` sets
  the title. The alerts `IMPORTANT` and `WARNING` are `note` and `caution`, and any other kind renders as the first
  of `kinds` with a warning.
- Code: a fence renders as `<pre data-lang="csharp" data-meta="{3-4}"><code class="language-csharp">`, the meta being
  the rest of the info string, for highlighters; a `passthrough` language renders as `<pre class="mermaid">`.
- Links: a relative link to a `.md` file of the content folder becomes the url of its page with the default
  permalinks (`other.md` is `/docs/other/`, `index.md` is `/docs/`, `#fragment` kept). A link to a file that does
  not exist is left as it is, and the page renders again when the file is created.
- Raw regions: a line that starts and ends with the U+001E character is HTML written as it is, its U+001D characters
  being newlines, for tags that produce block HTML (shortcodes).

`markdown: false` in front matter or folder data leaves a page as Markdown.

# headings

The `headings` step (`uses: headings`, on `**/*.html`, after `markdown` and before `layout`) works on the HTML of a
page, whatever produced its headings:

```yaml
  headings: { needs: markdown, uses: headings, with: { anchors: true, levels: [2, 3] } }
```

- It gives every heading an id, GitHub style (`What's new?` is `whats-new`, a second `Setup` is `setup-1`), and
  keeps the ids headings already have (`## Title {#custom}` with `attributes`).
- It sets `headings` (every heading: `depth`, `text`, `id`), `toc` (the headings of `levels`, default `[2, 3]`, as a
  tree: each entry has `children`), and `page.heading`, the text of the first `h1`.
- `anchors: true` appends `<a class="heading-anchor" href="#id" aria-label="Link to this heading">#</a>` to each
  heading (`anchor-class`, `anchor-label`).

A layout renders the tree with the `toc` filter, as nested lists: `{{ toc | toc: class: "toc", max-depth: 3 }}`.
# outputs

Feeds, sitemaps and text exports are templates: `sitemap.xml.liquid`, `feed.xml.liquid` or `llms.txt.liquid` in the
content folder are rendered like pages and written without `.liquid`, or to their `permalink`. They are not in the
collections, and layouts apply to HTML files only.

```liquid
---
permalink: /sitemap.xml
---
<?xml version="1.0" encoding="utf-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
{%- for p in collections.all %}
  <url><loc>{{ p.data.url | replace: 'index.html', '' | absolute_url }}</loc><lastmod>{{ p.data.page.date | date_to_xmlschema }}</lastmod></url>
{%- endfor %}
</urlset>
```

| Filter | Result |
|---|---|
| `xml_escape` | `&`, `<`, `>`, `"` and `'` as entities |
| `absolute_url` | `site.url` followed by the url |
| `relative_url` | the url after the path of `site.url` (`/blog/x/` for `https://example.com/blog`) |
| `strip_html` | the text without tags, comments, scripts and styles |
| `date_to_xmlschema`, `date_to_rfc822` | `2020-01-02T03:04:05+00:00`, `Thu, 02 Jan 2020 03:04:05 +0000` |
| `where_exp: "item", "item.data.draft != true"` | the items for which the expression is true |
| `group_by_exp: "item", "item.data.date \| date: '%Y'"` | `{ name, items, size }` per value, in order of appearance |
| `sort_by: "data.title"`, `sort_by: "data.date", "desc"` | the items sorted by the value at a path, missing values last |
| `index_by: "data.url"` | an object of the items by the value at a path |
| `jsonify` | `json` |

The `collect` step options:

| Option | Default | Meaning |
|---|---|---|
| `name` | `collections` | the name of the collections, so that a second `collect` step does not hide the first |
| `content` | `false` | items also have `content`, the text of the page at that point of the workflow; the step then runs again when a body changes, and holds every body in memory |

```yaml
collect-docs: { needs: markdown, uses: collect, match: "docs/**/*.html", with: { name: docs, content: true } }
```

`uses: fork` passes each file through and adds a copy, for instance the Markdown twin of every page, after Liquid:

```yaml
twins:    { needs: render, uses: fork, match: "docs/**/*.md", with: { permalink: "{{ url | replace: 'index.html', 'page.md' }}", data: { twin: true } } }
markdown: { needs: twins, uses: markdown, match: ["**/*.md", "!**/page.md"] }
```

| Option | Default | Meaning |
|---|---|---|
| `permalink` | | the copy's path, a template over the file's data; without an extension, a folder (`index.html` is added) |
| `data` | none | merged over the copy's data; the copy's `url` is its path |
| `content` | the file's | a template over the file's data, with the file's text as `content` |
| `strip-regions` | `true` | removes the lines that are a single U+001E character (the markers of raw HTML regions) |

`uses: replace` replaces literal tokens everywhere in a page, code blocks included, before Liquid:

```yaml
versions: { needs: pages, uses: replace, with: { tokens: { "%VERSION%": "{{ versions.current }}" }, skip: "docs/release-notes/**" } }
```

Each value is a template rendered with the page's data, only in the pages that contain its token: a version change
renders those pages again. The default `match` is `**/*.md` and `**/*.html`.

# navigation

A sidebar is site data, such as `_data/sidebar.yml`: a list of items, each a url, or an object with a `label`, a
`link`, `items` (a group), and anything the layout reads (`icon`, `badge`, `collapsed`, `external`). A `label` is a
string, or an object of labels by locale.

```yaml
- label: { en: Guides, fr: Guides pratiques }
  link: /docs/
  items:
    - label: Get started
      items:
        - { label: Install, link: /docs/install/, badge: { text: New } }
        - /docs/first-app/          # labeled with the page's title, from collections.urls
```

| Filter | Result |
|---|---|
| `sidebar \| nav_active: url` | the sidebar with `active: true` on the url's item and `open: true` on its ancestors |
| `sidebar \| nav_prev_next: url` | `{ prev, next }`: the items next to the url's in the same group; nil when the url is not in the sidebar |
| `sidebar \| nav_breadcrumbs: url` | the items from the top of the sidebar to the url's |
| `sidebar \| nav_flatten` | the items with a link and no items, in order |
| `sidebar \| nav_section: url` | the top-level item that contains the url |
| `item \| nav_label` | the label in the page's `locale` (or the one given), else the default locale's |

Links and urls are compared without `index.html`, and a locale prefix (`/ja/x/`) is removed from the url when no link
of the sidebar has one. Each sidebar is indexed once per build. A page that shows a sidebar depends on all of it: a
sidebar edit renders every such page again. Front matter `prev: false` or `next: false` is for the layout to read.

# locales

```yaml
site:
  locales:
    default: en           # the locale whose pages have no url prefix
    root: docs            # the folder of the content folder whose subfolders are locales; default: the content folder
    fallback: true        # copy untranslated pages under every locale (default false)
    strings: i18n         # the data of the UI strings: _data/i18n/en.yml, _data/i18n/ja.yml; default i18n
    languages:            # locale folders
      en: { label: English }             # the default locale's settings
      ja: { label: 日本語, lang: ja }
      ar: { label: العربية, lang: ar, dir: rtl }
```

A page of `docs/ja/` is a page of `ja`, any other page under `docs` is a page of `en`. `permalink` removes the
`root` folder from paths and keeps the locale folder: `docs/ja/guide.md` is `/ja/guide/`, `docs/guide.md` is
`/guide/`. `lang` defaults to the key and `dir` to `ltr`. Templates see the settings in `site.locales`, with
`site.locales.languages`, the list of every locale (`locale`, `label`, `lang`, `dir`, `prefix`), default first.

`uses: localize`, after `collect`, sets on every page under `root`: `locale`, `lang`, `dir`, `locale_url` (its url
in every locale: `/guide/`) and `alternates`, the `{ locale, lang, url }` of the locales that have a page at that
url, for `hreflang` links and language switchers. With `fallback: true`, a page of the default locale is copied under
each locale that does not translate it, with the locale's `locale`, `lang`, `dir` and url, `fallback: true` and
`original_url`, so that the layout renders it in the locale with a notice. Copies are not in the collections. Every
url of the default locale is then served in every locale, so the `alternates` of its pages and of their copies list
every language of `site.locales`.

```yaml
localize: { needs: [permalink, collections], uses: localize }
```

A page depends on the urls it looked for only: adding `docs/ja/guide.md` runs `localize` for `/guide/` again, not for
the other pages, and with `fallback: true` the pages after it do not render again when their `alternates` are the
same.

| Filter | Result |
|---|---|
| `"search.label" \| t` | the string in `i18n.<locale>` (a key with dots, or nested keys), else in the default locale's, else the key, with a warning; named arguments replace placeholders: `"count" \| t: n: 3` replaces `{n}` |
| `url \| locale_url: "ja"` | the url in another locale, whether or not a page exists there (`/ja/guide/`); no argument: without its prefix |
| `url \| locale_of` | the locale of a url |
| `collections.all \| locale_pages` | every page of every locale as `{ locale, lang, url, fallback, data }`, fallback copies included: for sitemaps |

# shortcodes

A shortcode is a Liquid file of the shortcodes folder (`_includes/shortcodes/` by default, `site.shortcodes` in
`douze.yml`) that every template can use as a tag: `shortcodes/badge.liquid` is `{% badge %}`. File names are tag
names: lowercase letters, digits and underscores (`link_card.liquid` is `{% link_card %}`), and a name that is a
Liquid or Douze tag (`include`, `raw`, `markdown`...) is an error. The front matter of the file says what kind of tag it
is:

```liquid
---
block: true       # paired: {% tabs %}...{% endtabs %}; default false: {% badge "New" %}
display: block    # block or inline; default block when paired, inline otherwise
args: [label]     # optional: names of the positional arguments
---
<div class="tabs" data-sync="{{ sync }}">{{ body }}</div>
```

```liquid
{% tabs sync: "lang" %}
{% tab "C#" %}
Some **Markdown**, and a fence:

```csharp
var x = 1;
```
{% endtab %}
{% endtabs %}

Install it with {% badge "v2", variant: "note" %}.
```

- Arguments are positional values and `name: value` pairs separated by commas, and any value or variable
  (`{% tab page.title %}`). In the template, a named argument is a variable, the positional ones are `args` and, when
  `args` names them, variables too. The page's names (`title`, `page`, data, `collections`, `site`) resolve as in an
  include. A shortcode can use another one.
- The body of a paired shortcode is rendered with the page's variables, then, in a Markdown page, as Markdown to HTML
  with the site's pipeline: that is `body`, and `source` is the body before Markdown. A `display: inline` body has no
  `<p>` (`Press {% kbd %}**Ctrl**{% endkbd %}`). In an HTML page or a layout, `body` is the body as it is.
- In a Markdown page, the output of a `display: block` shortcode is a raw region (see "markdown"): one line at the
  column of the tag, which the page's Markdown pass keeps as it is, blank lines included, in the list item or block
  quote of the tag. An inline shortcode's output is written in the text.
- `{% slot "title" %}...{% endslot %}` in a body renders as `<div data-slot="title">...</div>`, its content rendered
  like a body, for a shortcode template that places the parts of its body.
- A page renders again when a shortcode it uses changes. An error in a shortcode names it and the page
  (`error: render: /docs/a.md: shortcode 'tabs' (/_includes/shortcodes/tabs.liquid(3,12)): ...`), an unknown tag
  suggests a shortcode (`Unknown tag 'tabss'; did you mean 'tabs'?`), and an invalid shortcode file stops the build
  before it starts, with its position.

# markdown includes

- `{% include_markdown "reference/options.md", version: "2.0" %}` renders a Markdown file of the content folder, or
  of the including file's folder with `./` and `../`, as a Liquid template with the page's variables. Its front matter
  values are variables that the page's names hide, and the named arguments hide both. In a Markdown page its output
  is Markdown that the page renders, so its headings are the page's, and every line is indented like the tag, so an
  include stays in its list item. Elsewhere it is rendered to HTML. Its fences are not Liquid, a missing file is an
  error, includes nest up to 16 deep, and the page renders again when the file changes. A partial that is not a page
  sets `permalink: false`.
- `{% markdown %}...{% endmarkdown %}` renders its body as Markdown, in layouts and HTML pages.
- `{{ text | markdownify }}` renders a string as Markdown; `markdownify: inline: true` leaves a single paragraph without
  `<p>`. `{{ text | strip_markdown }}` is its plain text, `{{ content | excerpt }}` the first paragraph as HTML, or
  what comes before `<!-- more -->`.

# diagnostics

- `{% error "unknown API reference " | append: name %}` fails the page with the position of the tag
  (`error: render: /docs/a.md(12,3): unknown API reference Foo`); `{% warn "deprecated" %}` logs a warning.
- The `frontmatter` step's `require` lists keys every page must have, in its front matter or in folder or site data:
  `{ uses: frontmatter, with: { require: [title, description] } }`.
- The `liquid` step's `strict: true` makes an undefined variable an error rather than nothing; shortcode templates
  are not strict, so their optional arguments work.
- The `liquid` step's `protect: none` renders the fences of Markdown pages as Liquid.

# images

The `image` tag, in a page or a layout, makes the variants of a source image and writes their markup:

```liquid
{% image "images/dashboard.png", alt: "The dashboard", widths: [640, 1280], formats: ["webp", "png"], sizes: "(min-width: 60em) 50vw, 100vw", class: "figure" %}
{% image "/images/arch.svg", alt: "Architecture" %}
{% image light: "images/a-light.png", dark: "images/a-dark.png", alt: "The flow" %}
```

- The source is relative to the page's folder, or to the content folder when it starts with `/`. It is a dependency
  of the page: changing the image renders the page again.
- Each variant (a width, a format) is written to `output` (default `/assets/images/`) as
  `<name>-<width>.<hash>.<ext>`, the hash being that of the variant, so a changed image gets new names. Variants are
  never wider than the source; metadata (EXIF, XMP, ICC) is stripped; a variant used by several pages is written once.
- The markup is a `<picture>` with a `<source>` per format but the last, whose `srcset` lists every width, and an
  `<img>` of the last format with its largest width, `width`, `height` (no layout shift), `alt`, `loading` (`lazy`),
  `decoding="async"`, `class`, and any other named argument as an attribute (`true` without a value). One format gives
  a plain `<img>`, with a `srcset` for several widths.
- `light` and `dark` give one `<picture>` whose dark sources have `media="(prefers-color-scheme: dark)"`; with
  `theme: "attribute"`, two elements with `data-theme="light"` and `data-theme="dark"`, for a site that toggles a theme
  attribute and hides one in CSS.
- An SVG or a GIF is copied as it is (`<name>.<hash>.<ext>`), its size read from the file (an SVG's `width` and
  `height`, or its `viewBox`).
- `widths`, `formats` (`webp`, `avif`, `png`, `jpeg`), `quality` (default 80), `output` and `loading` default to the
  `images` settings of the `markdown` section; without widths, the source's width, without formats, its format.
- Arguments take strings, numbers, `true`, `false`, lists (`[640, 1280]`) and variables (`page.hero`).

`{{ "images/a.png" | image_url: width: 400, format: "webp" }}` makes one variant and returns its url (`og:image`, CSS);
`{{ "images/a.png" | image_size }}` returns `{ width, height }`.

For Markdown images (`![alt](images/x.png)`), three steps do the same without a tag:

```yaml
  images:    { inputs: "docs/**/*.{png,jpg,jpeg,webp,avif,gif,svg}", uses: images, with: { widths: [640, 1280], formats: [webp] } }
  image-map: { needs: images, uses: image-map }
  pictures:  { needs: [markdown, image-map], uses: pictures }
  write:     { needs: [layout, images], uses: write }
```

`images` emits the variants and the original (fingerprinted) of each image instead of the file, each with `image`:
`{ source, url, width, height, format, variants: [{ url, width, height, format }] }`. `image-map` collects them as
`images`, by source url (`images["/docs/images/x.png"]`), for templates and `pictures`, which rewrites the `<img>` of
the images it knows into the tag's markup, keeping their other attributes. A page depends on the images it shows only.

Image processing uses libvips (NetVips), included in the native AOT packages. Without it, a workflow with an `images`
or `social-image` step fails before building, with the steps to get it (`douze doctor` checks it); the `image` tag
and the `image_url` and `image_size` filters fail only the pages that use them on an image other than an SVG or a
GIF, which never need it.

# social images

The `social-image` step renders an Open Graph image per page: an SVG Liquid template rasterized as the background,
with text laid out over it.

```yaml
  og: { needs: pages, uses: social-image, match: "docs/**/*.md",
        with: { template: og/card.svg.liquid, width: 1200, height: 630, output: "og/{{ slug }}.png", skip: "og == false",
                text: [ { value: "{{ title }}", font: "Outfit Bold 56", fontfile: assets/fonts/Outfit-Bold.ttf, x: 80, y: 160, width: 1040, height: 220, color: "#ffffff" },
                        { value: "{{ description }}", font: "Outfit 30", fontfile: assets/fonts/Outfit-Regular.ttf, x: 80, y: 400, width: 1040, height: 150, color: "#c8c8d0", align: center } ] } }
```

- `template` is in the includes folder and renders with the page's model; images it references by a relative path
  (to the template's folder, or to the content folder with `/`) are inlined, and are dependencies of the page.
- Each `text` block renders its `value` (a Liquid template) with Pango as plain text: `font` (a Pango font: family,
  style, size in pixels), wrapped at `width`, clipped at `height`, aligned `left`, `center` or `right` in its box,
  colored (`#rrggbb` or `#rrggbbaa`), at `x`, `y`. `fontfile` (relative to the source folder) loads a font file first:
  containers usually have no system fonts.
- `output` (a Liquid template) is the image's path; `skip` (a Liquid condition) leaves a page without one; `width` and
  `height` default to 1200 and 630.
- The step passes every page through unchanged and adds the image to the page's outputs: it sits in the page chain,
  not on a branch. A layout computes the url with the same template: `{{ page | social_image_url: "og/{{ slug }}.png" }}`.

# hover-typescript

The `hover-typescript` step computes the type information of TypeScript code fences, as
[twoslash](https://twoslash.netlify.app) does, without Node: with the language server of TypeScript 7, Microsoft's
native compiler, which Douze downloads.

````yaml
  typescript: { needs: markdown, uses: hover-typescript,
                with: { files: { "modules/sdk.mts": data/types/sdk.d.ts },
                        compiler: { moduleResolution: bundler, lib: [es2022, dom] }, handbook: { noErrorValidation: true } } }
````

Every fence of an HTML page that the `markdown` step wrote with a language of `languages` (`ts`, `tsx`, `typescript`)
and the word `twoslash` in its meta (```` ```ts twoslash ````) gets a `data-twoslash` attribute: twoslash's result for
its code, `{ "code": ..., "nodes": [...] }`, the code without its notations and the hovers, `^?` queries, errors,
`^^^` highlights, `^|` completions and custom tags with their offsets, lines and characters. A highlighter renders the
popups from it instead of running twoslash (with Expressive Code, a `twoslasher` that returns it). The fence itself is
unchanged.

- Notations: `// @errors: 2322`, `// @noErrors`, `// @noErrorValidation` and the other handbook options, compiler
  options (`// @strict: false`, `// @target: es2015`), `// ---cut---` (`-before`, `-after`, `-start`, `-end`),
  `// @filename: a.ts` for several files, and the tags of `tags` (`annotate`, `log`, `warn`, `error` by default).
  `showEmit` is not supported.
- `compiler`: tsconfig compiler options over twoslash's defaults (`strict`, `module` and `target` `esnext`, `allowJs`,
  `skipLibCheck`, `moduleDetection: force`). `files`: files placed next to every snippet, a path to a file of the
  source folder (a declaration file the snippets import). `handbook`: handbook options for every snippet.
- `trim` (true): the code is checked as highlighters render it, without trailing spaces and blank lines around it.
  `tab-width` replaces tabs first.
- A snippet that twoslash would reject (an error its `// @errors` does not list, a query that finds nothing) fails the
  build, naming its page, the fence's number on the page and its first line. With `errors: warn` it is a warning, and
  the fence keeps its result when it has one (the errors are rendered). `handbook: { noErrorValidation: true }` renders
  unexpected errors without a warning.

All the snippets of a build are checked at once: the snippets that share their compiler options are the files of one
project (each in its own folder, the `files` shared), checked by one language server whose requests are sent in
parallel; `processes` (4) language servers run at once, `timeout` (300 seconds) each. Each result is kept in the cache
by its code and what the step depends on (the engine and its version, the options, the content of `files`): a build
checks only new snippets, and starts no language server when there is none.

The engine is downloaded on first use from the npm registry (`registry`), the package of the platform, verified
against its integrity (the one Douze pins for its default version), into `<cache>/tools/<engine>/<version>/<platform>/`;
later builds use it offline, and `douze tools install` downloads it ahead (see "doctor"). `engine` is `typescript` (TypeScript 7, `@typescript/typescript-<platform>`, for Windows,
macOS and Linux; 7.0.2 by default) or `tsrs` ([tsrs](https://github.com/oxc-project/tsrs), a port of TypeScript 7 to
Rust with the same protocol, for macOS on Apple silicon and Linux; it gives the same results with less memory).
`version` picks another release; `path` (relative to the source folder) uses a binary instead, for offline machines.

The hover texts are TypeScript 7's, which prints some types differently from TypeScript 5: no `import x` line under
an alias, union members in another order (literal types sorted), a type and a const of the same name in the other
order (`bench/twoslash.md` has a comparison). At a call, the step adds the count of the other overloads after the
signature (`(+1 overload)`), as TypeScript 5 prints it and TypeScript 7 does not, from the call's signature help; it
does not at a declaration.

# hover-csharp

The `hover-csharp` step does the same for C# fences with the Roslyn language server: every fence of `languages` (`csharp`,
`cs`, `c#`; only those whose meta has the `trigger` word when it is set) gets a `data-twoslash` attribute with the hovers
of its symbols (signature and documentation) and its errors, in the shape of the `hover-typescript` step's results, so that the
same renderer shows them.

````yaml
  csharp: { needs: markdown, uses: hover-csharp,
            with: { errors: warn, projects: { web: { sdk: Microsoft.NET.Sdk.Web, titles: [Program.cs],
                                                     packages: { Some.Package: "1.2.3" }, usings: [Some.Namespace] } } } }
````

- Snippets compile in generated projects: `projects` maps names to their `sdk` (`Microsoft.NET.Sdk`), `framework`
  (`net10.0`), `packages` (ids to versions), `usings` (global usings), `properties` (MSBuild properties), `files`
  (shared files of the source folder, such as stubs), `context` (a class of a shared file whose fields are the variables
  that fragments use without declaring them), `titles` and `patterns`. A fence uses the project of its meta's
  `project=name`, else the first whose `titles` end its `title`, else the first whose `patterns` match its code, else
  `default`.
- A snippet can be top-level statements, type declarations or class members: each is wrapped in a namespace of its own,
  so that the snippets of a project don't see each other.
- A snippet with errors fails the build, naming its page, the fence's number and its first line; with `errors: warn` it
  is a warning and the errors are rendered. `ignore` lists diagnostic codes that are neither reported nor rendered.
- The server (`roslyn-language-server.<rid>` from nuget.org, `registry`; `version`, or `path` to its
  `Microsoft.CodeAnalysis.LanguageServer.dll`) is downloaded on first use into `<cache>/tools/roslyn/<version>/<rid>/`.
  It needs the .NET SDK (10 or later), which also restores the projects: `dotnet`, or `DOTNET_HOST_PATH`, or the
  `PATH`'s.

Outcomes, errors included, are kept in the cache like the `hover-typescript` step's. `docs/code-hover-steps.md` has the design,
the costs and the plan for other languages.

# hover-go

The `hover-go` step does the same for Go fences with gopls: every fence of `languages` (`go`, `golang`; only those whose meta
has the `trigger` word when it is set) gets a `data-twoslash` attribute with the hovers of its symbols and its errors.

````yaml
  go: { needs: markdown, uses: hover-go,
        with: { errors: warn, ignore: [UnusedVar],
                modules: { default: { requires: { github.com/redis/go-redis/v9: v9.7.0 } } } } }
````

- Each snippet is a package of its own (`s<n>/main.go`, or the file name of its `title`) in a generated module: a
  snippet without a `package` clause gets `package main`, and statements outside a function go in `func _() { ... }`.
- `modules` maps names to their `requires` (module paths to versions, for third-party imports, added with `go get`),
  `files` (shared files of the source folder: a package of stubs at `api/api.go` is imported as `snippets/api`),
  `titles` and `patterns`. A fence uses the module of its meta's `module=name`, else the first whose `titles` end its
  `title`, else the first whose `patterns` match its code, else `default`.
- Errors fail the build, or warn with `errors: warn`; `ignore` lists diagnostic codes (`UnusedVar`, `UnusedImport`,
  `BrokenImport`...) that are neither reported nor rendered.
- gopls has no prebuilt binaries: on first use, `go install golang.org/x/tools/gopls@<version>` builds it into
  `<cache>/tools/gopls/<version>/` (`version`, or `path` to a gopls). It needs Go (`go`, or the `PATH`'s), which also
  adds the requirements and loads the packages; the Go version is part of the outcomes' key.

# highlight

The `highlight` step highlights the code fences of HTML pages, as the `markdown` step writes them, with TextMate
grammars and themes (the grammars of VS Code and of most highlighters for the web), without Node:

````yaml
  highlight: { needs: markdown, uses: highlight,
               with: { themes: [github-dark, github-light], stylesheet: assets/code.css } }
````

- Grammars and themes: every grammar of [tm-grammars](https://github.com/shikijs/textmate-grammars-themes) and every
  theme of tm-themes are bundled (the versions of Shiki 4.5), and `grammars` adds files of the source folder (names to
  grammar files: `grammars: { mylang: syntax/mylang.json }`); `aliases` maps fence languages to grammar names. A fence
  whose language no grammar knows is plain text, with one warning per build listing the languages and their fences.
  `text`, `txt`, `plain` and `plaintext` are plain text.
- Themes: `themes` lists bundled names or theme files (VS Code's JSON); the first is the default, the others are
  selected by CSS. Each token is a `<span style="--0:#color;--1:#color">`, a variable per theme (with `--0fs`, `--0fw`
  and `--0td` for italic, bold and decorations), so that a stylesheet switches themes without another render.
  `min-contrast` (5.5) makes token colors readable on the background and on marked lines (a WCAG contrast ratio, the
  rules of Expressive Code); 0 keeps the theme's colors. `markers` sets the background of `mark`, `ins` and `del` lines
  in each theme, which that applies to.
- Fence options, in the meta: `title="file.cs"` (else a file name in a comment on one of the first lines, which is
  removed: `title-from-comment`), `frame=code|terminal|none` (terminal languages get a terminal frame), line markers
  `{1,3-5}`, `ins={2}`, `del={3}` (`{"Label":2-3}` labels them), inline markers `"text"` and `/regex/` (keyed the same
  way), `diff` fences (with `lang=ts` to highlight the code), `collapse={4-10}` with `collapseStyle`,
  `showLineNumbers` and `startLineNumber=`, `wrap`. Every option is also given to the template.
- Twoslash: a fence with a `data-twoslash` attribute (from `hover-typescript`, `hover-csharp` or `hover-go`) gets its
  popups: the hovered code wraps the hover's type (highlighted as a block of its own, in the fence's language),
  documentation (Markdown) and tags; errors, queries, highlights and custom tags too.
- Markup: Liquid templates of the includes folder render a block (`templates: { block: code/block.liquid }`, given
  `code`: `language`, `title`, `frame`, `terminal`, `copy` (the code to copy, without the comment lines of a terminal),
  `options`, `lines` (each with `html`, `text`, `number`, `marker`, `label`, `indent`), `rows` (the lines grouped in
  collapsed `section`s), `line_numbers`, `wrap`, `popup`) and an annotation (`templates: { popup: ... }`, given `node`:
  `kind`, `content`, `type`, `docs`, `tags`, `text`...). Douze's defaults write
  `<figure class="code"><figcaption>...</figcaption><pre><code><span class="line">...` and the twoslash classes of
  other renderers (`twoslash-hover`, `twoslash-popup`); `stylesheet` writes a stylesheet for them. The templates read
  the page's data like any template (its `lang`, the `t` filter).
- `tab-width` replaces tabs; the code is trimmed as Markdown highlighters do (trailing spaces, blank lines around it).

The tokens of each fence are kept in the cache by its language and code, so that a changed page highlights only its
new fences. Tokenization uses [TextMateSharp](https://github.com/danipen/TextMateSharp) on Oniguruma (a native library
next to `douze`, like libvips); Douze reads the grammars and resolves the themes itself, with vscode-textmate's rules,
and gives Shiki's tokens.

# commands

A step with `run` instead of `uses` runs an external tool, such as a Node script. `mode` says how files reach it:

| Mode | Processes | Shape | Use it for |
|---|---|---|---|
| `filter` | one per file | map: content in on stdin, out on stdout | a handful of files, or tools that are not Node |
| `batch` (default) | one for all the files, or one per group with `group-by` | reduce: the files are written to a folder | tools that want a folder (OG images, Pagefind) |
| `worker` | one for the step, sent each file over JSON lines | map | per-file work at scale (twoslash on thousands of fences) |

Starting Node costs about 50 ms, so a `filter` over thousands of files spends minutes starting processes; a worker
starts once, while the steps before it run.

```yaml
minify: { needs: layout, run: "npx html-minifier-terser --collapse-whitespace", mode: filter, match: "**/*.html" }
search: { needs: write, run: "npx pagefind --site {{ site }}" }
og-images: { needs: render, run: "node scripts/og.mjs {{ inputs }} {{ outputs }}", match: "blog/**/*.html", outputs: "**/*.png" }
twoslash: { needs: render, run: "node scripts/twoslash-worker.mjs", mode: worker, match: "**/*.md", sources: ["scripts/twoslash-worker.mjs"] }
```

The fields of a command step:

| Field | Default | Meaning |
|---|---|---|
| `run` | | The command line, a Liquid template (below) |
| `mode` | `batch` | `filter`, `batch` or `worker` |
| `env` | | Environment variables; values are templates too |
| `cwd` | the source folder | Working directory, relative to the source folder |
| `timeout` | none | `30s`, `5m`: the process tree is killed and the unit fails. For a worker, each file has this long |
| `stderr` | `fail` | What output on stderr means: `fail`, `warn` (shown as a warning) or `ignore` |
| `shell` | `true` | Run through `/bin/sh -c` (`cmd.exe /d /s /c` on Windows); `false` splits the line and runs the program directly |
| `outputs` | every file | Batch: globs of the files read back from the outputs folder |
| `group-by` | | Batch: `file`, `dir` or a Liquid template; one process per group |
| `with` | | Worker: options sent to the worker in `hello` |

A non-zero exit code fails the unit with the end of stderr:
`error: minify: /blog/a.html: command exited with code 2 for '/blog/a.html': <last 20 lines of stderr>`. Node prints
some warnings on stderr (`ExperimentalWarning`): set `NODE_NO_WARNINGS: "1"` in `env`, or `stderr: warn`.

## the command line

`run` and `env` are rendered with these values:

| Value | Modes | Meaning |
|---|---|---|
| `{{ step }}` | all | The step name |
| `{{ site }}` | all | The output folder (absolute) |
| `{{ cwd }}` | all | The working directory (absolute) |
| `{{ inputs }}` | batch | The folder holding the input files |
| `{{ outputs }}` | batch | The folder the command writes its files to |
| `{{ file.path }}`, `file.dir`, `file.name`, `file.ext` | filter | The file's virtual path (`/blog/a.md`), folder (`/blog/`), name (`a.md`), extension (`.md`) |

A value written directly in `run` is quoted for the shell when it needs it (`'/my site/_site'` with `/bin/sh`,
`"C:\my site\_site"` with `cmd.exe`); the result of a filter (`{{ file.name | remove: '.md' }}`) is written as it is.
`env` values are never quoted. The environment also has `DOUZE_STEP`, `DOUZE_SITE` (output folder) and
`DOUZE_SOURCE` (source folder).

Before the build, the program the line starts with is looked up on the PATH, or relative to `cwd` when it is a path:
a missing one fails the build at once (see "doctor").

With `shell: false`, the line is split on whitespace, `'...'` is literal, and in `"..."` a `\"` is a quote. The
program is looked up on the PATH; on Windows, use the shell for `npx` and other `.cmd` scripts.

## filter

The file's content goes to stdin, stdout becomes its new content; the path and data are unchanged. The command also
gets `DOUZE_PATH` (`/blog/a.md`), `DOUZE_CONTENT_TYPE` and `DOUZE_METADATA` (the file's own data as JSON), or
`DOUZE_METADATA_FILE` (a file holding it) when the JSON is over 32 KB. Files run in parallel, up to `-p` at once.

## batch

When the command line references `{{ inputs }}`, every file the step consumes is written under that folder at its
path, next to `douze-inputs.json`:

```json
{ "step": "og-images", "unit": "*", "files": [
  { "path": "/blog/a.html", "contentType": "text/html", "metadata": { "title": "A", "url": "/blog/a/" } } ] }
```

When it references `{{ outputs }}`, that folder starts empty, and once the command succeeds every file it wrote there
(matching `outputs`) becomes a file of the site at the same path, `out/blog/a.png` being `/blog/a.png`. An optional
`douze-outputs.json`, with the same shape, gives files a content type and data. The consumed files are replaced by the
outputs: a step that should keep them uses `match` to consume only what it needs. A command that uses neither (the
Pagefind example) gets nothing on disk, and its step emits no files. stdout is shown with `--verbose`.

The folders are under `<source>/.douze/tmp/` (`--cache-dir`), and are deleted after the run unless `--verbose`.

## worker

A worker is a long-running process that reads requests on stdin and writes answers on stdout, one JSON object per
line (UTF-8, `\n` terminated, protocol 1). It starts with the step and stops when the step is done.

1. Handshake: Douze sends `hello`; the worker answers `ready` within 30 seconds. `options` is the step's `with`.
   `concurrency` is how many requests the worker takes at once (1 when omitted); Douze sends at most the smaller of
   both.

   ```json
   {"type":"hello","protocol":1,"step":"twoslash","concurrency":8,"options":{},"site":"/abs/_site","source":"/abs/src"}
   {"type":"ready","protocol":1,"concurrency":4,"name":"twoslash-worker"}
   ```

2. One request per file, answered in any order. `encoding` is `utf8` for text and `base64` for binary content. A
   `result` has zero files (the file is dropped), one or many. In a file, an omitted `path`, `content` or
   `contentType` keeps the request's (a new path without `contentType` gets the type of its extension), and
   `metadata` is merged over the file's data. An `error` fails that file only, and `line` and `column` are shown.

   ```json
   {"type":"file","id":"42","path":"/docs/a.md","contentType":"text/markdown","encoding":"utf8","content":"# Title\n...","metadata":{"title":"A"}}
   {"type":"result","id":"42","files":[{"path":"/docs/a.md","encoding":"utf8","content":"# Title\n...","metadata":{"twoslash":true}}]}
   {"type":"error","id":"42","message":"TS2304: Cannot find name 'foo'","file":"/docs/a.md","line":12,"column":3}
   ```

3. Logs, at any time, shown with the step name (`level`: `debug`, `info`, `warn` or `error`):

   ```json
   {"type":"log","level":"info","message":"loaded api.d.ts in 1.2 s"}
   ```

4. Shutdown: after the last answer Douze sends `shutdown` and closes stdin; the worker exits with code 0 within 10
   seconds, or it is killed.

   ```json
   {"type":"shutdown"}
   ```

Unknown message types are ignored. A line that is not JSON, output on stderr (with `stderr: fail`), or a worker that
exits before `shutdown` fails every file in flight and the step: `worker exited with code 1: <end of stderr>`.

`douze worker-helper > scripts/douze-worker.mjs` writes a small Node helper that speaks the protocol:

```js
// scripts/twoslash-worker.mjs
import { serve } from './douze-worker.mjs';

serve(async (file, ctx) => {
  // file: { path, contentType, metadata, content } (a string, or a Buffer for binary content)
  // ctx: { step, options, site, source, log(message, level) }
  return { content: await transform(file.content), metadata: { twoslash: true } };
}, { concurrency: 4, name: 'twoslash-worker' });
```

The handler returns the new content (a string or a Buffer), a file (`{ content, path, contentType, metadata }`, every
member optional), an array of files, or `null` to drop the file. A thrown error fails that file; give it `line` and
`column` properties to point at a position.

# progress and tracing

In a terminal, Douze shows a live table of the running steps: the shape of each step, the units done out of the
total (`done/?` while a streaming step does not know its total yet), a progress bar and the elapsed time. Each
completed step prints one line above the table. At the end, a summary table lists every step with its state, files
in and out, units and time, and a timeline column that places each step on the time scale of the build, so you can
see which steps ran at the same time and which one held the others back. A step's time starts when its first file
reaches it, not while it waits for the steps it needs.

The live view is replaced by one line per step (the plain view) with `--plain`, when the output is redirected, when
`CI` is set (to anything but `false` or `0`), or when `TERM=dumb`. `NO_COLOR` keeps the live view without colors.
`--quiet` prints errors only; `--verbose` adds stack traces, and in the plain view one line per unit.

```
douze --trace build.json        # Chrome trace events
douze --timeline build.html     # a self-contained page with the timeline of the build
douze --trace-units --timeline build.html   # also one span per file or group
```

`--trace` writes [Chrome trace events](https://docs.google.com/document/d/1CvAClvFfyA5R-PhYUmn5OOQtYMH4h6I0nSsKchNAySU)
that open in [Perfetto](https://ui.perfetto.dev) or `chrome://tracing`: one lane for the build, then lanes for the
steps, then lanes for the units, commands and I/O, so that overlapping spans never share a lane. Times are
microseconds since the start of the build.

`--timeline` writes a single HTML file that works offline: one row per step on a time axis (red when the step
failed), with the spans of its units (with `--trace-units`) and of its commands packed under it, a tooltip with the details of each span, zoom with the mouse wheel, pan
by dragging, and a table of the spans sortable by duration. It follows the light or dark theme of the system.

Both files are written even when the build fails or is cancelled. Step and command spans are always recorded; unit
spans only with `--trace-units`, because thousands of spans of a few microseconds each are noise on a timeline.
