---
title: Markdown
description: How Douze renders Markdown pages, its extensions and asides, links between .md files, and how Liquid and Markdown work together.
liquid: false
---

Douze renders Markdown with [Markdig](https://github.com/xoofx/markdig). A `.md` page is a Liquid template first and
Markdown second, as in Eleventy: tags and variables run, then Markdown renders what they produced. This page covers
the Markdown flavor and its extensions, asides, links between pages, code fences, and the ways to mix Liquid and
Markdown.

## CommonMark by default

Without a `markdown` section in `douze.yml`, Markdown is plain [CommonMark](https://commonmark.org): no tables, no
footnotes, no asides. Add the section to turn on extensions. With the section and no `extensions` key, you get this
list:

```yaml title="douze.yml"
markdown:
  extensions: [pipetables, footnotes, tasklists, definitionlists, attributes, autolinks, alerts, customcontainers]
```

`extensions` takes Markdig's names (`pipetables`, `gfm-pipetables`, `footnotes`, `emojis`, `smartypants`,
`mathematics`, `figures`, `abbreviations`...). `advanced` is Markdig's set without `autoidentifiers`, because heading
ids are the job of the [headings step](../reference/steps/headings.md). `strikethrough` is `~~text~~` without the rest
of `emphasisextras`. `smartypants` also turns the apostrophe of `it's` into `’`. An unknown name stops the build with
a suggestion:

```
douze.yml(2,16): error: unknown Markdown extension 'tabels'; did you mean 'pipetables'?
```

The `markdown` section applies to everything that renders Markdown: the `markdown` step, `include_markdown`, the
`markdown` tag, `markdownify` and the bodies of shortcodes. The `extensions` option of a `markdown` step replaces the
extensions for that step only.

## Asides and alerts

With `customcontainers`, a `:::kind` block is an aside. With `alerts`, so is a GitHub alert:

```md
:::tip
Run `douze serve` while you write.
:::

:::caution[Before you upgrade]
Read the release notes.
:::

> [!NOTE]
> Alerts render the same way.
```

Both render as:

```html
<aside aria-label="Tip" class="aside aside--tip"><p class="aside__title" aria-hidden="true">Tip</p>
<div class="aside__content">
<p>Run <code>douze serve</code> while you write.</p>
</div>
</aside>
```

`:::tip[Title]` sets the title. The kinds are `note`, `tip`, `caution` and `danger`. The alerts `IMPORTANT` and
`WARNING` are `note` and `caution`, and any other kind renders as the first of `kinds`, with a warning that names the
page and line. The `aside` section changes the markup to match your styles:

```yaml title="douze.yml"
markdown:
  aside:
    tag: aside
    class: callout # callout, callout--tip, callout__title, callout__content
    kinds: [note, tip, caution, danger]
    titles:
      note: Good to know
    icons:
      tip: '<svg width="16" height="16">...</svg>'
```

## Links to other pages

A relative link to a `.md` file of the content folder becomes the url of its page, with the default permalinks:

```md
See [the setup](setup.md#install) and [the guide](../guide/index.md).
```

From `/docs/index.md`, `setup.md#install` is `/docs/setup/#install`. Links work in your editor and on GitHub, and on
the site. A link to a file that does not exist is left as it is, and the page renders again once the file is created.
Absolute urls, root-relative paths, `mailto:` and anchors are left alone. `links: false` in the `markdown` section
turns the rewriting off.

:::caution
The rewritten link is computed from the source path. It does not follow a `permalink` that the target page sets, and
it does not add the path of `site.url`. A site served under a path, such as a GitHub Pages project site, keeps its
pages under a folder of that name in the content folder (`_content/my-repo/`).
:::

## Code fences

A fence renders as `<pre data-lang="..." data-meta="..."><code class="language-...">`. `data-meta` is the rest of the
info string (`title="a.cs" {2}`), for highlighters. The [highlight step](syntax-highlighting.md) reads it to render
highlighted code at build time. `code.class-prefix` changes `language-`, and a language of `code.passthrough`
(`[mermaid]` by default) renders as `<pre class="mermaid">` with no `code` element, for a client script:

```yaml title="douze.yml"
markdown:
  code:
    class-prefix: language-
    passthrough: [mermaid]
```

## Liquid before Markdown

In the default workflow, the `liquid` step renders a page before the `markdown` step does, so Markdown can use
variables, tags and shortcodes:

```md title="_content/index.md"
---
title: Home
version: "2.0"
---
# {{ title }}

Douze {{ version }} is out. {% if site.url %}It runs on {{ site.url }}.{% endif %}
```

Three rules follow from that order.

**Fences are not Liquid.** The `{{ }}` and `{% %}` of a fenced code block are kept as they are, so a code sample can
show Liquid. The `liquid` step's `protect: none` renders fences as Liquid too.

**Other `{{` and `{%` are Liquid**, inline code included. A page that shows Liquid syntax outside fences wraps it in
`{% raw %}`, or turns Liquid off:

```md
Use `{% raw %}{{ page.title }}{% endraw %}` in a layout.
```

```yaml
---
liquid: false    # this page is not a Liquid template
---
```

A folder data file can set `liquid: false` for every page of its folder. Without either, the page fails with the
position of the tag, for instance `error: render: /docs/a/index.md(1,26): Invalid 'if' tag`.

**A captured variable holds Markdown source.** `{% capture footer %}**Thanks**{% endcapture %}` holds `**Thanks**`,
which is not rendered when a layout writes it. Write captured content in HTML.

`markdown: false` in front matter or folder data leaves a page as Markdown, and a Markdown page whose `permalink` is
not HTML (`feed.xml`) is not rendered to HTML.

## Raw regions

Tags and shortcodes that produce block HTML write it as a raw region: one line that starts and ends with the U+001E
character, its newlines replaced by U+001D. The Markdown pass keeps it as it is, blank lines included, and it stays in
the list item or block quote of the tag. You rarely write one yourself, but it explains why a block shortcode inside a
list keeps the list together. See [Includes and shortcodes](shortcodes.md).

## Markdown includes

`include_markdown` renders a Markdown file of the content folder, or of the including file's folder with `./` and
`../`, as part of the page:

```md title="_content/docs/index.md"
---
title: Options
---
{% include_markdown "./partials/options.md", flag: "x" %}
```

```md title="_content/docs/partials/options.md"
---
permalink: false
flag: default
---
- Flag is {{ flag }}, page is {{ title }}.
```

The include is rendered as a Liquid template with the page's variables. Its front matter values are variables that the
page's names hide, and named arguments hide both, so the example renders `Flag is x, page is Options`. In a Markdown
page, the output is Markdown that the page renders, so its headings are the page's and it stays in its list item. Its
fences are not Liquid. A missing file is an error, includes nest up to 16 deep, and the page renders again when the
file changes. `permalink: false` keeps the partial from becoming a page.

## Markdown in templates

In a layout or an HTML page:

```liquid
{% markdown %}
Some **Markdown** in a layout.
{% endmarkdown %}

{{ page.summary | markdownify }}
{{ "*one* line" | markdownify: inline: true }}
{{ page.summary | strip_markdown }}
{{ content | excerpt }}
```

`markdownify: inline: true` renders a single paragraph without `<p>`. `strip_markdown` gives the plain text, and
`excerpt` the first paragraph of rendered HTML, or what comes before `<!-- more -->`.

## See also

- [markdown step reference](../reference/steps/markdown.md)
- [Configuration reference: markdown](../reference/configuration.md)
- [Liquid filters and tags](../reference/liquid.md)
- [Syntax highlighting](syntax-highlighting.md)
- [Includes and shortcodes](shortcodes.md)
