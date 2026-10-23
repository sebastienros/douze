---
title: social-image
description: "The social-image step: renders an Open Graph PNG per page from an SVG template and text blocks."
---

`uses: social-image` renders an Open Graph image per page: an SVG Liquid template rasterized as the background,
with blocks of text laid out over it by Pango, encoded as PNG.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | `**/*.html`, `**/*.md` |
| Default workflow | no |

## What it does

For each page it consumes, unless `skip` is true for it:

1. Renders `template`, a Liquid file of the includes folder that produces SVG, with the page's data (the same names
   as any template of the page). Images the SVG references by a relative `href` or `xlink:href` (relative to the
   template's folder, or to the content folder when it starts with `/`) are inlined as data URIs.
2. Rasterizes the SVG at `width` by `height`.
3. Renders each block of `text`: its `value` (a Liquid template over the page's data) is rendered, trimmed, and laid
   out with Pango as plain text in its box, then composited over the background.
4. Renders `output` (a Liquid template over the page's data) to get the image's path, and adds the PNG to the outputs
   of the step.

The page itself passes through unchanged, so the step sits in the chain of the pages, not on a branch: put it between
two steps of the page chain. A layout computes the url of the image with the `social_image_url` filter and the same
template as `output` (see [Liquid](../liquid.md)).

The rendered `output` is a file path: a leading `/` is optional, and a result that is empty, ends with `/`, or holds
`..` or `:` fails the page (`social image output '...' is not a file path`).

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `template` | string, required | | the SVG template, a path in the includes folder |
| `output` | string, required | | a Liquid template over the page's data: the path of the image |
| `width` | integer, 1 to 10000 | `1200` | the width of the image, in pixels |
| `height` | integer, 1 to 10000 | `630` | the height of the image, in pixels |
| `skip` | string | none | a Liquid condition over the page's data (`og == false`); pages for which it is true get no image |
| `text` | list of text blocks | none | the text laid out over the background |

Each block of `text`:

| Key | Type | Default | Meaning |
|---|---|---|---|
| `value` | string, required | | a Liquid template over the page's data: the text, rendered as plain text |
| `font` | string | `sans 32` | a Pango font description: family, style, size in pixels (`Outfit Bold 56`) |
| `fontfile` | string | none | a font file, relative to the source folder, that Pango loads before resolving `font`; containers usually have no system fonts |
| `x`, `y` | integer | `0` | the position of the block's box |
| `width` | integer | the image's width minus `x` | where the text wraps |
| `height` | integer | the image's height minus `y` | where the text is clipped |
| `color` | string | `#000000` | `#rgb`, `#rgba`, `#rrggbb` or `#rrggbbaa` |
| `align` | string | `left` | `left`, `center` (or `centre`) or `right`, in the block's box |

A block whose rendered text is empty or white space is skipped.

## Requirements

libvips, with its SVG loader and Pango, included in the native packages of Douze (see
[images](images.md#requirements)). A workflow with a `social-image` step checks it before building, and
`douze doctor` reports it.

## Caching

Each page is a unit. Its result depends on the page's data that the templates read, the template and the includes it
uses, the images inlined in the SVG and the font files: changing any of them renders the image again.

## Errors

```
douze.yml(4,3): error: step 'og' needs the option 'output'
douze.yml(4,3): error: options 'width' and 'height' of step 'og' must be from 1 to 10000
douze.yml(4,3): error: option 'text' of step 'og' has an invalid 'align' 'middle'; expected 'left', 'center' or 'right'
douze.yml(4,3): error: option 'text' of step 'og' has an invalid color 'white'; expected #rrggbb or #rrggbbaa
douze.yml(4,3): error: option 'text' of step 'og' needs a 'value'
```

During the build: `template '...' not found in '...'`, `font file '...' not found`, `image '...' not found (...)`.

## Example

`_includes/og/card.svg.liquid`:

```liquid title="_includes/og/card.svg.liquid"
<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="630"><rect width="1200" height="630" fill="#20232a"/></svg>
```

```yaml title="douze.yml"
site:
  content: docs

steps:
  pages:
    inputs: "docs/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: docs
  og:
    needs: permalink
    uses: social-image
    with:
      template: og/card.svg.liquid
      output: "og{{ url | replace: 'index.html', 'card.png' }}"
      skip: "og == false"
      text:
        - value: "{{ title }}"
          font: "sans bold 56"
          x: 80
          y: 160
          width: 1040
          height: 220
          color: "#ffffff"
  markdown:
    needs: og
    uses: markdown
  write:
    needs: markdown
    uses: write
```

`docs/guide/start.md`, whose `url` is `/guide/start/index.html`, gets `/og/guide/start/card.png`; a page with
`og: false` in its front matter gets none.

## Related

- [The `social_image_url` filter](../liquid.md)
- [Social images guide](../../guide/social-images.md)
- [images](images.md)
- [`douze doctor`](../cli.md)
