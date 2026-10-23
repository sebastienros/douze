---
title: Add icons to external links
description: Mark the links that leave the site with an icon, with CSS alone or with a class that the layout adds.
---

Markdown writes a link as a plain `<a href="...">`. An external link is one whose `href` starts with `http`, while
links inside the site are root-relative (`/docs/`) or relative. Two approaches mark them: a CSS selector, which needs
no change to the HTML, or a class and attributes added by the layout.

## With CSS only

An attribute selector matches the links by their `href`, and `::after` adds the icon:

```css title="assets/site.css"
main a[href^="http"]::after {
  content: "\2197"; /* ↗ */
  display: inline-block;
  margin-inline-start: 0.15em;
  font-size: 0.8em;
}

/* Absolute links to the site itself are not external. */
main a[href^="https://example.com"]::after {
  content: none;
}
```

Scope the selector (`main` here) to the content, so that the navigation and the footer keep their own style.

## With a class from the layout

To open external links in a new tab, or to give them a class, change the HTML. The layout receives the page's HTML as
`content`, and the `replace` filter can rewrite the start of every external link:

```liquid
<main>
{{ content | replace: '<a href="http', '<a class="external" target="_blank" rel="noopener" href="http' }}
</main>
```

The page:

```md
See [the docs](/docs/), [Liquid](https://shopify.github.io/liquid/) and <https://example.org>.
```

is written as:

```html
<main>
<p>See <a href="/docs/">the docs</a>, <a class="external" target="_blank" rel="noopener" href="https://shopify.github.io/liquid/">Liquid</a> and <a class="external" target="_blank" rel="noopener" href="https://example.org">https://example.org</a>.</p>
</main>
```

and the stylesheet targets the class:

```css
a.external::after {
  content: "\2197";
  margin-inline-start: 0.15em;
  font-size: 0.8em;
}
```

The replacement matches the exact text `<a href="http`, which is what Markdown writes for a link. A link written in
HTML with another attribute first (`<a class="x" href="https://...">`) is not changed; for those, the CSS approach is
more robust. A step that parses the HTML, such as a [worker step](node-worker.md) running an HTML parser,
handles every case.

## See also

- [Layouts](../guide/layouts.md)
- [Styles, scripts and assets](../guide/assets.md)
- [Markdown](../guide/markdown.md)
- [Liquid filters and tags](../reference/liquid.md)
