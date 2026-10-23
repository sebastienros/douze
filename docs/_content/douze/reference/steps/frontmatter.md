---
title: frontmatter
description: "Moves the YAML front matter of pages into their data and sets page.date."
---
The `frontmatter` step moves the YAML front matter of a page into its data and removes it from the content. It also
sets `page.date` to the modification time of the source file, and can check that every page has some keys.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | `**/*.{md,html,liquid}` |
| Default workflow | yes, as `pages` |

## What it does

The front matter is a YAML mapping between two `---` lines at the start of the file:

```markdown
---
title: Getting started
tags: [guide]
---
# Getting started
```

For each text file it consumes, the step:

- sets `page.date` to the last write time of the source file (UTC), when the file has one;
- merges the front matter values into the file's data: a nested object (`page: { ... }`) is merged into the existing
  one, lists and values replace the existing ones;
- removes the front matter block from the content. A file without front matter keeps its content as it is.

Binary files, and the files that `match` does not select, pass through unchanged. Front matter that is not a mapping
is an error (`the front matter must be a mapping`).

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `require` | string or list of strings | none | Keys every consumed file must have, in its front matter or in folder or site data. A key whose value is `null` counts as missing |

## Errors

A missing required key fails the file:

```
error: pages: /_content/nofm.md: required key 'description' is missing
error: pages: /_content/p.html: required keys 'title', 'description' are missing
```

## Example

```yaml
steps:
  pages:
    inputs: "**/*.md"
    uses: frontmatter
    with:
      require: [title, description]
  permalink:
    needs: pages
    uses: permalink
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

## Related

- [Pages](../../guide/pages.md)
- [Template data](../template-data.md): `page.date` and the name resolution order
- [data](data.md): folder data that `require` also checks
- [collect](collect.md): sorts pages by `date`, else `page.date`
