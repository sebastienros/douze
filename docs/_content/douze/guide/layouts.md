---
title: Layouts
description: Wrap pages in Liquid layouts, chain layouts, and set the layout of a page, a folder or the whole site.
liquid: false
---

A layout is a Liquid template that wraps a page: the page's HTML goes where the layout writes `{{ content }}`. Layouts
hold what pages share, such as the `<head>`, the navigation and the footer, so that a page only holds its own content.
A layout can have a layout of its own, which makes a chain from the most specific layout to the base one.

## A first layout

Layouts are files of the includes folder (`_includes`):

```liquid title="_includes/base.liquid"
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>{{ title }} | {{ site.title }}</title>
  <link rel="stylesheet" href="/css/site.css">
</head>
<body>
  {% include "header" %}
  <main>{{ content }}</main>
</body>
</html>
```

A page names it in its front matter:

```md title="_content/about.md"
---
title: About
layout: base.liquid
---
# About

This site is built with Douze.
```

- `layout` is a path in the includes folder: `base.liquid`, `layouts/post.html`. A name without an extension is tried
  as it is, then with `.liquid`, then with `.html`, so `layout: base` works too.
- The layout is rendered with the page's data (`title`, `url`, `page`, `collections`, site data) and `content`, the
  HTML of the page.
- Values are HTML-encoded (`Tom & Jerry` is written `Tom &amp; Jerry`); `content` is written as it is.
- Layouts apply to HTML files only: a Markdown page gets its layout after it is rendered to HTML, and feeds or JSON
  files get none.

## Chain layouts

A layout's front matter can name another layout. The page is rendered into the first layout, and the result into the
next one:

```liquid title="_includes/post.liquid"
---
layout: base.liquid
---
<article>
  <h1>{{ title }}</h1>
  <time datetime="{{ date | date: '%Y-%m-%d' }}">{{ date | date: "%B %d, %Y" }}</time>
  {{ content }}
</article>
```

A post with `layout: post.liquid` is wrapped in `<article>`, then in the HTML document of `base.liquid`. A chain is at
most 32 layouts long.

## Layout front matter

The front matter of a layout provides default values to the layouts of the chain. A page's own data wins, then its
folder and site data, then the front matter of its layouts, the innermost first:

```liquid title="_includes/post.liquid"
---
layout: base.liquid
section: Blog
---
```

`{{ section }}` is `Blog` in `post.liquid` and in `base.liquid`, unless the page or its folder data sets `section`.
The page's own Liquid does not see it: the page is rendered before its layouts.

## Set the layout of a folder or a site

`layout` resolves like any other value, so a folder data file sets it for every page of a folder and its subfolders:

```yaml title="_content/blog/blog.yml"
layout: post.liquid
tags: post
```

And a site data file named `layout` sets it for the whole site:

```yaml title="_data/layout.yml"
base.liquid
```

A page overrides either with its own `layout`, and `layout: false` renders a page without layout:

```html title="_content/standalone.html"
---
layout: false
---
<!doctype html>
<html><body>A page that writes its own document.</body></html>
```

## Includes in layouts

Layouts use includes, shortcodes and every tag and filter of pages. `{% include "header" %}` renders
`_includes/header.liquid` with the page's variables. See [Includes and shortcodes](shortcodes.md).

To keep the layouts apart from the includes, use a folder: `layout: layouts/base.liquid`.

## Markdown in layouts

A layout is HTML, not Markdown. To render a Markdown value, such as a description in front matter, use the
`markdownify` filter, or the `markdown` tag for a block:

```liquid
<p class="lead">{{ description | markdownify: inline: true }}</p>
{% markdown %}
**Note:** this page is generated.
{% endmarkdown %}
```

## When a layout is missing

A layout that does not exist fails the page, with the name it looked for and the page:

```txt
error: layout: /about/index.html: layout 'bas.liquid' not found in '/_includes/' (used by '/_content/about.md')
```

## See also

- [layout step](../reference/steps/layout.md)
- [Includes and shortcodes](shortcodes.md)
- [Data files](data.md)
- [Template data](../reference/template-data.md)
- [Liquid filters and tags](../reference/liquid.md)
