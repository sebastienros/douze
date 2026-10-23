---
title: Customize output paths
description: Change where pages are written with permalink, in front matter or for a whole folder, with dates and slugs in paths.
liquid: false
---

The `permalink` step writes each file to an output path and sets its `url`. Without a `permalink`, the path follows the
source file: `blog/post.md` is written to `blog/post/index.html`, `index.md` stays `index.html`, and
`feed.xml.liquid` is `feed.xml`. A `permalink` value overrides that path. It is a Liquid template rendered with the
page's data, and it can come from front matter, from folder data, or from site data.

## Set the path of one page

| Front matter | Output file | `url` |
|---|---|---|
| `permalink: /about/` | `about/index.html` | `/about/index.html` |
| `permalink: /about` | `about/index.html` | `/about/index.html` |
| `permalink: /legacy.html` | `legacy.html` | `/legacy.html` |
| `permalink: /api/{{ name }}.json` | `api/status.json` (with `name: status`) | `/api/status.json` |
| `permalink: false` | nothing: the page is not written | |

A path without an extension is a folder, which gets an `index.html`. The content folder (`_content`) is never part of
the output path.

```md
---
title: About us
permalink: /about/
---
About
```

## Set the paths of a folder

A data file in a folder applies its keys to every page of the folder and its subfolders, so a `permalink` there is a
pattern for all of them. Each page renders it with its own data:

```yaml title="_content/posts/posts.yml"
permalink: "/blog/{{ date | date: '%Y/%m' }}/{{ title | slug }}/"
```

```md
---
title: Hello, World & More!
date: 2026-03-14
---
Hi
```

`_content/posts/2026-03-14-hello.md` is written to `blog/2026/03/hello-world-more/index.html`. A page of the folder
that sets its own `permalink` keeps it: the page's data comes before its folder's.

:::note[No file name variable]
Douze has no variable for the source file name (Eleventy's `page.fileSlug`). A pattern builds the path from the
page's data, such as `title | slug`, or from a `slug` that each page sets in its front matter.
:::

## Write files that are not HTML

A file whose name ends in `.liquid` is rendered as a template and written without `.liquid`, which is how to publish
JSON, XML or text built from data:

```liquid
---
permalink: /api/{{ name }}.json
name: status
---
{ "name": "{{ name }}" }
```

Name it `_content/status.json.liquid`, not `status.json`: the `.json`, `.yml`, `.yaml` and `.xml` files of the content
folder are folder data, not pages.

## Read the url in templates

`url` is the output path, with `index.html`. Links to folder URLs remove it:

```liquid
<a href="{{ post.data.url | replace: 'index.html', '' }}">{{ post.data.title }}</a>
```

On a site served under a path, such as a GitHub Pages project site, `relative_url` adds the path of `site.url`:
see [Deploy to GitHub Pages](github-pages.md).

## Change the output folder

The output folder is `_site` in the source folder. `-o` changes it for one build, and `site.output` in `douze.yml`
for every build (`-o` wins):

```yaml
site:
  output: public
```

## See also

- [Routing and permalinks](../guide/routing.md)
- [Pagination](../guide/pagination.md), for the paths of paginated pages
- [permalink step](../reference/steps/permalink.md)
- [Template data](../reference/template-data.md)
- [douze.yml reference](../reference/configuration.md)
