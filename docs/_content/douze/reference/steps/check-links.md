---
title: check-links
description: "Checks the links of the built site: every internal link must lead to a file of the build, and its fragment to an anchor of the page."
liquid: false
---
Two steps check the links of your site. `links` reads the links and the anchors of every HTML page into its data, and
`check-links` checks them against the files of the build: a link to a page, an image or a stylesheet that the build
does not write, or to a heading that the page does not have, fails the build with the page and the line.

| | `links` | `check-links` |
|---|---|---|
| Shape | map | reduce (no `group-by`) |
| Default `match` | `**/*.html` | every file |
| Default workflow | no | no |

Put `links` after [layout](layout.md), so that the links of the layouts are checked too, and give `check-links` every
file the site writes: it knows the pages, images and assets of the site by the files it receives.

```yaml title="douze.yml"
steps:
  layout:
    needs: highlight
    uses: layout
  links:
    needs: layout
    uses: links
  check-links:
    needs: links
    uses: check-links
  write:
    needs: links
    uses: write
```

`write` does not need `check-links`: the check emits no file, so it is a branch of the workflow that the files do not
wait for. A broken link fails the build all the same, with exit code 1.

## links

The step reads the tags of each page and sets two values in its data, without changing its content:

| Value | Contains |
|---|---|
| `links` | `{ url, line }` for every `href`, `src`, `srcset` candidate, `poster` and `data-src`: the url as written (entities decoded) and its line in the page. Sorted by line, then url |
| `anchors` | every `id`, and the `name` of `<a>` elements. Sorted |

Comments and the content of `<script>`, `<style>` and `<textarea>` are not read: their text is not markup. The
markup of a code sample is escaped text, so a link shown in a sample is not a link of the page.

The step has no options.

## check-links

For each link of each page, the step resolves the url like a static file server:

| Link (from `/docs/guide/index.html`) | Must exist |
|---|---|
| `/docs/setup/` | `/docs/setup/index.html` |
| `../setup/` | `/docs/setup/index.html` |
| `images/a.png` | `/docs/guide/images/a.png` |
| `/docs/setup/?tab=2` | `/docs/setup/index.html`: queries are ignored |
| `/docs/setup` | the file `/docs/setup`; with `extensionless: true`, `/docs/setup/index.html` or `/docs/setup.html` too |
| `/docs/setup/#install` | `/docs/setup/index.html`, with an anchor `install` |
| `#install` | an anchor `install` in the page itself |

- A fragment is checked when the target is a page that `links` read: a link to `/guide.pdf#page=2` checks the file
  only. `#` and `#top` are always valid, and text fragments (`#:~:text=`) are not checked. Fragments and paths are
  percent-decoded: `#caf%C3%A9` is the anchor `café`.
- Links with another scheme (`mailto:`, `tel:`, `data:`, `javascript:`) are not checked.
- External links (`https://`, `http://`, `//host/`) are checked only with `external: true`, over the network: once per
  url, 8 at a time, 10 seconds each, with a `HEAD` request (then `GET` when the server refuses `HEAD`). A failure is a
  warning, never an error, since the network changes from one build to the next.

## Options

`links` has no options. The options of `check-links`:

| Option | Type | Default | Meaning |
|---|---|---|---|
| `ignore` | string or list | none | Links not checked: globs of internal targets (`/reference/api/**`, `/i18n/**`), and url prefixes of external links (`https://example.com/`) |
| `skip` | glob or list of globs | none | Pages whose links are not checked, by output path (`legacy/**`) |
| `anchors` | boolean | `true` | Checks fragments against the anchors of the target page |
| `extensionless` | boolean | `false` | `/a/b` also leads to `/a/b/index.html` and `/a/b.html`, for a server that serves pages without their extension |
| `external` | boolean | `false` | Checks `http` and `https` links over the network; failures are warnings |
| `errors` | `fail` or `warn` | `fail` | `warn` reports broken links as warnings and the build succeeds |

## Caching

`check-links` reads the data of the files, not their content, so it runs again only when a link, an anchor or the
data of a file changes, or a file is added or removed. Editing a layout or an include that moves no link replays it
from the cache. Editing a page usually runs it again: the page's date (`page.date`, from the file) changes, and so do
the lines of the links below the edit. The check reads no content, so running it again takes milliseconds.

With `errors: warn`, and for external links, the warnings of a replayed check are reported again. External links are
not requested again until the check runs again: to check them on every build, add `incremental: false` to the step.

## Errors

Each error names the page by its output path and its source file, the link as written, and its line in the output
page:

```txt
error: check-links: /docs/guide/index.html (/_content/docs/guide.md): broken link '/docs/setup/' (line 42)
/docs/guide/index.html (/_content/docs/guide.md): missing anchor '#install' in '/docs/setup/' (line 57)
/docs/guide/index.html (/_content/docs/guide.md): missing anchor '#options' (line 80)
```

Every broken link of the site is reported, sorted by page and line. External links are warnings:

```txt
warn: check-links: /docs/guide/index.html (/_content/docs/guide.md): external link 'https://example.com/old' (line 12): 404 Not Found
```

Invalid options are errors of `douze.yml`:

```txt
douze.yml(30,3): error: option 'errors' of step 'check-links' must be 'fail' or 'warn', not 'ignore'
```

## Example

A documentation site that leaves a generated API reference out, and checks its external links on every build as
warnings:

```yaml title="douze.yml"
steps:
  pages:
    inputs: "_content/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: _content
  markdown:
    needs: permalink
    uses: markdown
  headings:
    needs: markdown
    uses: headings
  layout:
    needs: headings
    uses: layout
  links:
    needs: layout
    uses: links
  check-links:
    needs: links
    uses: check-links
    incremental: false
    with:
      ignore: ["/reference/api/**", "https://localhost"]
      external: true
  write:
    needs: links
    uses: write
```

## Related

- [headings](headings.md), which gives headings the ids that anchors link to
- [check-code](check-code.md)
- [layout](layout.md)
- [write](write.md)
