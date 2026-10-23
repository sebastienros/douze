---
title: data
description: "Reads YAML, JSON and XML data files into site data or folder data."
---
The `data` step reads data files (YAML, JSON, XML) and turns them into values that templates and later steps can
read. With `scope: site`, each file is a site value named after its path. With `scope: folder`, the top-level keys of
each file apply to the files of its folder and its subfolders. The step emits no files: the data files it consumes
are not written to the output folder.

| | |
|---|---|
| Shape | reduce (no `group-by`) |
| Default `match` | `**/*.{yml,yaml,json,xml}` |
| Default workflow | yes, as `site-data` (`scope: site`, when there is a data folder) and `folder-data` (`scope: folder`) |

## What it does

The step parses every file it consumes (`.yml` and `.yaml` as YAML, `.json` as JSON, `.xml` as XML), in parallel,
then assembles the values in path order.

### Site data

With `scope: site`, each file is a site value named after its path relative to `root`, without its extension:

| File (with `root` the data folder `_data`) | Value |
|---|---|
| `_data/colors.json` | `colors` |
| `_data/pkgs/Contoso.Hosting.json` | `pkgs["Contoso.Hosting"]` |
| `_data/i18n/en.yml` | `i18n.en` |

A file can contain anything: a mapping, a list or a single value. A folder is an object whose keys are its files and
subfolders. A name is defined once: two files that define the same name (`pkgs.json` and `pkgs/a.json`) are an error.

### Folder data

With `scope: folder`, every file must contain a mapping (an empty file is ignored). Its top-level keys apply to every
file of its folder and of the folders below it, for instance `layout`, `tags` or `permalink` for a whole section.
Files of the same folder are merged in path order, a later file overriding the keys of an earlier one, and the data
of a folder hides the data of its parent folders.

### How the values reach templates

Values flow to the steps that `need` the `data` step, directly or through other steps. A name in a template resolves
to the file's own data first, then folder data, then site data. See [name resolution](../template-data.md).

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `scope` | `site` or `folder` | required | Site values named after each file, or values that apply to a folder |
| `root` | folder | the data folder (`site`), the content folder (`folder`) | The folder that file paths are relative to. Every consumed file must be under it. With `scope: site`, it decides the names of the values (`pkgs/a.json` under `root` is `pkgs.a`) |

## Errors

Validation errors stop the build before it starts (exit code 2):

```
douze.yml(3,3): error: step 'site-data' needs the option 'scope'
douze.yml(2,3): error: option 'scope' of step 'site-data' must be 'site' or 'folder', not 'page'
```

Build errors (exit code 1):

```
error: site-data: data key 'pkgs' is defined by both '/_data/pkgs.json' and '/_data/pkgs/a.json'
error: folder-data: folder data file '/_content/list.yml' must contain a mapping
error: site-data: data file '/other/a.json' is not under the root '/_data/'
error: site-data: unsupported data file '/_data/a.txt': use .yml, .yaml, .json or .xml
```

## XML files

An XML file is converted to objects: the document is an object with one key, the name of its root element. Each
element is `{ name, attributes, elements }`, plus `text` when it has text and no child elements.

## Example

```yaml
site:
  content: docs
  data: data

steps:
  site-data:
    inputs: "data/**/*.{yml,json}"
    uses: data
    with:
      scope: site
  folder-data:
    inputs: "docs/**/*.yml"
    uses: data
    with:
      scope: folder
      root: docs
  pages:
    inputs: ["docs/**", "!docs/**/*.yml"]
    needs: [site-data, folder-data]
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: docs
  render:
    needs: permalink
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  write:
    needs: markdown
    uses: write
```

`data/release.yml` containing `current: 2.1.0` is then available to every page:

```liquid
Version {{ release.current }}
```

## Related

- [Data files](../../guide/data.md)
- [parse](parse.md): reads a data file into the data of that file, instead of site or folder data
- [Template data](../template-data.md)
- [douze.yml](../configuration.md)
