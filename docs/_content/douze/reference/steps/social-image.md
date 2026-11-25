---
title: social-image
description: "The social-image step: renders an Open Graph PNG per page from an SVG template and text blocks."
---

`uses: social-image` renders an Open Graph image per page: an SVG Liquid template rasterized as the background,
with blocks of text laid out over it, encoded as PNG. Douze draws both itself: no browser, Node or native library.

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
2. Rasterizes the SVG at `width` by `height` (see [Supported SVG](#supported-svg)): its `viewBox` is fitted in its
   own `width` and `height`, which are stretched to the image's size.
3. Renders each block of `text`: its `value` (a Liquid template over the page's data) is rendered, trimmed, laid out
   as plain text in its box with the block's font (see [Fonts](#fonts)), and drawn over the background.
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
| `font` | string | `sans 32` | a font description: families, style words, size in pixels (`Outfit Bold 56`; see [Fonts](#fonts)) |
| `fontfile` | string | none | a font file (TrueType or OpenType), relative to the source folder, tried before the system's fonts; containers usually have no system fonts |
| `x`, `y` | integer | `0` | the position of the block's box |
| `width` | integer | the image's width minus `x` | where the text wraps |
| `height` | integer | the image's height minus `y` | where the text is clipped |
| `color` | string | `#000000` | `#rgb`, `#rgba`, `#rrggbb` or `#rrggbbaa` |
| `align` | string | `left` | `left`, `center` (or `centre`) or `right`, in the block's box |

A block whose rendered text is empty or white space is skipped.

## Fonts

`font` is `[families] [style words] [size]`: `Outfit Bold 56`, `Inter, sans italic 24`, `serif 40px`.

- Families are a comma separated list, tried in order. Each is looked for as the family of the block's `fontfile`,
  then among the fonts installed on the system. `sans` (or `sans-serif`), `serif` and `monospace` (or `mono`) are
  generic families: a common font of the system. When no family is found, the `fontfile` is used; without one, the
  page fails: `font 'Outfit Bold 56' not found; set 'fontfile' to a font file of the site, or install the font`.
- Style words end the families: `italic` or `oblique`, and a weight, from `thin`, `extralight` (or `ultralight`),
  `light`, `semilight`, `book`, `regular` and `medium`, which are regular, to `semibold` (or `demibold`), `bold`,
  `extrabold` (or `ultrabold`), `heavy` and `black`, which are bold. The words take a dash too (`semi-bold`). A weight
  is first tried as part of the family name: `Outfit Light` finds a family named `Outfit Light`, the name a font file
  of that weight often has. A family without the requested style uses its closest one: a font file of one style is
  used for every style.
- The size is in pixels, with an optional `px`, and is 16 without one. A size that is not a positive number fails the
  page: `invalid font size '0' in font 'sans 0'`.

Text wraps at the width of the block's box, is aligned in it, and is clipped at its height.

## Supported SVG

Douze renders the static subset of SVG that a card background uses. Text belongs in the `text` blocks of the step.

| Feature | Supported |
|---|---|
| Elements | `svg` (nested too), `g`, `a`, `switch` (its first child), `use` (of an element or a `symbol`, `href="#id"`), `rect` (with `rx`, `ry`), `circle`, `ellipse`, `line`, `polyline`, `polygon`, `path`, `image` |
| Geometry | `transform` (`matrix`, `translate`, `scale`, `rotate`, `skewX`, `skewY`), `viewBox`, `preserveAspectRatio`; lengths in `px`, `pt`, `pc`, `mm`, `cm`, `in`, `em` (16px), `ex` (8px) or percentages of the viewport |
| Fill and stroke | `fill`, `fill-rule`, `stroke`, `stroke-width`, `stroke-linecap`, `stroke-linejoin`, `stroke-miterlimit`, `stroke-dasharray`, `stroke-dashoffset` |
| Opacity and visibility | `opacity` (of an element and its children), `fill-opacity`, `stroke-opacity`, `display: none`, `visibility` |
| Colors | `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`, `rgb()`, `rgba()`, `hsl()`, `hsla()`, color names, `transparent`, `currentColor` (with `color`), `none` |
| Paint servers | `linearGradient` and `radialGradient` (`gradientUnits`, `gradientTransform`, `spreadMethod`, focal point `fx`, `fy`, `fr`, attributes and stops inherited through `href`), `stop` (`offset`, `stop-color`, `stop-opacity`), as `url(#id)` with an optional fallback color |
| Styles | presentation attributes, `style` attributes, and `<style>` sheets whose selectors are a type, classes and an id, alone or together (`rect`, `.accent`, `#logo`, `rect.accent`, `*`), applied by specificity; at-rules (`@font-face`, `@media`, `@import`) are skipped |
| Images | `image` with PNG, JPEG, WebP, GIF or SVG data, inlined: the step inlines images the SVG references by a relative path |

Definitions (`defs`, `symbol`, gradients), `title`, `desc`, `metadata`, `script`, animation elements (`animate`,
`animateMotion`, `animateTransform`, `set`) and elements of other namespaces (an editor's metadata) are not drawn where
they stand. An SVG without a `width` and a `height` needs a `viewBox`.

Anything else fails the page:

- `text`, `tspan` and `textPath`;
- the properties `clip-path`, `mask`, `filter`, `marker`, `marker-start`, `marker-mid` and `marker-end`, unless
  `none`;
- `pattern` paints, and any paint that is not a gradient;
- `foreignObject`, and any other element of the SVG namespace not listed above;
- images that are not inlined: an absolute url, or a path outside the source folder;
- selectors with combinators, attributes or pseudo-classes (`g rect`, `a > rect`, `[fill]`, `:hover`);
- a `use` whose `href` is not `#id`, or whose references form a cycle;
- an invalid color, length, number, transform, `viewBox` or path data (`invalid color 'bluish'`,
  `invalid length 'ten' of 'width'`).

The error names the element and its line in the rendered SVG:

```txt
SVG <text> (line 3): <text> is not supported; lay out text with the 'text' blocks of the step
SVG <rect> (line 5): the property 'filter' is not supported
SVG <rect> (line 6): a paint of <pattern> is not supported
SVG <image> (line 7): the image 'https://example.com/logo.png' is not inlined; reference images of the site by a relative path
SVG <foreignObject> (line 8): <foreignObject> is not supported
SVG <style> (line 2): the CSS selector 'g rect' is not supported; use a type, class or id selector
```

A document that is not well formed XML fails with `invalid SVG: ...`, as does one whose root is not `<svg>`
(`invalid SVG: the root element is not <svg>`) or that has neither a size nor a `viewBox`
(`invalid SVG: the <svg> element has neither a width and a height nor a viewBox`).

## Requirements

None to install: Douze rasterizes the SVG and lays out the text itself, on every platform it runs on. Text needs a
font: the block's `fontfile`, or a font installed on the system.

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

During the build: `template '...' not found in '...'`, `font file '...' not found`, `image '...' not found (...)`,
`cannot read the font file (...)`, the font errors of [Fonts](#fonts) and the SVG errors of
[Supported SVG](#supported-svg).

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
