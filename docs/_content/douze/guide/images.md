---
title: Images
description: Responsive images with the image tag, the image_url and image_size filters, and the images, image-map and pictures steps for Markdown images.
liquid: false
---

Douze resizes and converts images at build time with [libvips](https://www.libvips.org), and writes the markup that
goes with them: `<picture>`, `srcset`, `width` and `height`. You can do this in two ways. The `image` tag works in
any template. Three workflow steps do the same for the plain `![alt](src)` images of Markdown pages. This page covers
both, the filters, light and dark images, and what libvips is needed for.

## The image tag

```liquid
{% image "images/dashboard.png", alt: "The dashboard", widths: [640, 1280], formats: ["webp", "png"], sizes: "(min-width: 60em) 50vw, 100vw", class: "figure" %}
```

```html
<picture>
  <source type="image/webp" srcset="/assets/images/dashboard-640.65ca7992.webp 640w, /assets/images/dashboard-1280.0b5361a5.webp 1280w" sizes="(min-width: 60em) 50vw, 100vw">
  <img src="/assets/images/dashboard-1280.ee42001a.png" srcset="/assets/images/dashboard-640.f66c56a9.png 640w, /assets/images/dashboard-1280.ee42001a.png 1280w" sizes="(min-width: 60em) 50vw, 100vw" width="1280" height="720" alt="The dashboard" class="figure" loading="lazy" decoding="async">
</picture>
```

- The source is relative to the page's folder, or to the content folder when it starts with `/`. It is a dependency of
  the page: changing the image renders the page again.
- There is a `<source>` per format but the last, and an `<img>` of the last format at its largest width. One format
  gives a plain `<img>`, with a `srcset` when there are several widths.
- Each variant is written to `/assets/images/` (`output`) as `<name>-<width>.<hash>.<ext>`. The hash is the variant's,
  so a changed image gets new urls and can be cached forever. A variant that several pages use is written once.
- Variants are never wider than the source. EXIF, XMP and ICC metadata are stripped.
- `width` and `height` are always set, so the page does not shift while images load. `loading` is `lazy` and
  `decoding` is `async` unless you set them.
- Any other named argument is an attribute (`data-zoom: true` writes `data-zoom`).

Arguments take strings, numbers, `true`, `false`, lists and variables (`page.hero`). `formats` are `webp`, `avif`,
`png` and `jpeg`, and `quality` defaults to 80.

### Defaults

`widths`, `formats`, `quality`, `output` and `loading` default to the `images` settings of the `markdown` section,
so a site sets them once:

```yaml title="douze.yml"
markdown:
  images:
    widths: [640, 1280]
    formats: [webp]
    quality: 80
    output: assets/images
    loading: lazy
```

Without widths, a variant has the source's width, and without formats, the source's format.

### Light and dark images

```liquid
{% image light: "images/flow-light.png", dark: "images/flow-dark.png", alt: "The flow" %}
```

This gives one `<picture>` whose dark sources have `media="(prefers-color-scheme: dark)"`. A site that toggles a
theme attribute instead uses `theme: "attribute"`: the tag writes two elements, with `data-theme="light"` and
`data-theme="dark"`, and the site's CSS hides one.

### SVG and GIF

An SVG or a GIF is copied as it is, fingerprinted (`arch.ca50de10.svg`), with its size read from the file: an SVG's
`width` and `height`, or its `viewBox`. These need no libvips.

## Filters

```liquid
<meta property="og:image" content="{{ "images/hero.png" | image_url: width: 1200, format: "jpeg" | absolute_url }}">
{% assign size = "images/hero.png" | image_size %}
<div style="aspect-ratio: {{ size.width }} / {{ size.height }}">
```

`image_url` makes one variant and returns its url, for meta tags and CSS. It takes `width`, `format`, `quality` and
`output`. `image_size` returns the source's `{ width, height }`.

## Markdown images

For the images of Markdown pages (`![The hero](images/hero.png)`), three steps do what the tag does, without a tag:

```yaml title="douze.yml"
steps:
  pages:
    inputs: "_content/**/*.md"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  render:
    needs: permalink
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  images:
    inputs: "_content/**/*.{png,jpg,jpeg,webp,avif,gif,svg}"
    uses: images
    with:
      widths: [640, 1280]
      formats: [webp, png]
  image-map:
    needs: images
    uses: image-map
  pictures:
    needs: [markdown, image-map]
    uses: pictures
  layout:
    needs: pictures
    uses: layout
  write:
    needs: [layout, images]
    uses: write
```

- `images` replaces each image file with its variants and a fingerprinted copy of the original, each with an `image`
  value: `{ source, url, width, height, format, variants: [{ url, width, height, format }] }`.
- `image-map` collects them as `images`, by source url.
- `pictures` rewrites each `<img>` whose image it knows into a `<picture>`, as the tag does, and keeps the other
  attributes (`title`).

`write` needs `images` so that the variants are written. A page depends on the images it shows only.

`images` is site data for the steps after `image-map`. A layout, which runs after it, can read it:

```liquid title="_includes/doc.liquid"
{% assign hero = images["/docs/images/hero.png"] %}
<meta property="og:image" content="{{ hero.url | absolute_url }}">
```

The `render` step of this workflow runs before `image-map`, so the Liquid of a page does not see `images`.

## libvips

Image processing uses libvips through NetVips. The native builds of Douze include it, next to the `douze` executable.
Without it:

- a workflow with an `images` or `social-image` step fails before building, and says how to get libvips;
- the `image` tag and the `image_url` and `image_size` filters fail only the pages that use them on an image other
  than an SVG or a GIF.

`douze doctor` checks it:

```
STEP    REQUIREMENT  STATUS  DETAILS
images  libvips      ok      8.18.7
```

## See also

- [images step reference](../reference/steps/images.md)
- [image-map step reference](../reference/steps/image-map.md)
- [pictures step reference](../reference/steps/pictures.md)
- [Liquid filters and tags](../reference/liquid.md)
- [Social images](social-images.md)
- [Styles, scripts and assets](assets.md)
