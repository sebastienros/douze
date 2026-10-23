---
title: Deploy to GitHub Pages
description: Build a Douze site in GitHub Actions with the install script and deploy it with actions/deploy-pages.
---

GitHub Pages serves a site that a GitHub Actions workflow builds and uploads. The workflow below installs Douze with
its install script, builds the site of the `docs` folder, and deploys its output folder. This documentation site is
deployed the same way.

## Enable Pages

In the repository's **Settings > Pages**, set **Source** to **GitHub Actions**.

## Add the workflow

```yaml title=".github/workflows/pages.yml"
name: Deploy site

on:
  push:
    branches: [main]
  workflow_dispatch:

permissions:
  contents: read
  pages: write
  id-token: write

concurrency:
  group: pages
  cancel-in-progress: false

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7

      - name: Install douze
        run: |
          curl -fsSL https://raw.githubusercontent.com/sebastienros/douze/main/install.sh | sh -s -- --preview --no-modify-path
          echo "$HOME/.douze/bin" >> "$GITHUB_PATH"

      - name: Build
        run: douze -s docs -o docs/_site

      - uses: actions/upload-pages-artifact@v5
        with:
          path: docs/_site

  deploy:
    needs: build
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    steps:
      - id: deployment
        uses: actions/deploy-pages@v5
```

What each part does:

- **Install douze**: the install script downloads the native build for the runner into `~/.douze`, whose `bin` folder
  holds `douze`. `--preview` installs the latest preview (there is no stable release yet), and `--no-modify-path`
  skips the shell profile, which a CI job does not read; `$GITHUB_PATH` adds the folder to the `PATH` of the next
  steps. Add `--version <version>` to pin a version.
- **Build**: `douze -s docs` builds the site whose source folder is `docs`, with `docs/douze.yml` when it exists.
  `-o` is relative to the current folder, here the repository root, so `-o docs/_site` writes the output next to the
  sources; without `-o` or `site.output`, it would be `./_site`. In CI Douze prints one line per step instead of the
  live view, and exits with 1 when the build has errors, which fails the job.
- **Deploy**: `upload-pages-artifact` packs `docs/_site`, and `deploy-pages` publishes it.

For a site at the root of the repository, run `douze` and upload `_site`.

## Reuse the cache between builds

Each build of the workflow above starts from scratch. To build only what changed, restore the
[disk cache](../guide/cache.md), `docs/.douze`, which also holds the tools Douze downloads (Pagefind, the language
servers of the hover steps). Add this step before the build:

```yaml
      - name: Restore the cache
        uses: actions/cache@v6
        with:
          path: docs/.douze
          key: douze-${{ github.sha }}
          restore-keys: douze-
```

The key changes with every commit, so each build saves a new cache, and `restore-keys` restores the latest one.

A fresh checkout gives every file the time of the checkout, and `page.date`, the modification time, is part of what a
page's cache entry depends on. To reuse the cache entries of unchanged pages, and to get the right `page.date`, set
each file's time to its last commit with the script of [Add last modified time](modified-time.md), which needs the
history:

```yaml
      - uses: actions/checkout@v7
        with:
          fetch-depth: 0
      # ...
      - name: Set page dates from Git
        run: ./scripts/git-mtime.sh
```

The script lists the files of `_content`; for the site of `docs`, change it to the content folder, such as
`docs/_content`.

## Sites served under a path

A project site is served under the repository's name: `https://<user>.github.io/<repository>/`. Root-relative links
(`/guide/`) then miss the `/<repository>` prefix. Douze does not rewrite links for a base path, but the output path of
a page follows its folder, so the simplest fix is to put the pages in a folder named after the repository:

```
docs/
  douze.yml
  _content/
    my-site/
      index.md
      guide.md
```

`_content/my-site/guide.md` is written to `docs/_site/my-site/guide/index.html`, its `url` starts with `/my-site/`, and
so do the links that Douze makes from `.md` links between pages (`[Guide](guide.md)` is `/my-site/guide/`). Upload the
folder of the repository:

```yaml
      - uses: actions/upload-pages-artifact@v5
        with:
          path: docs/_site/my-site
```

`douze serve -s docs` then serves the site at `http://localhost:8080/my-site/`, as GitHub Pages does. This
documentation site is built this way.

Links written by hand in layouts can also use `relative_url`, which adds the path of `site.url`:

```yaml title="docs/douze.yml"
site:
  url: https://octocat.github.io/my-site
```

```liquid
<link href="{{ '/assets/site.css' | relative_url }}" rel="stylesheet">
```

```html
<link href="/my-site/assets/site.css" rel="stylesheet">
```

`relative_url` does not apply to Markdown links, nor to the links Douze makes from `.md` links: with this approach,
write Markdown links as relative URLs (`../guide/`) rather than links to `.md` files.

## Other deployment targets

The same steps work on any CI that runs Linux, macOS or Windows: install Douze (`install.ps1` on Windows), build, and
upload the output folder. The script needs `curl` or `wget`, and `unzip`, `bsdtar` or `python3`; the GitHub runners
have them. To build in a container, see [Build your site with Docker](docker.md).

## See also

- [Installation](../guide/install.md)
- [Cache and incremental builds](../guide/cache.md)
- [Develop and build](../guide/develop-and-build.md)
- [Command line](../reference/cli.md)
