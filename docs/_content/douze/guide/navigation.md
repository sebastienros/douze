---
title: Navigation and sidebars
description: Describe a sidebar as data and render it, with the active page, previous and next links, and breadcrumbs.
liquid: false
---

Douze has no built-in menu. A sidebar, a top navigation or a footer menu is site data, a YAML file of `_data`, and a
layout renders it. The `nav_*` filters do the work that every layout would otherwise repeat: find the current page in
the sidebar, its previous and next pages, and the path to it.

## A sidebar as data

A sidebar is a list of items. An item is a URL, or an object with a `label`, a `link` and `items` (a group). It can
have any other key the layout reads, such as `icon`, `badge`, `collapsed` or `external`.

```yaml title="_data/sidebar.yml"
- label: Start
  items:
    - label: Install
      link: /docs/install/
    - /docs/first-site/ # labeled with the page's title
- label: Guides
  items:
    - /docs/layouts/
    - label: GitHub
      link: "https://github.com/me/site"
      external: true
```

An item that is only a URL gets the title of the page at that URL, from `collections.urls`, as its label. A `label`
can also be an object of labels by locale (`{ en: Guides, fr: Guides pratiques }`), see
[Internationalization](internationalization.md).

## Render it

```liquid title="_includes/doc.liquid"
{%- assign nav = sidebar | nav_active: url -%}
<nav>
  <ul>
  {%- for group in nav %}
    <li>{{ group | nav_label }}
      <ul>
      {%- for item in group.items %}
        <li><a href="{{ item.link }}"{% if item.active %} aria-current="page"{% endif %}>{{ item | nav_label }}</a></li>
      {%- endfor %}
      </ul>
    </li>
  {%- endfor %}
  </ul>
</nav>
<main>{{ content }}</main>
```

`nav_active: url` returns the sidebar with `active: true` on the item of the page and `open: true` on the groups that
contain it, so a layout can expand the current group. `nav_label` returns the label of an item in the page's locale.

## Previous and next

```liquid
{%- assign pn = sidebar | nav_prev_next: url %}
<footer>
  {% if pn.prev %}<a href="{{ pn.prev.link }}">{{ pn.prev | nav_label }}</a>{% endif %}
  {% if pn.next %}<a href="{{ pn.next.link }}">{{ pn.next | nav_label }}</a>{% endif %}
</footer>
```

`nav_prev_next` returns `{ prev, next }`: the items next to the page's in the same group. On the last page of a group,
`next` is empty. When the page is not in the sidebar, the result is nil. To link across groups, flatten the sidebar
first: `sidebar | nav_flatten` lists every item with a link and no items, in order.

Front matter `prev: false` or `next: false` has no effect by itself; the layout reads it to hide a link:

```liquid
{% if pn.next and next != false %}...{% endif %}
```

## Breadcrumbs

```liquid
{%- assign crumbs = sidebar | nav_breadcrumbs: url %}
<p class="breadcrumbs">
  {%- for item in crumbs %}{{ item | nav_label }}{% unless forloop.last %} / {% endunless %}{% endfor -%}
</p>
```

`nav_breadcrumbs` returns the items from the top of the sidebar to the page's: `Start / Install`. `nav_section`
returns the top-level item that contains the page, for a site with one sidebar per section.

## How URLs are matched

- Links and URLs are compared without `index.html`, so `url` (`/docs/install/index.html`) matches
  `/docs/install/`.
- On a multilingual site, the locale prefix of the URL (`/ja/docs/install/`) is removed when no link of the sidebar
  has one, so one sidebar serves every locale.
- Each sidebar is indexed once per build, however many pages render it.

A page that renders a sidebar depends on all of it: editing `sidebar.yml` renders every such page again.

## Find a page by URL

`collections.urls` holds every page by URL, without `index.html`. A layout can read another page's data from a link:

```liquid
{%- assign target = collections.urls["/docs/layouts/"] %}
<a href="/docs/layouts/">{{ target.data.title }}</a>
```

The layout must run in a step that needs `collect` (the default workflow's `layout` does).

## Several menus

Each menu is its own data file: `_data/sidebar.yml`, `_data/topnav.yml`, `_data/footer.yml`. The filters work on any
of them. For a sidebar per section, a page can name its sidebar in front matter or folder data (`section: guides`)
and the layout picks it from a data file that holds all of them (`_data/sidebars.yml`):
`{% assign nav = sidebars[section] | nav_active: url %}`.

## See also

- [Liquid filters and tags](../reference/liquid.md)
- [Layouts](layouts.md)
- [Data files](data.md)
- [Collections](collections.md)
- [Internationalization](internationalization.md)
