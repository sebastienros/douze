---
title: check-code
description: "Checks that code samples and generated files are well formed: YAML, JSON, XML, Liquid, and douze.yml against its schema."
liquid: false
---
The `check-code` step checks that code is well formed, with parsers built into Douze: the fenced code blocks of your
pages, so that a sample a reader copies is valid, and generated files such as feeds and JSON exports. A `douze.yml`
sample is also checked against the workflow schema. Files pass through unchanged.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | `**/*.html` |
| Default workflow | no |

## What it does

What the step checks depends on the file:

- **An HTML page**: its code fences, as the [markdown](markdown.md) step writes them (`<pre data-lang="yaml">`). Put
  the step after `markdown` and before [highlight](highlight.md), which replaces the fences with highlighted markup.
  The code is checked as written: lines and columns in errors are those of the fence.
- **Any other file**: its whole content, by the language of its extension: `/feed.xml` is checked as XML. Select
  these files with `match`, after [layout](layout.md) for generated files. A file whose extension has no validator
  passes through unchecked.

| Language | Checks | Fence languages and extensions |
|---|---|---|
| `yaml` | every document of the stream, and no key defined twice in a mapping; tabs in indentation are errors | `yaml`, `yml` |
| `json` | one JSON value, no comments, no property defined twice | `json`, `webmanifest` |
| `jsonc` | the same, with comments and trailing commas | `jsonc` |
| `jsonl` | a JSON value on every line that is not blank (JSON Lines) | `jsonl`, `ndjson` |
| `xml` | well-formed XML; in a fence, several elements are allowed. A DTD is ignored and nothing is downloaded | `xml`, `svg`, `xsl`, `xslt`, `rss`, `atom` |
| `liquid` | the template parses, with Douze's tags and filters and the site's [shortcodes](../../guide/shortcodes.md) | `liquid` |

### douze.yml samples

A YAML fence whose title is `douze.yml` (` ```yaml title="douze.yml" `), and a YAML file named `douze.yml`, is also
checked as a Douze workflow, with the rules of `douze build`: unknown keys of `site`, `markdown`, `vars` and of a step,
invalid values, unknown `uses`, unknown or invalid `with` options, unknown `tool`. `needs` that name steps the sample
does not show are not errors, since a sample shows part of a workflow. A sample of steps without the `steps:` line
(every top-level key is a step with `uses`, `run`, `needs` or `inputs`) is checked as the content of `steps`.

### Leaving a sample out

A sample that is wrong on purpose, or that uses a shortcode your site does not have, takes the `nocheck` word in its
meta:

````md
```yaml nocheck
title: [unclosed
```
````

The highlighter ignores the word. `ignore` leaves whole pages or files out.

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `languages` | string or list | every language | The validators that run: `yaml`, `json`, `jsonc`, `jsonl`, `xml`, `liquid` |
| `aliases` | mapping | none | More fence languages or extensions for a validator: `{ compose: yaml, plist: xml }`. A built-in alias (`svg`) cannot change validator |
| `workflow` | string or list | `douze.yml` | The fence titles and file names checked as a Douze workflow; `[]` for none. A title with folders (`site/douze.yml`) matches by its file name |
| `skip` | string | `nocheck` | The meta word that leaves a fence out |
| `ignore` | glob or list of globs | none | Pages and files left out, by output path |
| `errors` | `fail` or `warn` | `fail` | `warn` reports the errors as warnings and the build succeeds |

## Caching

The step checks a page again only when its content changes; an unchanged page is replayed from the cache. With
`errors: warn`, the warnings of a replayed page are reported again, so a build from the cache shows the same warnings
as a full build. Adding or changing a shortcode checks the Liquid samples again.

## Errors

Each error names the page by its source file, the fence by its language and its number among the checked fences of the
page, its first line, then the line and column in the fence:

```txt
error: check-code: /guide/assets/index.html: /_content/guide/assets.md: yaml fence 3 ('steps:'): line 4, column 5: mapping values are not allowed in this context
error: check-code: /guide/images/index.html: /_content/guide/images.md: yaml fence 2 ('markdown:'): line 3, column 5: unknown key 'widthz' in 'markdown.images'; expected 'widths', 'formats', 'quality', 'output' or 'loading'
error: check-code: /reference/liquid/index.html: /_content/reference/liquid.md: liquid fence 21 ('{% for p in collections.all | locale_pages %}'): line 1, column 7: Invalid 'for' tag
error: check-code: /feed.xml: /feed.xml (/_content/feed.xml.liquid): xml: line 12, column 3: The 'item' start tag on line 9 position 4 does not match the end tag of 'channel'.
```

Every error of a page is reported. Like any step, `check-code` stops at the first page that fails; `--keep-going`
checks every page and reports them all. Invalid options are errors of `douze.yml`:

```txt
douze.yml(20,3): error: unknown language 'toml' in option 'languages' of step 'check-code'; expected 'yaml', 'json', 'jsonc', 'jsonl', 'xml', 'liquid'
douze.yml(20,3): error: alias 'svg' of step 'check-code' is checked by 'xml' already
```

## Example

The samples of the pages are checked before highlighting, and the feed and the JSON export after the layouts:

```yaml title="douze.yml"
steps:
  pages:
    inputs: "_content/**"
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
  check-code:
    needs: markdown
    uses: check-code
    with:
      aliases:
        compose: yaml
  highlight:
    needs: check-code
    uses: highlight
  layout:
    needs: highlight
    uses: layout
  check-files:
    needs: layout
    uses: check-code
    match: ["**/*.xml", "**/*.json"]
  write:
    needs: check-files
    uses: write
```

## Rules of your own

`check-code` checks syntax. For rules beyond it (a CSS linter, an HTML validator, a spelling check), run the tool as a
[command step](../commands.md) on a branch of the workflow: a step that no other step needs. A non-zero exit code, or
output on stderr (`stderr: fail`, the default), fails the build with the tool's output. On a branch, the files the
command consumes do not replace the files that continue to `write`.

```yaml title="douze.yml"
steps:
  pages:
    inputs: "_content/**"
    uses: frontmatter
  markdown:
    needs: pages
    uses: markdown
  layout:
    needs: markdown
    uses: layout
  lint-css:
    needs: pages
    match: "**/*.css"
    run: "npx stylelint \"{{ inputs }}/**/*.css\""
  validate-html:
    needs: layout
    match: "**/*.html"
    run: "npx html-validate {{ inputs }}"
  write:
    needs: layout
    uses: write
```

Both are `batch` steps (the default mode): one process checks every file. Their results are cached like other
steps, so an unchanged site does not run them again.

## Related

- [markdown](markdown.md)
- [highlight](highlight.md)
- [check-links](check-links.md)
- [Command steps](../../guide/command-steps.md)
- [Configuration](../configuration.md)
