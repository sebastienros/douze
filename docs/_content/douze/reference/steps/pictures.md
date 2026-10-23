---
title: pictures
description: "The pictures step: rewrites the img elements of pages into picture markup of their variants."
---

`uses: pictures` rewrites the `<img>` elements of HTML pages whose source is an image of the `images` map (from
[`image-map`](image-map.md)) into the markup of the `image` tag: a `<picture>` of the variants, with
`width` and `height`, so that the page does not move while images load.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | `**/*.html` |
| Default workflow | no |

## What it does

For each `<img src="...">` of a page:

- The source is resolved like a Markdown link: relative to the page's source folder, or to the content folder when it
  starts with `/`. A query or a fragment is ignored. External urls (`https:`, `data:`, `//`) are left alone.
- When the resolved url is a key of `images`, the element is replaced by the markup of its variants: a `<picture>`
  with a `<source>` per format but the last, whose `srcset` lists every width, and an `<img>` of the last format with
  its largest width, `width`, `height`, `alt`, `loading` and `decoding`. A single format gives a plain `<img>`, with a
  `srcset` when there are several widths.
- `alt`, `sizes` and `class` are kept. `loading` is kept, or set to `markdown.images.loading`, else `lazy`; `decoding`
  is kept, or set to `async`. Every other attribute (`title`, `id`, `data-*`...) is kept as it is. `src`, `srcset`,
  `width` and `height` are replaced.

Images that are not in `images`, images already inside a `<picture>`, and `<img>` inside comments, `<pre>`, `<code>`,
`<script>`, `<style>` and `<textarea>` are left as they are. The step changes the page's content only.

For example, `![A test](images/x.png)` with `widths: [320, 640]` and `formats: [webp, png]` becomes:

```html
<picture><source type="image/webp" srcset="/assets/images/x-320.9758097b.webp 320w, /assets/images/x-640.0f6f6979.webp 640w"><img src="/assets/images/x-640.a82043f3.png" srcset="/assets/images/x-320.abc9b219.png 320w, /assets/images/x-640.a82043f3.png 640w" width="640" height="320" alt="A test" loading="lazy" decoding="async"></picture>
```

## Options

None. Any `with` option is an error (`unknown option '...' for step '...'`).

## Requirements

None: the variants are made by the `images` step.

## Caching

A page depends on the entries of `images` it used only: a new or changed image renders again the pages that show
it, not the others.

## Example

```yaml title="douze.yml"
site:
  content: docs

steps:
  pages:
    inputs: ["docs/**", "!docs/**/*.{png,jpg,jpeg,webp,avif,gif,svg}"]
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: docs
  markdown:
    needs: permalink
    uses: markdown
  images:
    inputs: "docs/**/*.{png,jpg,jpeg,webp,avif,gif,svg}"
    uses: images
    with:
      widths: [320, 640]
      formats: [webp, png]
  image-map:
    needs: images
    uses: image-map
  pictures:
    needs: [markdown, image-map]
    uses: pictures
  write:
    needs: [pictures, images]
    uses: write
```

`pictures` works on HTML, so it runs after `markdown`; in a workflow with layouts, it can run before or after
`layout`.

## Related

- [images](images.md) and [image-map](image-map.md)
- [The `image` tag](../liquid.md)
- [Images guide](../../guide/images.md)
