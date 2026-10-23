---
title: Liquid filters and tags
description: Every Liquid filter and tag Douze adds to Fluid, with its arguments, result and an example.
liquid: false
---

Douze renders templates with [Fluid](https://github.com/sebastienros/fluid), a .NET implementation of
[Liquid](https://shopify.github.io/liquid/). Every standard Liquid tag and filter works as Fluid implements it:
`if`, `for`, `assign`, `capture`, `include`, `render`, `raw`, `date`, `map`, `where`, `sort`, `join`, `replace`,
`size`, `default` and the others. See [Fluid's documentation](https://github.com/sebastienros/fluid#readme) for those.
This page lists what Douze adds or changes.

The values templates see are on the [template data](template-data.md) page. `include` and `render` load files from
the includes folder, with `.liquid` as the default extension.

## Filters

| Filter | Group | Result |
|---|---|---|
| [`slug`](#slug) | Text | A url-safe slug. |
| [`markdownify`](#markdownify) | Markdown | Markdown as HTML. |
| [`strip_markdown`](#strip_markdown) | Markdown | The text of Markdown. |
| [`excerpt`](#excerpt) | Markdown | The first paragraph, or what comes before `<!-- more -->`, as HTML. |
| [`where_exp`](#where_exp) | Data | The items for which an expression is true. |
| [`group_by_exp`](#group_by_exp) | Data | The items grouped by the value of an expression. |
| [`sort_by`](#sort_by) | Data | The items sorted by the value at a path. |
| [`index_by`](#index_by) | Data | An object of the items by the value at a path. |
| [`json`, `jsonify`](#json) | Data | The value as JSON. |
| [`where`, `find_index`, `slice`](#where-find_index-slice) | Data | Fluid's filters, faster on large data arrays. |
| [`xml_escape`](#xml_escape) | Output | The text with XML entities. |
| [`absolute_url`](#absolute_url) | Output | `site.url` followed by the url. |
| [`relative_url`](#relative_url) | Output | The url from the root of the server. |
| [`strip_html`](#strip_html) | Output | The text without tags. |
| [`date_to_xmlschema`](#date_to_xmlschema) | Output | An ISO 8601 date. |
| [`date_to_rfc822`](#date_to_rfc822) | Output | An RFC 822 date. |
| [`toc`](#toc) | Headings | The table of contents as nested lists. |
| [`t`](#t) | Locales | A UI string in the page's locale. |
| [`locale_url`](#locale_url) | Locales | A url in another locale. |
| [`locale_of`](#locale_of) | Locales | The locale of a url. |
| [`locale_pages`](#locale_pages) | Locales | Every page of every locale. |
| [`nav_active`](#nav_active) | Navigation | The sidebar with the current item marked. |
| [`nav_prev_next`](#nav_prev_next) | Navigation | The previous and next items. |
| [`nav_breadcrumbs`](#nav_breadcrumbs) | Navigation | The items from the top to the current one. |
| [`nav_flatten`](#nav_flatten) | Navigation | The leaves of the sidebar in order. |
| [`nav_section`](#nav_section) | Navigation | The top-level item that contains a url. |
| [`nav_label`](#nav_label) | Navigation | The label of an item in a locale. |
| [`image_url`](#image_url) | Images | The url of one variant of an image. |
| [`image_size`](#image_size) | Images | The width and height of an image. |
| [`social_image_url`](#social_image_url) | Images | The url of the page's social image. |

:::caution[Output encoding]
The [liquid step](steps/liquid.md) HTML-encodes the values of output tags by default (`encode: html`), in every file
it renders, XML included. Filters that return markup (`markdownify`, `excerpt`, `toc`, `json`) are not encoded again,
but the others are: `xml_escape` then escapes twice (`&amp;amp;`), and the `+` of `date_to_xmlschema` and
`date_to_rfc822` is written `&#x2B;`, which XML readers decode. Without `xml_escape`, a value is already escaped for
XML. A step with `encode: none` writes values as they are.
:::

### slug

```liquid
{{ "Héllo, World! 2" | slug }}
```

`hello-world-2`: lowercase, diacritics removed, letters, digits, `-`, `_` and `~` kept, any other character a dash,
runs of dashes collapsed, dashes, underscores and dots trimmed from the ends, at most 1000 characters. Letters of other
scripts are kept.

### markdownify

```liquid
{{ page.summary | markdownify }}
{{ "**a** b" | markdownify: inline: true }}
```

Renders a string as Markdown to HTML with the site's pipeline (the `markdown` section of `douze.yml`). With
`inline: true`, a single paragraph is returned without its `<p>`: `<strong>a</strong> b`. Nil is an empty string.

### strip_markdown

```liquid
{{ "**a** &amp; [b](c)" | strip_markdown }}
```

`a & b`: the Markdown rendered, then its tags removed and entities decoded, trimmed.

### excerpt

```liquid
{{ content | excerpt }}
```

The Markdown before `<!-- more -->` rendered to HTML, or without the marker, the first `<p>` of the rendered HTML (an
empty string when there is none).

### where_exp

```liquid
{{ collections.all | where_exp: "item", "item.data.draft != true" }}
```

The items for which the condition is true. The first argument names the item in the expression, the second is a
Liquid condition. Both are required: `where_exp needs a variable name and an expression: where_exp: "item", "item.data.draft != true"`.

### group_by_exp

```liquid
{% assign years = collections.post | group_by_exp: "item", "item.data.date | date: '%Y'" %}
{% for year in years %}<h2>{{ year.name }}</h2>{{ year.size }} posts{% endfor %}
```

One `{ name, items, size }` per value of the expression, in the order the values first appear. The expression can use
filters.

### sort_by

```liquid
{{ collections.post | sort_by: "data.title" }}
{{ collections.post | sort_by: "data.date", "desc" }}
```

The items sorted by the value at a dotted path. Numbers and dates compare by value, other values as strings
(ordinal). Items without the value are last in both orders. The sort is stable. The second argument is `desc` or
`descending` for a descending sort. Without a path: `sort_by needs the path of the value to sort by`.

### index_by

```liquid
{% assign by_url = collections.all | index_by: "data.url" %}
{{ by_url["/docs/last/index.html"].data.title }}
```

An object of the items by the value at a dotted path. The first item of a value wins, and items without one are left
out. Without a path: `index_by needs the path of the key`.

### json

```liquid
<script type="application/json">{{ sidebar | json }}</script>
```

The value as JSON. `jsonify` is the same filter. Site data, collections and other data objects are written in their
own key order; other values go through Fluid's `json` filter. The result is not HTML-encoded.

### where, find_index, slice

```liquid
{{ api | where: "module", page.module }}
{{ api | find_index: "file", name }}
{{ api | slice: 10, 20 }}
```

Fluid's filters. When the input is a large data array (or a slice of one) and the value a string, `where` and
`find_index` use an index built once per array instead of scanning it, and a slice of a data array keeps using that
index.

### xml_escape

```liquid
{{ title | xml_escape }}
```

`&`, `<`, `>`, `"` and `'` as `&amp;`, `&lt;`, `&gt;`, `&quot;` and `&#39;`. See the caution about
[output encoding](#filters).

### absolute_url

```liquid
{{ page.url | absolute_url }}
```

`site.url` without its trailing slash, followed by the url with a leading slash: `/x/` is
`https://example.com/blog/x/` for `site.url: https://example.com/blog`. A url with a scheme, `//` or `mailto:` is
returned as it is. Without `site.url`, the url with a leading slash.

### relative_url

```liquid
{{ "x/" | relative_url }}
```

The path of `site.url` followed by the url with a leading slash: `/blog/x/` for `https://example.com/blog`. Absolute
urls are returned as they are.

### strip_html

```liquid
{{ content | strip_html | truncate: 160 }}
```

The text without tags, comments, and `script` and `style` elements with their content. Entities stay as they are.

### date_to_xmlschema

```liquid
{{ page.date | date_to_xmlschema }}
```

`2020-01-02T03:04:05+00:00`. A value that is not a date is returned as it is.

### date_to_rfc822

```liquid
{{ page.date | date_to_rfc822 }}
```

`Thu, 02 Jan 2020 03:04:05 +0000`, for RSS. A value that is not a date is returned as it is.

### toc

```liquid
{{ toc | toc: class: "toc", max-depth: 3 }}
```

The tree that the [headings step](steps/headings.md) sets as `toc`, as nested lists:

```html
<ul class="toc"><li><a href="#setup">Setup</a><ul><li><a href="#install">Install</a></li></ul></li></ul>
```

| Argument | Default | Meaning |
|---|---|---|
| `class` | none | The class of the outer `<ul>`. |
| `max-depth` | `6` | Entries deeper than this heading level are left out, with their children. |

An empty tree, or a value that is not a list, is an empty string.

### t

```liquid
{{ "search.label" | t }}
{{ "pages.count" | t: count: 3 }}
```

The UI string at that key in `<strings>.<locale>` (`i18n.ja`, from `_data/i18n/ja.yml`), where `strings` is
`site.locales.strings` and `locale` the page's `locale`. Else the default locale's string, else the key itself with a
warning (`no translation for 'missing.key' in 'i18n.en'`). A key is a member (`"search.label"` as one key) or a dotted
path of nested members. Named arguments replace placeholders: `count: 3` replaces `{count}`. See
[Internationalization](../guide/internationalization.md).

### locale_url

```liquid
{{ page.url | locale_url: "ja" }}
{{ page.url | locale_url }}
```

The url in another locale, whether or not a page exists there: `/docs/install/index.html` is `/ja/docs/install/` in
`ja`. The url is normalized (no `index.html`), its locale prefix removed, then the prefix of the locale added (none for
the default locale). Without an argument, the url without its prefix. An unknown locale is an error:
`locale_url: unknown locale 'xx'`.

### locale_of

```liquid
{{ page.url | locale_of }}
```

The key of the locale whose prefix starts the url (`/ja/x/` is `ja`), the default locale's otherwise.

### locale_pages

```liquid
{% for p in collections.all | locale_pages %}
<url><loc>{{ p.url | absolute_url }}</loc></url>
{% endfor %}
```

Every page the site serves in every locale, as `{ locale, lang, url, fallback, data }` with the url normalized: each
collection item with its locale, and with `site.locales.fallback`, for a page of the default locale under the locale
root, one entry per locale that has no page at that url (`fallback: true`, with the default locale page's data). In the
order of the items, each followed by its fallbacks. For sitemaps.

### nav_active

```liquid
{% assign nav = sidebar | nav_active: page.url %}
```

A sidebar is a list of items: a url, or an object with `label` (a string, or labels by locale), `link`, `items` (a
group), and anything a layout reads (`icon`, `badge`, `collapsed`, `external`). `nav_active` returns the sidebar with
`active: true` on the url's item and `open: true` on its ancestors. Bare urls become `{ label, link }`, labeled with the
`title` of their page in `collections.urls`.

Every navigation filter compares links and urls without `index.html`, and removes the url's locale prefix when no link
of the sidebar has one. A page that uses one depends on the whole sidebar. See
[Navigation and sidebars](../guide/navigation.md).

### nav_prev_next

```liquid
{% assign pn = sidebar | nav_prev_next: page.url %}
{% if pn.prev %}<a href="{{ pn.prev.link }}">{{ pn.prev | nav_label }}</a>{% endif %}
```

`{ prev, next }`: the leaves next to the url's item in the same group (none across groups, none at the ends). Nil when
the url is not in the sidebar.

### nav_breadcrumbs

```liquid
{% for item in sidebar | nav_breadcrumbs: page.url %}{{ item | nav_label }} / {% endfor %}
```

The items from the top of the sidebar to the url's item. Empty when the url is not in the sidebar.

### nav_flatten

```liquid
{{ sidebar | nav_flatten | map: "link" | join: "," }}
```

The leaves of the sidebar in order: the items with a link and no items.

### nav_section

```liquid
{% assign section = sidebar | nav_section: page.url %}
```

The top-level item that contains the url's item, or nil.

### nav_label

```liquid
{{ item | nav_label }}
{{ item | nav_label: "ja" }}
```

The label of an item in a locale (default: the page's `locale`): a string label, or the locale's member of an object of
labels, else the default locale's, else its first one. A bare url is its own label.

### image_url

```liquid
<meta property="og:image" content="{{ 'images/hero.png' | image_url: width: 1200, format: 'jpeg' | absolute_url }}">
```

Makes one variant of a source image, adds it to the outputs of the page, and returns its url
(`/assets/images/hero-1200.<hash>.jpeg`). Arguments: `width`, `format`, `quality`, `output`. Without `width` or
`format`, the first of the `markdown.images` defaults, else the source's own. An SVG or a GIF is copied as it is. The
source is relative to the page's folder, or to the content folder when it starts with `/`. See
[Images](../guide/images.md).

### image_size

```liquid
{% assign size = "images/a.svg" | image_size %}{{ size.width }}x{{ size.height }}
```

`{ width, height }` of a source image, as displayed. Nothing is written; the source is a dependency of the page. A file
that is not an image is an error: `'<src>' is not an image format douze reads (png, jpeg, webp, avif, gif, svg)`.

### social_image_url

```liquid
<meta property="og:image" content="{{ page | social_image_url: 'og{{ url }}.png' | absolute_url }}">
```

The url of the page's social image: the `output` template of its [social-image step](steps/social-image.md), passed
as the argument and rendered with the page's data. The input is not read. Without an argument:
`social_image_url needs the output template of the social-image step`.

## Tags

| Tag | Result |
|---|---|
| [`image`](#image) | The variants of an image and their `<picture>` or `<img>` markup. |
| [`markdown`](#markdown) | Its body rendered as Markdown. |
| [`include_markdown`](#include_markdown) | A Markdown file of the content folder, rendered with the page's variables. |
| [`slot`](#slot) | A named part of a shortcode body. |
| [`error`](#error-and-warn) | Fails the page with a message. |
| [`warn`](#error-and-warn) | Logs a warning. |
| [Shortcodes](#shortcodes) | A tag per file of the shortcodes folder. |

### image

```liquid
{% image "images/dashboard.png", alt: "The dashboard", widths: [640, 1280], formats: ["webp", "png"], sizes: "(min-width: 60em) 50vw, 100vw", class: "figure" %}
{% image "/images/arch.svg", alt: "Architecture" %}
{% image light: "images/a-light.png", dark: "images/a-dark.png", alt: "The flow" %}
```

Makes the variants of a source image, adds them to the outputs of the page, and writes their markup. Arguments are
comma separated, positional or `name: value`; values are strings, numbers, `true`, `false`, `nil`, lists
(`[640, 1280]`) and variables (`page.hero`).

| Argument | Default | Meaning |
|---|---|---|
| source (positional) | | The image, relative to the page's folder, or to the content folder with a leading `/`. |
| `light`, `dark` | | Two sources: one `<picture>` whose dark sources have `media="(prefers-color-scheme: dark)"`. |
| `theme` | `media` | `attribute` writes two elements with `data-theme="light"` and `data-theme="dark"` instead. |
| `alt` | none | The `alt` attribute. |
| `widths` | `markdown.images.widths`, else the source's width | Variant widths; never wider than the source. |
| `formats` | `markdown.images.formats`, else the source's format | `webp`, `avif`, `png`, `jpeg`. |
| `quality` | `markdown.images.quality`, else `80` | The quality of lossy formats. |
| `output` | `markdown.images.output`, else `/assets/images/` | The folder of the variants. |
| `sizes` | none | The `sizes` attribute. |
| `class` | none | The `class` attribute. |
| `loading` | `markdown.images.loading`, else `lazy` | The `loading` attribute. |
| `decoding` | `async` | The `decoding` attribute. |
| any other name | | An attribute of the `<img>`: `true` without a value, `false` and nil left out. |

Variants are written as `<name>-<width>.<hash>.<ext>`; an SVG or a GIF is copied as `<name>.<hash>.<ext>` with its size
read from the file. The markup is a `<picture>` with a `<source>` per format but the last, and an `<img>` of the last
format at its largest width with `width` and `height`; one format gives a plain `<img>`:

```html
<img src="/assets/images/a.eafe29e7.svg" width="40" height="20" alt="A" loading="lazy" decoding="async">
```

Errors: `the image tag needs a source: {% image "path" %} or {% image light: "path", dark: "path" %}`,
`the image tag takes one source; name the other arguments`, `the image tag takes a source or 'light', not both`,
`image theme must be 'media' or 'attribute', not '<value>'`. Images other than SVG and GIF need libvips. See
[Images](../guide/images.md).

### markdown

```liquid
{% markdown %}
Some *Markdown* in a layout.
{% endmarkdown %}
```

Renders its body as Markdown to HTML with the site's pipeline, in layouts and HTML pages. In a Markdown page its output
is a raw region that the page's Markdown pass keeps as it is.

### include_markdown

```liquid
{% include_markdown "reference/options.md", version: "2.0" %}
{% include_markdown "./shared/note.md" %}
```

Renders a Markdown file as a Liquid template with the page's variables. The path is relative to the content folder, or
with `./` and `../` to the including file. The file's front matter values are variables that the page's names hide,
and the named arguments hide both. In a Markdown page, the output is Markdown source that the page renders, every line
indented like the tag so that it stays in its list item; elsewhere it is rendered to HTML. Its fences are not Liquid.
Includes nest up to 16 deep, and the page renders again when the file changes. A partial that is not a page sets
`permalink: false`.

Errors: `include_markdown: '<path>' not found in '<folder>'`, `include_markdown: '<path>' is outside the source folder`,
`include_markdown: more than 16 nested includes: ...`, and
`include_markdown takes a path and named arguments: {% include_markdown "path.md", name: value %}`.

### slot

```liquid
{% card %}
{% slot "title" %}Get started{% endslot %}
Body text.
{% endcard %}
```

In a shortcode body, renders `<div data-slot="title">...</div>`, its content rendered like a body (to HTML in a Markdown
page), so that the shortcode template can place the parts of its body. It takes one positional name:
`slot takes a name: {% slot "name" %}`.

### error and warn

```liquid
{% if api == nil %}{% error "unknown API reference " | append: name %}{% endif %}
{% warn "this page uses a deprecated layout" %}
```

`error` fails the page with the position of the tag: `error: render: /docs/a.md(12,3): unknown API reference Foo`.
`warn` logs a warning with the position: `warning: render: /index.html(27,1): careful`. The message is any Liquid
expression, with filters.

### Shortcodes

A shortcode is a `.liquid` or `.html` file directly in the shortcodes folder (`site.shortcodes`, default
`shortcodes` in the includes folder). Every template can use it as a tag named after the file:
`shortcodes/link_card.liquid` is `{% link_card %}`.

```liquid
---
block: true
display: block
args: [label]
params: [variant]
---
<div class="tabs" data-label="{{ label }}" data-variant="{{ variant }}">{{ body }}</div>
```

| Front matter | Default | Meaning |
|---|---|---|
| `block` | `false` | `true`: a paired tag, `{% tabs %}...{% endtabs %}`. |
| `display` | `block` when paired, `inline` otherwise | `block`: in a Markdown page, the output is a raw region at the column of the tag. `inline`: the output is written in the text, and a paired body has no `<p>`. |
| `args` | none | Names of the positional arguments, which become variables. |
| `params` | none | Names of the named arguments the template reads: one a call does not pass is nil instead of the page's value of that name. |

A call takes positional values and `name: value` pairs separated by commas, and any value or variable
(`{% tab page.title %}`). The template sees:

| Name | Value |
|---|---|
| `args` | The positional arguments, as a list. |
| each `args` name | The positional argument at that position. |
| each named argument | Its value. |
| `body` | Paired tags: the body rendered with the page's variables, then in a Markdown page as Markdown to HTML. |
| `source` | Paired tags: the body before Markdown. |
| anything else | The page's names, as in an include: `page`, `title`, data, `collections`, `site`. |

A shortcode can use another one. A page renders again when a shortcode it uses changes. Shortcode templates are not
strict, so their optional arguments work with `strict: true`.

Names are lowercase letters, digits and underscores, starting with a letter or `_`. Invalid shortcode files stop the
build before it starts (exit code 2), with their position:

| Message |
|---|
| `'<file>' is not a shortcode name: use lowercase letters, digits and underscores` |
| `shortcode '<name>' hides a built-in tag; rename the file` |
| `shortcode '<name>' is defined twice` |
| `invalid front matter: <reason>` |
| `'block' of shortcode '<name>' must be true or false` |
| `'display' of shortcode '<name>' must be 'block' or 'inline'` |
| `'args' of shortcode '<name>' must be a list of names (lowercase letters, digits and underscores)` (also `params`) |
| `unknown key '<key>' in the front matter of shortcode '<name>'; expected 'block', 'display', 'args' or 'params'` |

An error while rendering names the shortcode and the page:
`error: render: /docs/a.md: shortcode 'tabs' (/_includes/shortcodes/tabs.liquid(3,12)): ...`, and an unknown tag
suggests a shortcode: `Unknown tag 'tabss'; did you mean 'tabs'?`. See
[Includes and shortcodes](../guide/shortcodes.md).
