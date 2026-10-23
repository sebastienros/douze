---
title: Styles, scripts and assets
description: How Douze publishes stylesheets, scripts, fonts and other files, and how to bundle and minify CSS with a tool step.
liquid: false
---

Douze does not bundle or transform assets by default. Stylesheets, scripts, fonts, icons and downloads are files of
the content folder, and the default workflow copies them to the output folder as they are, at the same path. A
command step can process them when a site needs it, and `douze serve` reloads them in the browser as you edit them.

## Files copied as they are

Any file of the content folder that no step consumes is written unchanged:

```txt
_content/
├── css/site.css          → _site/css/site.css
├── js/menu.js            → _site/js/menu.js
├── fonts/Inter.woff2     → _site/fonts/Inter.woff2
├── favicon.svg           → _site/favicon.svg
└── downloads/guide.pdf   → _site/downloads/guide.pdf
```

Link them with root-relative URLs from the layouts:

```liquid title="_includes/base.liquid"
<link rel="icon" href="/favicon.svg" type="image/svg+xml">
<link rel="stylesheet" href="/css/site.css">
<script src="/js/menu.js" defer></script>
```

For a site served under a path (`site.url: https://example.com/docs`), use `relative_url`:
`{{ "/css/site.css" | relative_url }}`.

A few extensions are not copied as they are, because steps of the default workflow consume them:

- `.md`, `.html` and `.liquid` files are pages or templates, and `.txt` files are rendered as Liquid.
- `.json`, `.xml`, `.yml` and `.yaml` files are folder data, not published. To publish one, add `.liquid` to its
  name: `manifest.json.liquid` is written as `manifest.json`, unchanged as long as it contains no `{{` or `{%`.

## Fingerprinting

Douze does not rename stylesheets or scripts with a hash of their content. Images made by the `image` tag and the
`images` step are fingerprinted (`dashboard-640.3f2a1c9b.webp`), so that a changed image gets a new URL. See
[Images](images.md).

For stylesheets and scripts, rely on the cache headers of your host, or add a version to the URL yourself, for
instance from site data: `/css/site.css?v={{ site.version }}`.

## Bundle and minify CSS with Lightning CSS

The `lightningcss` tool runs the [Lightning CSS](https://lightningcss.dev) command line, which Douze downloads on
first use. A batch command step bundles the `@import`s of a stylesheet and minifies it:

```css title="_content/css/site.css"
@import "./base.css";
@import "./layout.css";

.lead { font-size: 1.25rem; }
```

```yaml title="douze.yml"
steps:
  pages:
    inputs: "_content/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  css:
    needs: permalink
    match: "css/**/*.css"
    tool: lightningcss
    run: "{{ tool }} --minify --bundle {{ inputs }}/css/site.css -o {{ outputs }}/css/site.css"
  render:
    needs: css
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  layout:
    needs: markdown
    uses: layout
  write:
    needs: layout
    uses: write
```

- `match` makes the step consume every stylesheet: they are written under `{{ inputs }}` at their paths, so the
  `@import`s resolve.
- The step emits what Lightning CSS writes under `{{ outputs }}`: one `css/site.css`. The imported files are not
  published.
- The step is in the chain (`render` needs `css`): files that it does not consume pass through it.
- It runs again only when a stylesheet changes. Other options of the command line, such as `--targets`, go in `run`.

`douze tools install` downloads the tool ahead of time, for CI or offline builds. See
[Command steps](command-steps.md).

## Scripts

Scripts are copied as they are. To bundle or minify them, run a tool with a command step, such as esbuild as a batch
step:

```yaml title="douze.yml"
js:
  needs: permalink
  match: "js/**/*.js"
  run: "npx esbuild {{ inputs }}/js/main.js --bundle --minify --log-level=warning --outfile={{ outputs }}/js/main.js"
render:
  needs: js
  uses: liquid
```

esbuild prints its summary on stderr, which fails a command step by default: `--log-level=warning` keeps it quiet,
and `stderr: warn` would show it as a warning instead.

The program of a `run` line must be on the `PATH`: here `npx`, from Node. Douze checks it before the build, and
`douze doctor` reports it when it is missing.

## Fonts

Fonts are copied like any other file. Declare them in a stylesheet with root-relative URLs:

```css title="_content/css/fonts.css"
@font-face {
  font-family: "Inter";
  src: url("/fonts/Inter.woff2") format("woff2");
  font-display: swap;
}
```

The `social-image` step loads font files of the source folder itself (`fontfile`), since containers usually have no
system fonts. See [Social images](social-images.md).

## Live reload

`douze serve` adds a live reload script to every HTML page it serves. After a rebuild:

- when only stylesheets changed, the page swaps them in place, without reloading: the scroll position and the state
  of the page stay;
- when a script changed (`.js`, `.mjs`), the page reloads;
- when the page itself changed, it reloads.

Stylesheets are swapped when the page links them with `<link rel="stylesheet">` from the same origin. A stylesheet
that a bundling step writes is swapped the same way, since its output file changed. See
[Develop and build](develop-and-build.md#live-reload).

## See also

- [Command steps](command-steps.md)
- [Images](images.md)
- [Syntax highlighting](syntax-highlighting.md), whose `stylesheet` option writes the CSS of code blocks
- [Develop and build](develop-and-build.md)
- [Command steps and workers](../reference/commands.md)
