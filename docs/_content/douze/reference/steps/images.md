---
title: images
description: "The images step: responsive variants and a fingerprinted copy of each image file."
---

`uses: images` turns each image file into its responsive variants (the widths and formats you ask for) and a
fingerprinted copy of the original, and writes the description of the result in the `image` value of every file it
emits. With [`image-map`](image-map.md) and [`pictures`](pictures.md), it gives the
images of Markdown pages (`![alt](images/x.png)`) the same markup as the `image` tag.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | `**/*.{png,jpg,jpeg,webp,avif,gif,svg}` |
| Default workflow | no |

## What it does

For each image it consumes, the step emits these files instead of the image:

- the original, with a fingerprinted name: `<name>.<hash>.<ext>`;
- one variant per width and format: `<name>-<width>.<hash>.<ext>`, the hash being the first 8 characters of the
  hash of the variant's content, so that a changed image gets new names.

All of them are written to the `output` folder (`/assets/images/` by default). Variants are never wider than the
source (a width larger than the source gives the source's width), and metadata (EXIF, XMP, ICC) is stripped. Without
`widths`, the only width is the source's; without `formats`, the only format is the source's.

An SVG or a GIF is only copied, as `<name>.<hash>.<ext>`, without libvips; its size is read from the file (an SVG's
`width` and `height`, or its `viewBox`).

Every file the step emits carries the same `image` value:

| Key | Meaning |
|---|---|
| `image.source` | the url of the source image: its path without the content folder (`/docs/images/x.png`) |
| `image.url` | the url of the fingerprinted original |
| `image.width`, `image.height` | the size of the source, as displayed (EXIF orientation applied) |
| `image.format` | the format of the source (`png`, `jpeg`, `webp`, `avif`, `gif`, `svg`) |
| `image.variants` | a list of `{ url, width, height, format }`, by format in the order of `formats`, then by width ascending |

The files that leave the step are images, not pages: a `write` step must need it for them to reach the output folder.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `widths` | integer or list of integers | `markdown.images.widths`, else the source's width | the widths of the variants, in pixels |
| `formats` | string or list of strings | `markdown.images.formats`, else the source's format | the formats of the variants: `webp`, `avif`, `png`, `jpeg` (`jpg` is `jpeg`) |
| `quality` | integer, 1 to 100 | `markdown.images.quality`, else `80` | the quality of the lossy formats (`webp`, `avif`, `jpeg`) |
| `output` | string | `markdown.images.output`, else `/assets/images/` | the folder of the variants and of the copies |

The defaults come from the `images` settings of the [`markdown` section](../configuration.md) of `douze.yml`,
which the `image` tag and the `image_url` filter use too.

## Requirements

libvips, included in the native packages of Douze for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`
and `osx-arm64`. On other platforms, put libvips 8.18 (`libvips.so.42`, `libvips.42.dylib` or `libvips-42.dll`) next
to the `douze` executable or on the library path. A workflow with an `images` step checks it before building, and
`douze doctor` reports it.

## Caching

Each image is a unit: its result is reused while the image, the step's options and the `images` settings of the
`markdown` section do not change.

## Errors

```
douze.yml(2,3): error: option 'formats' of step 'images' must be a list of image formats ('webp', 'avif', 'png' or 'jpeg')
douze.yml(3,3): error: step 'images' needs positive widths and a quality from 1 to 100
douze.yml(2,3): error: unknown option 'width' for step 'images'; did you mean 'widths'?
```

A file that is not an image Douze reads fails its unit:
`'/docs/a.bmp' is not an image format douze reads (png, jpeg, webp, avif, gif, svg)`.

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

`docs/images/x.png` (800 pixels wide) gives `/assets/images/x.<hash>.png`, `x-320.<hash>.webp`,
`x-640.<hash>.webp`, `x-320.<hash>.png` and `x-640.<hash>.png`.

## Related

- [image-map](image-map.md) and [pictures](pictures.md)
- [The `image` tag and the image filters](../liquid.md)
- [The `markdown.images` settings](../configuration.md)
- [Images guide](../../guide/images.md)
- [`douze doctor`](../cli.md)
