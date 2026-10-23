---
title: Template data
description: Every value Douze templates can read, which step sets it, and the order names resolve in.
liquid: false
---

A template reads names: `title`, `page.date`, `collections.post`, `site.url`. A page's data is its front matter plus
the values that steps set on it, and a name that the page does not define resolves through its folder data, the site
data and the `site` section of `douze.yml`. This page lists every value Douze provides, and the step that provides it.

## Name resolution

A name in a page, its layouts and the includes and shortcodes they use resolves to the first of:

1. the page's own data: its front matter, the values steps set (`page`, `url`, `pagination`, `headings`...), and the
   variables it captured;
2. the data of its folder, then of each parent folder up to the root (folder data files, from a `data` step with
   `scope: folder`);
3. the site data (the files of the data folder, from a `data` step with `scope: site`) and the values that reduce steps
   publish (`collections`, `images`);
4. the `site` section of `douze.yml` (`site.url`, `site.locales`);
5. in a layout, the front matter of the layouts, the innermost first.

The first scope that defines a name wins, with one exception: when site data defines `site` as an object
(`_data/site.json`), its keys are layered over the `site` values of `douze.yml`, so `site.title` comes from the data
file and `site.url` from `douze.yml` unless the data file defines `url` too.

A name that resolves to nothing is nil, or an error with the liquid step's `strict: true`
(`undefined variable 'name'`). A page depends only on the values it read: a data file change renders again the pages
that read what changed.

Front matter keys are free: `title`, `description`, `tags`, `date`, `draft` mean nothing to Douze unless a step or a
template reads them. The keys that steps read are listed in [Keys steps read](#keys-steps-read).

## page

| Name | Set by | Value |
|---|---|---|
| `page.date` | [frontmatter](steps/frontmatter.md) | The last write time of the source file, in UTC. |
| `page.heading` | [headings](steps/headings.md) | The text of the page's first `h1`. |

A `page:` object in front matter is merged into `page` rather than replacing it.

```liquid
<time datetime="{{ page.date | date_to_xmlschema }}">{{ page.date | date: "%B %-d, %Y" }}</time>
```

## url

`url`, set by [permalink](steps/permalink.md) (and [paginate](steps/paginate.md) for each page of a paginated
template), is the output path of the page, from the root of the output folder: `/blog/post/index.html`. A Markdown page
already has its HTML url before the markdown step runs. Remove `index.html` for pretty links:

```liquid
<a href="{{ url | replace: 'index.html', '' }}">{{ title }}</a>
```

## content

| Where | `content` is |
|---|---|
| A layout | The page's HTML, or the inner layout's result. `{{ content }}` writes it as it is. |
| A collection item, with `collect` `content: true` | The page's text at that point of the workflow. |
| A `fork` step's `content` template | The file's text. |
| A `liquid` step `template` | `file.content`, see [liquid step](steps/liquid.md). |

A page does not see its own content as `content`.

## collections

Published by the [collect step](steps/collect.md) (`collections` by default, or its `name`):

| Name | Value |
|---|---|
| `collections.all` | Every page the step consumed. |
| `collections.<tag>` | The pages whose `tags` contain the tag. |
| `collections.urls` | Every page by its url without `index.html`: `collections.urls["/blog/post/"]`. |

Each item has `data`, the page's data at the point of the workflow where `collect` runs (`item.data.title`,
`item.data.url`, `item.data.page.date`), and with `content: true`, `content`. Items are sorted by `date` (front matter,
or `page.date`), then by source path. A paginated page is in the collections once, with the url of its first page.

```liquid
{% for post in collections.post reversed %}
  <a href="{{ post.data.url | replace: 'index.html', '' }}">{{ post.data.title }}</a>
{% endfor %}
```

## pagination

Set on each page of a paginated template by the [paginate step](steps/paginate.md):

| Name | Value |
|---|---|
| `pagination.items` | The items of the page. |
| `pagination.pageNumber` | The index of the page, from 0. |
| `pagination.hrefs` | The url of every page. |
| `pagination.pages` | Every page, each `{ items, pageNumber, href }`. |
| `pagination.page.next`, `pagination.page.previous` | The neighbor pages, each `{ items, pageNumber, href }`; nil at the ends. |
| `pagination.page.first`, `pagination.page.last` | The first and last pages. |
| the `alias` | The items of the page under that name (the item itself when `size` is 1). |

The front matter keys `pagination.data`, `size`, `alias` and `resolve` configure it; see the step.

## site

| Name | Value |
|---|---|
| `site.url` | `site.url` of `douze.yml`. |
| `site.locales` | With `site.locales` in `douze.yml`: `default`, `fallback`, `strings` and `languages`, the list of every locale (`locale`, `label`, `lang`, `dir`, `prefix`), the default first. |
| `site.<key>` | Any key of a `site` data file (`_data/site.json`). |

Every other file of the data folder is a top-level name: `_data/colors.json` is `colors`,
`_data/pkgs/Contoso.Hosting.json` is `pkgs["Contoso.Hosting"]`. See the [data step](steps/data.md).

## headings and toc

Set by the [headings step](steps/headings.md):

| Name | Value |
|---|---|
| `headings` | Every heading of the page, in order: `depth` (1 to 6), `text` and `id`. |
| `toc` | The headings of the step's `levels` (default `[2, 3]`) as a tree: each entry has `depth`, `text`, `id` and `children`. |
| `page.heading` | The text of the first `h1`. |

```liquid
{{ toc | toc: class: "toc", max-depth: 3 }}
```

## Locale values

Set by the [localize step](steps/localize.md) on every page under the locale root:

| Name | Value |
|---|---|
| `locale` | The locale key: `ja`, or the default locale's. |
| `lang` | The locale's `lang`. |
| `dir` | `ltr` or `rtl`. |
| `locale_url` | The url without its locale prefix and `index.html`: `/guide/`. |
| `alternates` | `{ locale, lang, url }` of every locale that has a page at `locale_url`, the default first. |
| `fallback` | `true` on a copy of a default-locale page under a locale that does not translate it. |
| `original_url` | On such a copy, the url of the default locale's page. |

The `t` filter reads the page's `locale` and the strings of `site.locales.strings`
(`i18n.<locale>`). See [Internationalization](../guide/internationalization.md).

## Image data

| Name | Set by | Value |
|---|---|---|
| `image` | [images](steps/images.md), on each file it emits | `{ source, url, width, height, format, variants }`, `variants` being a list of `{ url, width, height, format }`. |
| `images` | [image-map](steps/image-map.md), as site data | The `image` values by source url: `images["/docs/images/x.png"].width`. |

## Shortcode values

A [shortcode](liquid.md#shortcodes) template sees, before the page's names:

| Name | Value |
|---|---|
| `args` | The positional arguments, as a list. |
| each name of `args` (front matter) | The positional argument at that position. |
| each named argument | Its value. A name listed in `params` and not passed is nil. |
| `body` | Paired shortcodes: the body, rendered as Markdown to HTML in a Markdown page. |
| `source` | Paired shortcodes: the body before Markdown. |

## Template steps and groups

A [liquid step](steps/liquid.md) with a `template`, and its `permalink`, see:

| Name | Value |
|---|---|
| `file` | Without `group-by`: `path`, `name`, `dir`, `ext`, `content` (nil for a binary file) and `data`, the file's data. |
| `group` | With `group-by`: `key` and `files`, a list of the same objects as `file`. |

A `group-by` template renders with `file` (`path`, `dir`, `name`, `ext`) and `data`, the file's data. See
[douze.yml](configuration.md#group-by).

Command steps render `run` and `env` with their own values (`step`, `site`, `cwd`, `inputs`, `outputs`, `file`,
`tool`): see [Command steps and workers](commands.md#template-values).

## Keys steps read

| Key | Read by | Effect |
|---|---|---|
| `permalink` | [permalink](steps/permalink.md), [paginate](steps/paginate.md) | The output path, a template; `false` drops the file. |
| `tags` | [collect](steps/collect.md) | The collections of the page. |
| `date` | [collect](steps/collect.md) | The sort date; `page.date` otherwise. |
| `pagination` | [paginate](steps/paginate.md) | Paginates the page. |
| `layout` | [layout](steps/layout.md) | The layout file of the includes folder. |
| `liquid` | [liquid](steps/liquid.md) | `false` leaves the page out of Liquid. |
| `markdown` | [markdown](steps/markdown.md) | `false` leaves the page as Markdown. |
| `localize` | [localize](steps/localize.md) | `false` leaves the page as it is. |

Each one resolves like any name, so folder or site data can set it for many pages: a `blog/blog.yml` with
`layout: post` and `tags: post` applies to every page of `blog/`.
