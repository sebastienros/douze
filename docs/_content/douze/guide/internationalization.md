---
title: Internationalization
description: Build a site in several languages, with locale folders, translated UI strings, language switchers and fallback pages.
liquid: false
---

A multilingual Douze site keeps each language in a folder: `docs/guide.md` is the default language, `docs/fr/guide.md`
its French translation. `site.locales` in `douze.yml` declares the languages, the `localize` step tells each page its
locale and its translations, and the `t` filter translates the strings of the layouts. Untranslated pages can be
copied under every locale, so that every URL exists in every language.

## Declare the locales

```yaml title="douze.yml"
site:
  url: https://example.com
  locales:
    default: en # the locale whose pages have no URL prefix
    root: docs # the folder whose subfolders are locales; default: the content folder
    fallback: true # copy untranslated pages under every locale (default false)
    strings: i18n # the site data of the UI strings (default i18n)
    languages:
      en:
        label: English
      fr:
        label: Français
      ar:
        label: العربية
        lang: ar
        dir: rtl
```

- The keys of `languages` are the locales, and the names of their folders. `lang` defaults to the key and `dir` to
  `ltr`.
- A page under `root/fr/` is a page of `fr`; any other page under `root` is a page of the default locale.
- `root` is relative to the content folder. Pages outside it have no locale.

Templates see these settings as `site.locales`, and `site.locales.languages` is the list of every locale, the default
first, each with `locale`, `label`, `lang`, `dir` and `prefix`.

## URLs

`permalink` removes `root` from paths and keeps the locale folder:

| Source | URL |
|---|---|
| `_content/docs/guide.md` | `/guide/` |
| `_content/docs/fr/guide.md` | `/fr/guide/` |
| `_content/docs/fr/index.md` | `/fr/` |

## Add the `localize` step

The default workflow does not localize pages: a multilingual site lists its steps, with `localize` after `collect`:

```yaml title="douze.yml"
steps:
  site-data:
    inputs: "_data/**/*.{yml,yaml,json}"
    uses: data
    with:
      scope: site
  pages:
    inputs: "_content/**"
    needs: site-data
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
  layout:
    needs: markdown
    uses: layout
  write:
    needs: layout
    uses: write
```

`localize` sets on every page under `root`:

| Key | Value |
|---|---|
| `locale` | `fr` |
| `lang`, `dir` | `fr`, `ltr` |
| `locale_url` | The page's URL without its locale prefix: `/guide/` |
| `alternates` | `{ locale, lang, url }` of each locale that has a page at that URL |

A page depends only on the URLs it looked for: adding `docs/fr/guide.md` runs `localize` again for `/guide/`, not for
every page.

## Translate the UI strings

The strings of the layouts live in site data, one file per locale, under the name of `strings`:

```yaml title="_data/i18n/en.yml"
nav:
  home: Home
untranslated: This page is not translated yet.
results: "{count} results"
```

```yaml title="_data/i18n/fr.yml"
nav:
  home: Accueil
untranslated: Cette page n'est pas encore traduite.
results: "{count} résultats"
```

The `t` filter returns the string in the page's locale, else in the default locale's, else the key itself, with a
warning. Keys are dotted paths, or keys with dots. Named arguments replace placeholders:

```liquid
{{ "nav.home" | t }}
{{ "results" | t: count: 3 }}
```

## Language switchers and `hreflang`

```liquid title="_includes/base.liquid"
<html lang="{{ lang }}" dir="{{ dir }}">
<head>
{%- for alt in alternates %}
  <link rel="alternate" hreflang="{{ alt.lang }}" href="{{ alt.url | absolute_url }}">
{%- endfor %}
</head>
<body>
  <a href="{{ '/' | locale_url: locale }}">{{ "nav.home" | t }}</a>
  {% if fallback %}<p>{{ "untranslated" | t }}</p>{% endif %}
  {{ content }}
  <ul>
  {%- for l in site.locales.languages %}
    <li><a href="{{ locale_url | locale_url: l.locale }}" lang="{{ l.lang }}">{{ l.label }}</a></li>
  {%- endfor %}
  </ul>
</body>
</html>
```

| Filter | Result |
|---|---|
| `url \| locale_url: "fr"` | The URL in another locale, whether or not a page exists there: `/fr/guide/`. Without an argument, the URL without its prefix. |
| `url \| locale_of` | The locale of a URL |
| `collections.all \| locale_pages` | Every page of every locale, fallback copies included, as `{ locale, lang, url, fallback, data }`: for sitemaps |
| `item \| nav_label` | The label of a sidebar item in the page's locale |

The switcher above links every locale. To link only the translations that exist, loop over `alternates` instead.

## Fallback pages

With `fallback: true`, a page of the default locale that a locale does not translate is copied under that locale,
with its `locale`, `lang`, `dir` and URL, plus `fallback: true` and `original_url`. The layout renders it in the
locale, with a notice such as the one above. Every URL of the default locale then exists in every locale, so
language switchers never lead to a missing page.

Fallback copies are not in the collections. `locale_pages` includes them, for a sitemap of every URL.

## Sidebars in several languages

A sidebar item's `label` can be an object of labels by locale, and `nav_label` picks the page's. Links are written
once, without a prefix: the filters remove the locale prefix of the page's URL when no link of the sidebar has one.

```yaml title="_data/sidebar.yml"
- label:
    en: Guides
    fr: Guides pratiques
  items:
    - /guide/
```

## See also

- [localize step](../reference/steps/localize.md)
- [douze.yml reference](../reference/configuration.md)
- [Liquid filters and tags](../reference/liquid.md)
- [Navigation and sidebars](navigation.md)
- [Routing and permalinks](routing.md)
