---
title: Project structure
description: The folders of a Douze site, what each one holds, and how source files map to output files.
liquid: false
---

A Douze site is a folder. Douze recognizes a few folders by their names, all optional, and builds everything else
with conventions. This page describes them, and how a source file becomes an output file.

## A typical site

```txt
my-site/
├── _content/              pages and files to publish
│   ├── index.md
│   ├── about.md
│   ├── blog/
│   │   ├── blog.yml       folder data for blog/ and its subfolders
│   │   └── first-post.md
│   ├── css/site.css
│   └── feed.xml.liquid
├── _data/                 site data
│   ├── site.json
│   └── sidebar.yml
├── _includes/             layouts, includes and templates
│   ├── base.liquid
│   ├── post.liquid
│   └── shortcodes/        shortcodes
│       └── callout.liquid
├── douze.yml              optional configuration
├── _site/                 the output, written by douze
└── .douze/                the cache, written by douze
```

## Folders

| Folder | Default | Contains |
|---|---|---|
| content | `_content` if it exists, else the source folder | Pages, folder data files, and any other file to publish |
| data | `_data` if it exists, else none | Site data |
| includes | `_includes` if it exists, else the source folder | Layouts, includes and templates |
| shortcodes | `shortcodes` in the includes folder | Shortcodes |

The `site` section of `douze.yml` changes them (`content`, `data`, `includes`, `shortcodes`), and a folder set there
is used whether or not it exists. Paths are relative to the source folder.

```yaml title="douze.yml"
site:
  content: docs
  data: data
  includes: templates
```

### `_content`

Every file of the content folder goes through the workflow. Markdown (`.md`), HTML (`.html`) and Liquid (`.liquid`)
files are pages: their front matter is read, they are rendered as Liquid, and the HTML ones get their layouts. The data
files (`.yml`, `.yaml`, `.json`, `.xml`) are folder data, not published: their top-level keys apply to the pages of
their folder and its subfolders. Every other file, such as an image, a stylesheet or a font, is copied as it is.

The content folder is removed from output paths: `_content/blog/first-post.md` is published at `/blog/first-post/`.

### `_data`

Each file of the data folder is a site value named after the file: `_data/site.json` is `site`, `_data/sidebar.yml`
is `sidebar`, and `_data/pkgs/Contoso.Hosting.json` is `pkgs["Contoso.Hosting"]`. Two files cannot define the same
name. See [Data files](data.md).

### `_includes`

Layouts (`layout: post.liquid`), files of `{% include %}` and `{% render %}`, and the templates of steps (`liquid`
with a `template`, `highlight` templates, `social-image` templates) are looked up in the includes folder. See
[Layouts](layouts.md) and [Includes and shortcodes](shortcodes.md).

### `_includes/shortcodes`

Each Liquid file of this folder is a tag that every template can use: `shortcodes/callout.liquid` is
`{% callout %}`. See [Includes and shortcodes](shortcodes.md#shortcodes).

### `douze.yml`

The optional configuration file at the root of the source folder: the `site`, `markdown` and `steps` sections. See
[Configuration overview](configuration.md).

### `_site`

The output folder. Douze writes the site there, and after a successful build deletes the files that previous builds
wrote and this one did not. `-o` sets another folder, relative to the current folder, and `site.output` in
`douze.yml` sets one relative to the source folder; `-o` wins. Without either, the output is `_site` in the current
folder, so `douze -s my-site` run from the parent folder writes to `./_site`.

### `.douze`

The cache folder: the results of the previous builds (`.douze/cache/`), the tools that steps download
(`.douze/tools/`) and the temporary folders of command steps (`.douze/tmp/`). `--cache-dir` moves it. Add both
`.douze/` and `_site/` to your `.gitignore`.

```txt title=".gitignore"
_site/
.douze/
```

The output folder and the cache folder are skipped when they are inside the source folder.

## Sites without `_content`

Without `_content`, the source folder is the content folder, and without `_includes` it is also the includes folder.
That is fine for a site of a few pages, but every file of the folder is then a page or an asset, layouts included:
`base.html` is published as `/base/`, and so are dot files and folders such as `.gitignore`, `.git/` or
`node_modules/`. Only `douze.yml`, the data and includes folders when they exist, the output folder and `.douze/` are left out.

:::tip
Use `_content` and `_includes` for anything beyond a handful of pages: only the files of `_content` are published.
:::

## How files map to outputs

The `permalink` step computes each output path from the source path, with the content folder removed:

| Source | Output | `url` |
|---|---|---|
| `_content/index.md` | `index.html` | `/index.html` |
| `_content/about.md` | `about/index.html` | `/about/index.html` |
| `_content/blog/first-post.html` | `blog/first-post/index.html` | `/blog/first-post/index.html` |
| `_content/blog/index.html` | `blog/index.html` | `/blog/index.html` |
| `_content/feed.xml.liquid` | `feed.xml` | `/feed.xml` |
| `_content/css/site.css` | `css/site.css` | `/css/site.css` |
| `_content/blog/blog.yml` | not published (folder data) | |

A page's front matter `permalink` overrides its path, and `permalink: false` leaves it out. See
[Routing and permalinks](routing.md).

## See also

- [Routing and permalinks](routing.md)
- [Develop and build](develop-and-build.md)
- [Data files](data.md)
- [douze.yml reference](../reference/configuration.md)
- [Command line reference](../reference/cli.md)
