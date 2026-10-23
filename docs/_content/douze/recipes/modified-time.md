---
title: Add last modified time
description: Show when a page was last changed, from page.date, and set it from the last Git commit of each file in CI.
---

The `frontmatter` step sets `page.date` on every page to the modification time of its source file. A layout can show
it as the last update of the page. On a developer's machine that is the time of the last save; in a fresh clone, such
as a CI checkout, every file has the time of the checkout, so the site needs the dates from Git instead.

## Show the date

```liquid
<article>
  {{ content }}
  <footer>
    Last updated:
    <time datetime="{{ page.date | date_to_xmlschema | raw }}">{{ page.date | date: "%B %-d, %Y" }}</time>
  </footer>
</article>
```

```html
<footer>
  Last updated:
  <time datetime="2025-05-06T07:08:09+00:00">May 6, 2025</time>
</footer>
```

`| raw` keeps the `+` of the offset as it is: the `liquid` step encodes its output for HTML, which writes `+` as
`&#x2B;` (valid, but harder to read).

`page.date` is separate from the front matter `date`, which is the publication date of a post. A collection is sorted
by `date` when a page has one, else by `page.date`.

## Use the date of the last commit

Set the modification time of every tracked file to the date of its last commit before the build. This script works
with GNU `touch` (Linux) and BSD `touch` (macOS):

```sh title="scripts/git-mtime.sh"
#!/bin/sh
# Sets the modification time of every file tracked under _content to the date of its last commit.
set -eu
git ls-files -z _content | xargs -0 -n 1 sh -c '
  date=$(TZ=UTC git log -1 --format=%cd --date=format-local:%Y-%m-%dT%H:%M:%SZ -- "$1")
  [ -n "$date" ] && touch -d "$date" "$1"
' sh
```

```sh
chmod +x scripts/git-mtime.sh
./scripts/git-mtime.sh
douze
```

It runs one `git log` per file, which takes a few seconds for a thousand files. Files that are not committed yet keep
their time.

In GitHub Actions, the checkout must have the history, or every file gets the date of the last commit:

```yaml
- uses: actions/checkout@v7
  with:
    fetch-depth: 0
- run: ./scripts/git-mtime.sh
```

:::tip[The dates and the cache]
`page.date` is part of what a page's cache entry depends on: a file whose modification time changed runs again even
when its content did not. Setting the dates from Git also keeps them stable from one CI build to the next, so a
restored [cache](../guide/cache.md) is reused.
:::

## Set the date by hand

A page can also carry its own date in front matter, under another name, and the layout prefer it:

```md
---
title: Install
updated: 2026-03-01
---
```

```liquid
{%- assign updated = updated | default: page.date %}
<time datetime="{{ updated | date_to_xmlschema | raw }}">{{ updated | date: "%B %-d, %Y" }}</time>
```

## See also

- [Template data](../reference/template-data.md)
- [frontmatter step](../reference/steps/frontmatter.md)
- [Collections](../guide/collections.md)
- [Add a sitemap](sitemap.md)
- [Deploy to GitHub Pages](github-pages.md)
