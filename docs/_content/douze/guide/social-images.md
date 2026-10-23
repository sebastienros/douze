---
title: Social images
description: Render an Open Graph image for every page with the social-image step, from an SVG template and text laid out by Pango.
liquid: false
---

The `social-image` step renders a PNG image for each page, for the `og:image` and `twitter:image` meta tags that
social networks and chat apps show next to a link. It draws an SVG background and lays text over it, the page's
title for instance. The text wraps in its box, so a long title does not overflow. Rendering uses libvips and Pango,
inside Douze, with no browser or Node. This page shows a complete setup.

## Set it up

The step sits in the chain of pages, after their data is known and before they are written:

```yaml title="douze.yml" {8-31}
steps:
  pages:
    inputs: "_content/**/*.md"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  og:
    needs: permalink
    uses: social-image
    with:
      template: og/card.svg.liquid
      output: "og/{{ title | slug }}.png"
      skip: "og == false"
      text:
        - value: "{{ title }}"
          font: "Outfit Bold 64"
          fontfile: assets/fonts/Outfit-Bold.ttf
          x: 80
          y: 120
          width: 1040
          height: 300
          color: "#ffffff"
        - value: "{{ description }}"
          font: "Outfit 30"
          fontfile: assets/fonts/Outfit-Regular.ttf
          x: 80
          y: 420
          width: 1040
          height: 120
          color: "#c8c8d0"
  render:
    needs: og
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

The step passes every page through unchanged, and adds its image to the page's outputs, so `write` writes both. It is
not a branch of the workflow.

## The template

`template` is a Liquid template of the includes folder that renders an SVG, with the page's data. It is the
background: shapes, colors, a logo. Text is better left to the `text` blocks, which wrap and clip.

```liquid title="_includes/og/card.svg.liquid"
<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="630" viewBox="0 0 1200 630">
  <rect width="1200" height="630" fill="#1e1b4b"/>
  <rect x="80" y="560" width="200" height="8" fill="#a78bfa"/>
  <image href="logo.png" x="1040" y="500" width="80" height="80"/>
</svg>
```

Images that the SVG references by a relative path (to the template's folder, or to the content folder with a leading
`/`) are inlined, and they are dependencies of the pages: changing the logo renders every card again.

## Text blocks

Each block of `text` renders its `value`, a Liquid template, as plain text:

| Field | Meaning |
|---|---|
| `value` | the text, a template over the page's data |
| `font` | a Pango font description: family, style, size in pixels (`Outfit Bold 64`) |
| `fontfile` | a font file to load first, relative to the source folder |
| `x`, `y`, `width`, `height` | the box: the text wraps at `width` and is clipped at `height` |
| `align` | `left` (default), `center` or `right` in the box |
| `color` | `#rrggbb` or `#rrggbbaa` |

Containers and CI machines usually have no fonts installed, so give a `fontfile` and use the family name that the file
declares in `font`. When the family is not found, Pango falls back to another font without an error.

## Output and skipping

- `output` is a Liquid template for the image's path in the site. Make it unique per page, from its slug or url.
- `skip` is a Liquid condition: a page for which it is true gets no image. `og == false` lets a page opt out with
  `og: false` in its front matter.
- `width` and `height` default to 1200 and 630, the size most networks expect.

## Link the image from the layout

`social_image_url` computes the url with the same template as `output`, so the layout and the step agree:

```liquid title="_includes/base.liquid"
{%- unless og == false %}
<meta property="og:image" content="{{ page | social_image_url: "og/{{ title | slug }}.png" | absolute_url }}">
<meta name="twitter:card" content="summary_large_image">
{%- endunless %}
```

Social networks need an absolute url: set `site.url` in `douze.yml` for `absolute_url`.

## Requirements and cost

The step needs libvips, which the native builds of Douze include. A workflow with a `social-image` step fails before
building when libvips is missing, and `douze doctor` checks it. Each image is cached with what it depends on (the
template, its images, the fonts and the text), so a rebuild renders only the cards whose page changed.

## See also

- [social-image step reference](../reference/steps/social-image.md)
- [Images](images.md)
- [Liquid filters and tags](../reference/liquid.md)
- [Cache and incremental builds](cache.md)
