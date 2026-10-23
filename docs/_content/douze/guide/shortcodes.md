---
title: Includes and shortcodes
description: Reuse markup with includes, build your own tags with shortcodes, and include Markdown files in pages.
liquid: false
---

Douze has three ways to reuse content. An include renders a Liquid file of the includes folder where the tag is. A
shortcode is a Liquid file that becomes a tag of its own, with arguments and a body, usable in every page: Douze's
counterpart of components. `include_markdown` renders a Markdown file into a page, so that its headings and lists
become the page's.

## Includes

`{% include %}` renders a file of the includes folder with the variables of the page, at the place of the tag. A name
without an extension gets `.liquid`:

```liquid title="_includes/partials/nav.liquid"
<nav aria-label="Main">
  <a href="/"{% if url == "/index.html" %} aria-current="page"{% endif %}>Home</a>
  <a href="/blog/">Blog</a>
</nav>
```

```liquid
{% include "partials/nav" %}
{% include "card", title: "Install", link: "/docs/install/" %}
```

Named arguments are variables of the include. An include sees, and changes, the variables of the page: a variable it
assigns is visible after the tag.

`{% render %}` works the same, but what it assigns stays inside it:

```liquid
{% render "footer", year: 2026 %}
```

In Douze, the page's data (`title`, `site`, `collections`, site data) still resolves inside `render`, since it is the
model of the template, not a variable of the page.

## Shortcodes

A shortcode is a Liquid file of the shortcodes folder, `_includes/shortcodes/` (`site.shortcodes` in `douze.yml`).
Its file name is its tag name: `shortcodes/badge.liquid` is `{% badge %}`, and `link_card.liquid` is
`{% link_card %}`. Names are lowercase letters, digits and underscores, and cannot be a Liquid or Douze tag
(`include`, `raw`, `markdown`...).

### Inline shortcodes

```liquid title="_includes/shortcodes/badge.liquid"
---
args: [text]
---
<span class="badge badge-{{ variant | default: 'note' }}">{{ text }}</span>
```

```md
Install it with {% badge "v2", variant: "tip" %}.
```

Arguments are positional values and `name: value` pairs, separated by commas; any value or variable works
(`{% badge page.status %}`). In the template, each named argument is a variable, and the positional ones are in
`args`; `args` in the front matter names them, so `text` is the first one.

:::tip
The output of a shortcode is its whole file, final newline included, and in a sentence that newline reads as a space
(`v2 .`). Save inline shortcodes without a final newline.
:::

### Block shortcodes

`block: true` makes a paired tag. Its body is in `body`:

```liquid title="_includes/shortcodes/callout.liquid"
---
block: true
args: [kind]
---
<div class="callout callout-{{ kind | default: 'note' }}">{{ body }}</div>
```

```md
{% callout "tip" %}
Use **Markdown** here.
{% endcallout %}
```

```html
<div class="callout callout-tip"><p>Use <strong>Markdown</strong> here.</p>
</div>
```

- In a Markdown page, the body is rendered with the page's variables, then as Markdown, with the site's Markdown
  settings: that is `body`. `source` is the body before Markdown.
- In an HTML page or a layout, `body` is the body as it is.
- `display` is `block` (the default for a paired tag) or `inline` (the default otherwise). An inline body has no
  `<p>`: `Press {% kbd %}**Ctrl**{% endkbd %}`.

The output of a block shortcode in a Markdown page is kept as it is by the page's Markdown pass, blank lines included,
even inside a list item or a block quote.

### Slots

`{% slot "name" %}...{% endslot %}` in a body marks a part of it, rendered like the body, as
`<div data-slot="name">...</div>`. The shortcode's template, or a script, places the parts:

```md
{% card %}
{% slot "title" %}Install{% endslot %}
Run the install script.
{% endcard %}
```

### Shortcodes that use shortcodes

A shortcode can use another one, and a body can contain shortcodes:

```md
{% tabs %}
{% tab "macOS" %}
Run the shell script.
{% endtab %}
{% tab "Windows" %}
Run the PowerShell script.
{% endtab %}
{% endtabs %}
```

### Errors

An error in a shortcode names the shortcode, its position and the page, and an unknown tag suggests a shortcode:

```txt
error: render: /docs/a.md: shortcode 'tabs' (/_includes/shortcodes/tabs.liquid(3,12)): ...
Unknown tag 'tabss'; did you mean 'tabs'?
```

An invalid shortcode file stops the build before it starts, with its position. A page renders again when a shortcode
it uses changes.

## Include Markdown files

`{% include_markdown %}` renders a Markdown file of the content folder into the page, as a Liquid template with the
page's variables. With `./` or `../`, the path is relative to the including file's folder:

```md title="_content/docs/intro.md"
---
title: Introduction
---
# Introduction

{% include_markdown "./shared/requirements.md", product: "Douze" %}
```

```md title="_content/docs/shared/requirements.md"
---
permalink: false
---
## Requirements

{{ product }} runs on Windows, macOS and Linux.
```

- In a Markdown page, the included file's output is Markdown that the page renders: its `## Requirements` is a
  heading of the page, in its table of contents. Every line is indented like the tag, so an include stays in its list
  item.
- In an HTML page or a layout, it is rendered to HTML.
- The front matter of the included file provides variables, which the page's names hide; named arguments hide both.
- `permalink: false` keeps the partial from being a page of its own.
- Includes nest up to 16 deep, a missing file is an error, and the page renders again when the file changes.

## Which one to use

| Need | Use |
|---|---|
| A piece of a layout (header, footer, head) | `{% include %}` |
| An isolated piece with parameters | `{% render %}` |
| A tag that authors use in Markdown, with a body | A block shortcode |
| A small inline element (badge, key, icon) | An inline shortcode |
| The same Markdown section in several pages | `{% include_markdown %}` |

## See also

- [Liquid filters and tags](../reference/liquid.md)
- [Layouts](layouts.md)
- [Markdown](markdown.md)
- [Project structure](project-structure.md)
- [Template data](../reference/template-data.md)
