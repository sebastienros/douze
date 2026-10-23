---
title: write
description: Writes files to the output folder and deletes stale outputs.
---

`uses: write` writes files to the output folder. It consumes every file that reaches it, so the files no other step
claimed (images, stylesheets, scripts) are copied as they are, and it passes each written file on, so that steps can
run after it (a search indexer that reads the whole site, for instance).

| | |
|---|---|
| Shape | map |
| Default `match` | every file (`**`) |
| Default workflow | yes, as step `write` (the last step) |

## What it does

- Writes each file at its path in the output folder (`-o`, or `site.output`), creating folders as needed. The path is
  the one the steps before it gave the file: the `permalink` step's output path for pages, the source path for the
  files that went through no step that moves them.
- Two files with the same output path are an error, unless they are the same file (same path, content and data): an
  image variant produced for two pages is written once.
- A file already written to that path by an earlier `write` step is not written again.
- Passes each file on with its content read back from the output folder rather than kept in memory.
- Reads no data: it does not wait for the steps that collect data across pages.

A workflow without a `write` step builds with a warning: `no 'write' step: nothing will be written to the output
folder`.

### Stale outputs

With the cache, each build records what it wrote. Once a build succeeds, it deletes the output files that previous
builds wrote and this one did not (the pages of deleted or renamed sources, the pages a pagination no longer has) and
the folders they leave empty. A file that something else changed since Douze wrote it is kept, and nothing is deleted
when the build fails or runs with `--no-cache`.

## Options

None. Any `with` key is an error (`unknown option '...' for step 'write'`).

## Caching

A replayed unit writes nothing: its file is reused while it is in the output folder as Douze wrote it. A file that is
missing from the output folder, or that was changed there, is written again.

## Errors

```
output '/index.html' is produced by both '/_content/index.html' and '/_content/index.md'
output '/feed.xml' is produced by both '/_content/feed.xml.liquid' and step 'feed'
```

A file that comes from a source is named by its source path, a file a step created by the step's name.

## Example

Write the site, then index it with a command step that reads the written files:

```yaml title="douze.yml"
steps:
  pages:
    inputs: "**/*.md"
    uses: frontmatter
  markdown:
    needs: pages
    uses: markdown
  assets:
    inputs: "assets/**"
    uses: write
  write:
    needs: markdown
    uses: write
  search:
    needs: [write, assets]
    run: "npx pagefind --site {{ site }}"
```

## Related

- [Command line: `-o`, `--no-cache`](../cli.md)
- [Command steps](../commands.md)
- [layout](layout.md)
- Guides: [Cache and incremental builds](../../guide/cache.md), [Feeds, sitemaps and other outputs](../../guide/outputs.md)
