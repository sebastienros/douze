---
title: localize
description: "Sets the locale and alternate urls of pages, and copies untranslated pages under every locale."
---
The `localize` step gives every page under the locale root its locale, and the urls of the same page in the other
locales, for `hreflang` links and language switchers. With `site.locales.fallback: true`, it also copies the pages of
the default locale under every locale that does not translate them. It needs
[`site.locales`](../configuration.md#sitelocales) in `douze.yml`.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | `**/*.html`, `**/*.md` |
| Default workflow | no |

## What it does

The step runs after [`permalink`](permalink.md) (it reads `url`) and
[`collect`](collect.md) (it reads `collections.urls`). A page of a locale folder under the locale root
(`<root>/ja/`) is a page of that locale; any other page under the root is a page of the default locale. Pages outside
the root, and pages whose data sets `localize: false` (a paginated template whose pages are not per locale, for
instance), pass through unchanged.

It sets on each page:

| Key | Value |
|---|---|
| `locale` | the locale key: `ja`, or the default locale's (`en`) |
| `lang` | the locale's `lang` (default: its key) |
| `dir` | the locale's `dir`: `ltr` (default) or `rtl` |
| `locale_url` | the page's url without its locale prefix and trailing `index.html`: `/ja/guide/index.html` is `/guide/` |
| `alternates` | `{ locale, lang, url }` for each locale that has a page at `locale_url`, the default locale first, the page's own included |

`locale_url` and `alternates` are set only when the page has a `url`.

### Fallback copies

With `fallback: true`, a page of the default locale is also copied under each locale that has no page at its url. A
copy has the same content and data, with:

| Key | Value |
|---|---|
| `locale`, `lang`, `dir` | the locale's |
| `url` | the url with the locale prefix: `/ja/guide/index.html` |
| `fallback` | `true` |
| `original_url` | the url of the default locale's page |

The copy is written under the locale prefix (`/ja/guide/index.md`), so the layout can render it in the locale with a
notice. Copies are not in the collections, which are built before. Every url of the default locale is then served in
every locale, so the `alternates` of its pages, and of their copies, list every locale.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `collections` | string | `collections` | The name of the site value of the `collect` step whose `urls` the step reads (the `name` of that step) |

The locales themselves are the `site.locales` settings: `default`, `root`, `fallback`, `strings` and `languages`.
See [douze.yml](../configuration.md#sitelocales).

## Caching

A page depends on the urls it looked for in `collections.urls` only: adding `docs/ja/guide.md` runs `localize` again
for the pages of `/guide/`, not for the others. A page and its fallback copies differ only by their locale keys and
path: a later step that does not read them renders the page once for all of them. Changing `site.locales` runs the
step again for every page.

## Example

```yaml
site:
  content: docs
  locales:
    default: en
    fallback: true
    languages:
      en:
        label: English
      ja:
        label: 日本語

steps:
  pages:
    inputs: "docs/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  collections:
    needs: permalink
    uses: collect
  localize:
    needs: [permalink, collections]
    uses: localize
  render:
    needs: localize
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  write:
    needs: markdown
    uses: write
```

With `docs/index.md`, `docs/guide.md` and `docs/ja/index.md`, the build writes `index.html`, `guide/index.html`,
`ja/index.html` and `ja/guide/index.html`, a copy of the English guide with `fallback: true`. A layout lists the
alternates:

```liquid
{% for a in alternates %}
<link rel="alternate" hreflang="{{ a.lang }}" href="{{ a.url | absolute_url }}">
{% endfor %}
```

## Related

- [Internationalization](../../guide/internationalization.md)
- [douze.yml: locales](../configuration.md#sitelocales)
- [Liquid filters](../liquid.md): `t`, `locale_url`, `locale_of`, `locale_pages`
- [collect](collect.md)
