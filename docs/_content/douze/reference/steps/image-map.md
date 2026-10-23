---
title: image-map
description: "The image-map step: collects the image values of the images step into the images site value."
liquid: false
---

`uses: image-map` collects the `image` values that the [`images`](images.md) step set on its outputs
into one site value, `images`, by source url. The [`pictures`](pictures.md) step reads it to rewrite
the `<img>` of pages, and templates can read it too.

| | |
|---|---|
| Shape | reduce (no `group-by`) |
| Default `match` | every file (`**`) |
| Default workflow | no |

## What it does

The step receives every file it consumes, reads their `image` value and publishes, as site data:

- `images`: an object whose keys are the source urls of the images (`/docs/images/x.png`, the path without the
  content folder) and whose values are the `image` values (`{ source, url, width, height, format, variants }`, see
  [images](images.md#what-it-does)).

A template reads an entry by its url: `{{ images["/docs/images/x.png"].width }}`.

The step reads metadata only and emits no file: the image files themselves reach the output folder through a `write`
step that needs `images` directly. Files without an `image` value are ignored. When several outputs of the same
source carry its `image` (the original and each variant), the first one, by path, is kept: they are the same.

## Options

None. Any `with` option is an error (`unknown option '...' for step '...'`).

## Requirements

None.

## Caching

The step reads metadata only: a change of the bytes of an image that leaves its `image` value as it was does not run
it again. A page that `pictures` rewrites depends on the entries of `images` it used, not on the whole map.

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

## Related

- [images](images.md) and [pictures](pictures.md)
- [Template data](../template-data.md)
- [Images guide](../../guide/images.md)
