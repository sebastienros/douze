---
title: Migrate from Hugo
description: Map Hugo's folders, Go templates, sections and shortcodes to Douze, and port a site step by step.
liquid: false
---
A Hugo site keeps its content, its data and its static files when it moves to Douze, and Hugo's default URLs
(`content/posts/first.md` at `/posts/first/`) are Douze's too. Its templates do not carry over: Hugo uses Go
templates, Douze uses Liquid, and Douze has no template lookup by kind, section or type, so every layout is rewritten
and named by the pages that use it. Hugo builds more things in (taxonomy pages, feeds, sitemaps, Sass, asset bundling,
aliases); in Douze some of them are templates or command steps, and some are missing.

## Concepts

| Hugo | Douze |
|---|---|
| `hugo.toml` / `hugo.yaml` | `douze.yml` ([Configuration](../guide/configuration.md)) and `_data/site.yml` for values |
| `baseURL` | `site.url` |
| `params` | any data file: `data/site.yml` with `params:` is `site.params` |
| `content/` | `site.content: content` |
| `layouts/` | `site.includes: layouts`, with Liquid files |
| `layouts/shortcodes/` | `layouts/shortcodes/` when `site.includes` is `layouts` (or `site.shortcodes`) |
| `static/` | move its files into the content folder: files no step claims are copied |
| `data/` | `site.data: data` (JSON, YAML, XML; not TOML or CSV) |
| `assets/`, Hugo Pipes | files in the content folder; processing with [command steps](../guide/command-steps.md), `tool: lightningcss` |
| `i18n/en.toml` | `_data/i18n/en.yml` and the `t` filter ([Internationalization](../guide/internationalization.md)) |
| `archetypes/`, `hugo new` | none |
| `themes/`, modules | none: copy the theme's files into the site |
| `public/` | `-o public` or `site.output: public` (default `_site`) |
| `hugo server` (port 1313) | `douze serve` (port 8080, `--port`) |
| TOML (`+++`) or JSON front matter | YAML (`---`) only |
| `.Title`, `.Params.x` | `title`, `x` |
| `.Site.Title`, `.Site.Params.x` | `site.title`, `site.params.x` from `data/site.yml` |
| `.Site.Data.x` | `x` |
| `.Content` in `single.html` | `content` in a layout |
| `.Permalink`, `.RelPermalink` | `url \| replace: 'index.html', '' \| absolute_url`, `url` (with `index.html`) |
| `.Date`, `.Lastmod` | `date` (front matter), `page.date` (the file's modification time) |
| `baseof.html` with `block` and `define` | a layout chain: a layout's front matter names its own layout, which receives `content` |
| `partial "header.html" .` | `{% include 'header.html' %}` (the page's variables are visible) |
| `{{< figure src="x.png" >}}`, `{{% note %}}` | `{% figure src: "x.png" %}`, `{% note %}...{% endnote %}` with shortcode files |
| Template lookup (`_default/single.html`, `posts/list.html`) | `layout` in front matter or in the folder's data file |
| `_index.md` (a section's list page) | `index.md`; its front matter overrides the folder's data |
| Page bundles (`posts/x/index.md` and its images) | the same: the page is `/posts/x/` and its files are copied next to it |
| `range .Pages`, sections | `collections.<tag>`: a data file in the folder sets `tags` |
| Taxonomies, term pages | `tags` and `collections.<tag>`; term pages with pagination over a list of tags |
| `weight` ordering | `sort_by: "data.weight"` |
| `.Paginate` | `pagination` front matter ([Pagination](../guide/pagination.md)) |
| `[permalinks]` | a `permalink` template in the folder's data file ([Routing](../guide/routing.md)) |
| `slug`, `url` front matter | `permalink` |
| `aliases` | none |
| `draft: true` (skipped unless `-D`) | `permalink: false` |
| `.Summary`, `<!--more-->` | `{{ content \| excerpt }}` |
| `.TableOfContents` | the `headings` step and the `toc` filter |
| `.WordCount`, `.ReadingTime` | Liquid ([reading time](../recipes/reading-time.md)) |
| `ref`, `relref` | relative links to `.md` files, rewritten to the page's url |
| `relURL`, `absURL` | `relative_url`, `absolute_url` |
| Menus | a sidebar data file and the `nav_*` filters ([Navigation](../guide/navigation.md)) |
| Image processing (`.Resize`, `.Fill`) | the `image` tag, `image_url` ([Images](../guide/images.md)) |
| Chroma highlighting | the [`highlight` step](../guide/syntax-highlighting.md) (TextMate grammars) |
| Goldmark (GFM extensions on) | Markdig: enable them in `markdown` |
| Built-in RSS, sitemap, robots.txt | templates ([RSS](../recipes/rss.md), [sitemap](../recipes/sitemap.md)) |
| Multilingual sites | `site.locales` and the `localize` step |
| `getJSON`, `resources.GetRemote` | data written before the build ([Data files](../guide/data.md)) |

## Differences to watch for

- **Go template syntax in content.** Douze renders Markdown pages as Liquid before Markdown. A Hugo shortcode call
  left in a page, `{{< figure >}}`, is a Liquid syntax error (`A value was expected`). Convert every shortcode call
  before the first build, or set `liquid: false` on the pages you have not ported yet.
- **No lookup order.** Hugo picks `posts/single.html`, then `_default/single.html`. Douze renders the layout a page
  names. Set `layout` once per folder in a data file (`content/posts/posts.yml`).
- **Drafts are published.** Douze has no `draft` key: a draft page needs `permalink: false`, which also leaves it out
  of the collections.
- **Lists are built from tags.** A section has no list of its pages: tag them with the folder's data file and loop over
  `collections.<tag>`.
- **Encoding.** Hugo's `html/template` escapes values for their context. Douze encodes Liquid output for the type
  of the file: HTML in pages, XML in feeds, JSON strings in `.json` files. In a page, a value that holds HTML needs
  `raw`. See [Liquid output is encoded](index.md#liquid-output-is-encoded).

## Port a site, step by step

The example is a small Hugo site: posts in `content/posts/`, one of them a page bundle, a `youtube` shortcode, a
`baseof.html` and the `single` and `list` templates.

### 1. Point Douze at Hugo's folders

```yaml title="douze.yml"
site:
  content: content
  data: data
  includes: layouts
  url: https://example.org/

markdown:
  extensions: [pipetables, strikethrough, tasklists, autolinks, footnotes, definitionlists, attributes]
```

```yaml title="data/site.yml"
title: My Hugo site
params:
  description: Notes and posts
```

Move the files of `static/` into `content/`. Then move Hugo's templates out of `layouts/` (to `hugo-layouts/`, for
instance) and bring them back one by one as you rewrite them: every file of `layouts/shortcodes/` is parsed as a Liquid
shortcode before the build starts, so a Go template left there stops the build
(`/layouts/shortcodes/youtube.html(1,16): error: A value was expected`).

### 2. Convert front matter to YAML

```markdown
+++
title = "First post"
date = 2024-05-01
+++
```

becomes

```markdown
---
title: First post
date: 2024-05-01
---
```

### 3. Rewrite the templates in Liquid

`baseof.html` and its `block "main"` become a base layout that writes `content`, and `single.html` a layout whose front
matter names the base:

```liquid title="layouts/baseof.liquid"
<!doctype html>
<html lang="en">
<head><title>{{ title }} | {{ site.title }}</title><meta name="description" content="{{ site.params.description }}"></head>
<body>{{ content }}</body>
</html>
```

```liquid title="layouts/single.liquid"
---
layout: baseof
---
<article><h1>{{ title }}</h1>{{ content }}</article>
```

```liquid title="layouts/list.liquid"
---
layout: baseof
---
<h1>{{ title }}</h1>
<ul>
{%- assign posts = collections.posts | sort_by: "data.weight" %}
{%- for p in posts %}
  <li><a href="{{ p.data.url | replace: 'index.html', '' | relative_url }}">{{ p.data.title }}</a> {{ p.data.date | date: "%Y-%m-%d" }}</li>
{%- endfor %}
</ul>
```

### 4. Replace the template lookup with data files

A data file in the section folder gives its pages a layout and a tag. The section's `_index.md` becomes `index.md`,
with its own layout and no tag:

```yaml title="content/posts/posts.yml"
layout: single
tags: [posts]
```

```markdown title="content/posts/index.md"
---
title: Posts
layout: list
tags: []
---
```

`content/posts/second-post/index.md`, a page bundle, is written to `/posts/second-post/index.html`, and its
`photo.png` next to it, so `![A photo](photo.png)` keeps working.

### 5. Convert the shortcodes

`layouts/shortcodes/youtube.html`, which reads `{{ .Get 0 }}`, becomes

 `layouts/shortcodes/youtube.liquid`:

```liquid title="layouts/shortcodes/youtube.liquid"
---
args: [id]
---
<iframe src="https://www.youtube-nocookie.com/embed/{{ id }}" title="YouTube video" allowfullscreen></iframe>
```

and the call in a page changes from Hugo's syntax to a Liquid tag:

```markdown
Hello. {% youtube "dQw4w9WgXcQ" %}
```

A shortcode with a body (`{{% note %}}...{{% /note %}}`) sets `block: true` and writes `body`. See
[Includes and shortcodes](../guide/shortcodes.md).

### 6. Build and compare

```bash
hugo -d old
douze -o public --plain
diff -r old public
```

Hugo writes feeds, a sitemap and taxonomy pages that Douze does not: add the templates you need, and set redirects for
`aliases` on the web server.

## See also

- [Migration overview](index.md)
- [Layouts](../guide/layouts.md)
- [Includes and shortcodes](../guide/shortcodes.md)
- [Collections](../guide/collections.md)
- [Data files](../guide/data.md)
- [Images](../guide/images.md)
- [douze.yml reference](../reference/configuration.md)
