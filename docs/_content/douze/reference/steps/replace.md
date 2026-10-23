---
title: replace
description: "Replaces literal tokens in pages, code blocks included, with values rendered from their data."
liquid: false
---
The `replace` step replaces literal tokens in the content of each page, code blocks included, with values rendered
from the page's data. It runs before Liquid, so it also reaches the fenced code blocks that Liquid leaves alone, for
instance to write the current version in installation commands.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | `**/*.md`, `**/*.html` |
| Default workflow | no |

## What it does

For each text file it consumes, the step looks for every token of `tokens`, in the order they are listed, with an
ordinal (exact, case sensitive) search. For each token the file contains, it renders the token's value as a Liquid
template with the file's data, and replaces every occurrence of the token with the result. Tokens the file does not
contain are not rendered.

A file that contains no token, a binary file, and a file that `skip` matches pass through unchanged.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `tokens` | mapping of strings to strings | required | Each token and its value, a Liquid template rendered with the file's data (`"%VERSION%": "{{ versions.current }}"`). Numbers and booleans are used as text |
| `skip` | glob or list of globs | none | Files that pass through without replacements |

## Caching

A file depends on the data its token values read, and only when it contains the token: changing the version renders
again the pages that contain `%VERSION%`, not the others.

## Errors

```
douze.yml(4,3): error: step 'versions' needs the option 'tokens', a mapping of tokens to values
douze.yml(4,3): error: option 'tokens' of step 'versions' has an empty token
douze.yml(4,3): error: the value of token '%VERSION%' of step 'versions' must be a string
```

## Example

```yaml
site:
  data: data

steps:
  site-data:
    inputs: "data/*.yml"
    uses: data
    with:
      scope: site
  pages:
    inputs: "docs/**/*.md"
    needs: site-data
    uses: frontmatter
  versions:
    needs: pages
    uses: replace
    with:
      tokens:
        "%VERSION%": "{{ release.current }}"
      skip: "docs/release-notes/**"
  permalink:
    needs: versions
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

With `data/release.yml` containing `current: 2.1.0`, a page with

````markdown
```bash
dotnet add package Contoso --version %VERSION%
```
````

renders `dotnet add package Contoso --version 2.1.0`.

## Related

- [liquid](liquid.md)
- [Template data](../template-data.md)
- [Globs](../configuration.md#globs)
