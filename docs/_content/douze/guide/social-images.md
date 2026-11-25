---
title: Social images
description: Render an Open Graph image for every page with the social-image step, from an SVG template and blocks of text.
liquid: false
---

The `social-image` step renders a PNG image for each page, for the `og:image` and `twitter:image` meta tags that
social networks and chat apps show next to a link. It draws an SVG background and lays text over it, the page's
title for instance. The text wraps in its box, so a long title does not overflow. Douze renders the SVG and the text
itself, with no browser, Node or native library. This page shows a complete setup.

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
background: shapes, gradients, a logo. Text goes in the `text` blocks, which wrap and clip: a `<text>` element in the
SVG is an error.

```liquid title="_includes/og/card.svg.liquid"
<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="630" viewBox="0 0 1200 630">
  <rect width="1200" height="630" fill="#1e1b4b"/>
  <rect x="80" y="560" width="200" height="8" fill="#a78bfa"/>
  <image href="logo.png" x="1040" y="500" width="80" height="80"/>
</svg>
```

Images that the SVG references by a relative path (to the template's folder, or to the content folder with a leading
`/`) are inlined, and they are dependencies of the pages: changing the logo renders every card again. An image by an
absolute url is an error.

Douze draws the static subset of SVG that backgrounds use: shapes and paths, transforms, view boxes, solid colors,
linear and radial gradients, opacity, simple style sheets, `use` and images. Clipping paths, masks, filters, markers
and patterns fail the page with the element and its line. The
[step reference](../reference/steps/social-image.md#supported-svg) lists what is supported. Export the SVG of a design
tool with its text converted to paths, or draw it with plain shapes, to stay within it.

## Text blocks

Each block of `text` renders its `value`, a Liquid template, as plain text:

| Field | Meaning |
|---|---|
| `value` | the text, a template over the page's data |
| `font` | a font description: families, style words, size in pixels (`Outfit Bold 64`) |
| `fontfile` | a font file to look in first, relative to the source folder |
| `x`, `y`, `width`, `height` | the box: the text wraps at `width` and is clipped at `height` |
| `align` | `left` (default), `center` or `right` in the box |
| `color` | `#rrggbb` or `#rrggbbaa` |

Containers and CI machines usually have no fonts installed, so give a `fontfile` and name its family in `font`, with
the style and the size. When no family of `font` is found, the font of the `fontfile` is used. Without a `fontfile`,
the families are looked for among the fonts of the system (`Inter, sans`: Inter, else a common sans serif font), and
a page whose font is not found fails:

```txt
font 'Outfit Bold 64' not found; set 'fontfile' to a font file of the site, or install the font
```

Style words are `italic` and weights from `thin` to `black`; a font file of one style is used for every style. See
[Fonts](../reference/steps/social-image.md#fonts) in the reference.

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

## Cost

Each image is cached with what it depends on (the template, its images, the fonts and the text), so a rebuild renders
only the cards whose page changed. Pages with the same background SVG rasterize it once per build.

## See also

- [social-image step reference](../reference/steps/social-image.md)
- [Images](images.md)
- [Liquid filters and tags](../reference/liquid.md)
- [Cache and incremental builds](cache.md)
