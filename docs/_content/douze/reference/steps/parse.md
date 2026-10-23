---
title: parse
description: "Parses a data file into the data of that same file, under one key."
---
The `parse` step reads the content of a data file (YAML, JSON, XML) into the data of that same file, under one key.
The file keeps its content and goes on through the workflow, so a later step can render a page from it, for
instance a `liquid` step with a `template` and `group-by: file`.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | `**/*.{json,yml,yaml,xml}` |
| Default workflow | no |

## What it does

The step parses the file by its extension (`.yml` and `.yaml` as YAML, `.json` as JSON, `.xml` as XML) and sets the
result on the file's data under `as`. XML is converted as the [`data`](data.md#xml-files) step converts
it. Another extension fails the file:

```
error: packages: unsupported data file '/data/packages/a.txt': use .yml, .yaml, .json or .xml
```

Unlike [`data`](data.md), the values are not site or folder data: only the file itself, and the steps
it reaches, see them.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `as` | string | `data` | The key of the file's data that holds the parsed value |

## Example

One page per JSON file of `data/packages`:

```yaml
steps:
  packages:
    inputs: "data/packages/*.json"
    uses: parse
    with:
      as: package
  package-pages:
    needs: packages
    uses: liquid
    group-by: file
    with:
      template: package.liquid
      permalink: "packages/{{ group.files[0].data.package.name }}/"
  write:
    needs: package-pages
    uses: write
```

`_includes/package.liquid`:

```liquid
<h1>{{ group.files[0].data.package.name }} {{ group.files[0].data.package.version }}</h1>
```

`data/packages/contoso.json` with `{"name":"contoso","version":"1.2.0"}` is written to `packages/contoso/index.html`.

## Related

- [data](data.md)
- [liquid](liquid.md): `template` and `group-by`
- [Data files](../../guide/data.md)
