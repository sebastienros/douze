---
title: "Unit 6: Workflows"
description: Configure the build with douze.yml, add heading anchors, a table of contents, syntax highlighting and search, look at a build timeline, and deploy to GitHub Pages.
---

In this unit you will:

- print the workflow that builds the blog, and write it to a `douze.yml` file,
- add steps: heading anchors and a table of contents, syntax highlighting, and a search index built by a command,
- check what the steps need with `douze doctor`, and see where the build spends its time with `--timeline`,
- deploy the blog to GitHub Pages with GitHub Actions.

## Step 1: print the workflow

Douze builds a site by running a **workflow**: a graph of steps that the files flow through. Without a configuration
file, it runs its default workflow. Stop `douze serve` and print it:

```sh
douze --print-workflow
```

It prints one line per step; here is the same workflow with each step's fields on their own lines:

```yaml
steps:
  site-data:
    inputs: "_data/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: site
  folder-data:
    inputs: "_content/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: folder
      root: _content
  pages:
    inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"]
    needs: [site-data, folder-data]
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: _content
  collections:
    needs: permalink
    uses: collect
  paginate:
    needs: [permalink, collections]
    uses: paginate
  render:
    needs: paginate
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

Each line is a step, with a name and:

- `inputs`: the source files that enter the step (globs, relative to the site folder),
- `needs`: the steps whose files enter the step, after them,
- `uses`: the built-in step that does the work,
- `with`: the options of that step.

You have used every one of these steps already. `site-data` and `folder-data` read the data files, `pages` reads the
front matter, `permalink` computes the output paths, `collections` builds `collections.post` and the others, `paginate`
expands `tags.md` into a page per topic, `render` runs Liquid, `markdown` turns Markdown into HTML, `layout` applies the
layouts and `write` writes `_site`. The [workflow architecture](../guide/workflow-architecture.md) page explains how
files flow through them.

## Step 2: write douze.yml

When the site folder has a `douze.yml` file, Douze runs its workflow instead. Create `douze.yml` in the `my-blog`
folder, next to `_content`, with the printed steps:

```yaml title="douze.yml"
steps:
  site-data:
    inputs: "_data/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: site
  folder-data:
    inputs: "_content/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: folder
      root: _content
  pages:
    inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"]
    needs: [site-data, folder-data]
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: _content
  collections:
    needs: permalink
    uses: collect
  paginate:
    needs: [permalink, collections]
    uses: paginate
  render:
    needs: paginate
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

Run `douze serve` again. The site is the same: you wrote down the workflow that was already running. From now on, you
can change it. While `douze serve` runs, a change to `douze.yml` takes effect at the next rebuild.

:::tip[Errors in douze.yml]
Douze checks the whole file before it builds, and reports every error with its position, and a suggestion for a
misspelled name: `douze.yml(9,3): error: step 'markdown' needs unknown step 'rnder'; did you mean 'render'?`. Try it.
:::

## Step 3: add heading anchors and a table of contents

The `headings` step reads the headings of the HTML pages. It gives each one an `id`, adds an anchor link to it, and sets
`toc`, the table of contents of the page, for the layouts. It works on HTML, so it goes after `markdown` and before
`layout`. Add it, and make `layout` need it:

```yaml title="douze.yml" ins={34-38,40}
steps:
  site-data:
    inputs: "_data/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: site
  folder-data:
    inputs: "_content/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: folder
      root: _content
  pages:
    inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"]
    needs: [site-data, folder-data]
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: _content
  collections:
    needs: permalink
    uses: collect
  paginate:
    needs: [permalink, collections]
    uses: paginate
  render:
    needs: paginate
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  headings:
    needs: markdown
    uses: headings
    with:
      anchors: true
  layout:
    needs: headings
    uses: layout
  write:
    needs: layout
    uses: write
```

The order of the lines does not matter: `needs` decides the order of the steps.

Show the table of contents in the post layout. Replace `_includes/post.html`:

```html title="_includes/post.html" ins={14-19}
---
layout: base.html
---
<article class="post">
  <h1>{{ title }}</h1>
  <p class="meta">Published on <time datetime="{{ date | date: "%Y-%m-%d" }}">{{ date | date: "%B %-d, %Y" }}</time> by {{ author }}</p>
  <p class="tags">
    {%- for tag in tags %}
    {%- unless tag == "post" %}
    <a href="/tags/{{ tag }}/">#{{ tag }}</a>
    {%- endunless %}
    {%- endfor %}
  </p>
  {%- if toc.size > 0 %}
  <nav class="toc" aria-label="On this page">
    <p>On this page</p>
    {{ toc | toc }}
  </nav>
  {%- endif %}
  {{ content }}
</article>
```

Open `http://localhost:8080/posts/third-post/`. Under the date, "On this page" links to the three sections, Variables,
Filters and Loops, and each heading has a `#` link: `<h2 id="variables">Variables<a class="heading-anchor"
href="#variables">`. The `headings` step adds the anchors to the headings of every page, the about page included.

## Step 4: highlight the code

The third post has code samples, which are plain text for now. The `highlight` step colors the code fences of HTML
pages with the TextMate grammars and themes of VS Code, and writes a stylesheet for them. Add it after `headings`:

```yaml title="douze.yml" ins={39-44,46}
steps:
  site-data:
    inputs: "_data/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: site
  folder-data:
    inputs: "_content/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: folder
      root: _content
  pages:
    inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"]
    needs: [site-data, folder-data]
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: _content
  collections:
    needs: permalink
    uses: collect
  paginate:
    needs: [permalink, collections]
    uses: paginate
  render:
    needs: paginate
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  headings:
    needs: markdown
    uses: headings
    with:
      anchors: true
  highlight:
    needs: headings
    uses: highlight
    with:
      themes: [github-light, github-dark]
      stylesheet: styles/code.css
  layout:
    needs: highlight
    uses: layout
  write:
    needs: layout
    uses: write
```

The first theme is used by default, and the second when the system is in dark mode. `stylesheet` writes the CSS of the
code blocks to `_site/styles/code.css`. Link it in the base layout, with a small script for the Copy button that each
code block gets:

```html title="_includes/base.html" ins={10,18-23}
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>{{ title }} | {{ site.title }}</title>
    <meta name="description" content="{{ description | default: site.description }}">
    <link rel="alternate" type="application/rss+xml" title="{{ site.title }}" href="/feed.xml">
    <link rel="stylesheet" href="/styles/global.css">
    <link rel="stylesheet" href="/styles/code.css">
  </head>
  <body>
    {% include "header.html" %}
    <main>
      {{ content }}
    </main>
    {% include "footer.html" %}
    <script>
      document.addEventListener("click", (event) => {
        const button = event.target.closest(".code-copy");
        if (button) navigator.clipboard.writeText(button.dataset.code);
      });
    </script>
  </body>
</html>
```

Reload the third post: the Liquid samples are colored. No Node or browser script does it: the HTML that Douze writes
has the colors of both themes in each token. See [Syntax highlighting](../guide/syntax-highlighting.md) for titles,
line markers and the other options of a fence.

## Step 5: add search with a command step

A step can also run a program, with `run` instead of `uses`. [Pagefind](https://pagefind.app) builds a search index of
the HTML of a site, and Douze can download it for you with `tool: pagefind`. Add a `search` step that runs it on the
output folder once `write` is done:

```yaml title="douze.yml" ins={52-55}
steps:
  site-data:
    inputs: "_data/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: site
  folder-data:
    inputs: "_content/**/*.{yml,yaml,json,xml}"
    uses: data
    with:
      scope: folder
      root: _content
  pages:
    inputs: ["_content/**", "!_content/**/*.{yml,yaml,json,xml}"]
    needs: [site-data, folder-data]
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: _content
  collections:
    needs: permalink
    uses: collect
  paginate:
    needs: [permalink, collections]
    uses: paginate
  render:
    needs: paginate
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  headings:
    needs: markdown
    uses: headings
    with:
      anchors: true
  highlight:
    needs: headings
    uses: highlight
    with:
      themes: [github-light, github-dark]
      stylesheet: styles/code.css
  layout:
    needs: highlight
    uses: layout
  write:
    needs: layout
    uses: write

  search:
    needs: write
    tool: pagefind
    run: "{{ tool }} --site {{ site }}"
```

{% raw %}
`run` is the command line, a Liquid template: `{{ tool }}` is the path of the Pagefind binary and `{{ site }}` the
output folder. Before it builds, Douze checks what the steps need. Run `douze doctor` to see it:
{% endraw %}

```sh
douze doctor
```

```txt
STEP    REQUIREMENT     STATUS    DETAILS
search  pagefind 1.5.2  download  is not installed: it is downloaded for linux-x64 on first use, or now by 'douze tools install'

Every requirement is met: 1 tool will be downloaded on first use ('douze tools install' downloads it now)
```

The first build downloads Pagefind into the `.douze` folder, then Pagefind writes its index and its search interface
to `_site/pagefind/`. Add a search page, `_content/search.html`:

```html title="_content/search.html"
---
title: Search
---
<h1>Search</h1>
<link rel="stylesheet" href="/pagefind/pagefind-ui.css">
<script src="/pagefind/pagefind-ui.js"></script>
<div id="search"></div>
<script>
  window.addEventListener("DOMContentLoaded", () => {
    new PagefindUI({ element: "#search", showSubResults: true });
  });
</script>
```

And a link to it in the navigation:

```yaml title="_data/navigation.yml" ins={9-10}
- label: Home
  url: /
- label: Blog
  url: /blog/
- label: About
  url: /about/
- label: Projects
  url: /projects/
- label: Search
  url: /search/
```

Open `http://localhost:8080/search/` and search for "liquid": the results link to the posts and pages that mention it.

See [Command steps](../guide/command-steps.md) for the three modes of a command (a process per file, one for all the
files, or a long-running worker), and [Add search with Pagefind](../recipes/pagefind.md) for more options.

## Step 6: look at a build timeline

Douze runs the steps in parallel, as soon as their files are ready. To see it, build with a timeline:

```sh
douze --timeline timeline.html
```

Open `timeline.html` in your browser: one row per step on a time axis, with the time of each one. On a bigger site,
it shows which step holds the others back. Add `timeline.html` to `.gitignore`, or delete it when you are done. See
[Progress and tracing](../guide/tracing.md).

## Step 7: deploy to GitHub Pages

The blog is ready to publish. `_site` is a folder of static files that any host can serve. With GitHub Pages, a
GitHub Actions workflow installs Douze, builds the site and publishes `_site`, at every push.

1. Push the blog to a GitHub repository. Name it `<user>.github.io`, where `<user>` is your GitHub user name, so that
   the site is served at the root of `https://<user>.github.io/`, as its links expect.
2. Set `url` in `_data/site.yml` to `https://<user>.github.io`, so that the feed and the sitemap link to it.
3. In the repository's settings, under **Pages**, set **Source** to **GitHub Actions**.
4. Create `.github/workflows/deploy.yml`:

```yaml title=".github/workflows/deploy.yml"
name: Deploy

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
        run: douze

      - uses: actions/upload-pages-artifact@v5
        with:
          path: _site

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

Commit it and push. The **Actions** tab of the repository shows the run, and the deploy job prints the address of the
site when it is done.

The `build` job installs Douze with the same script as [unit 1](1-setup.md), adds it to the `PATH` of the next steps,
and runs `douze`. In CI, Douze prints one line per step instead of the live view. It builds without the cache of
previous runs, which is fine for a blog: see [Cache and incremental builds](../guide/cache.md) to keep the `.douze`
folder between runs of a big site.

:::caution[A project site]
A repository with another name is served under a path, such as `https://<user>.github.io/my-blog/`, where the
root-relative links of the blog (`/about/`, `/styles/global.css`) do not work. This documentation is such a site: it
is built with Douze (see `docs/douze.yml` in the Douze repository) and keeps its pages under `_content/douze/`, so that
their paths start with `/douze/`. See [Deploy to GitHub Pages](../recipes/github-pages.md) for that setup and for
custom domains.
:::

:::tip[Check your understanding]

1. Where does a new step go in the workflow, and what decides when it runs?
2. Why does the `headings` step need `markdown`, and `layout` need `highlight`?
3. What does `douze doctor` check?

:::

<details>
<summary>Answers</summary>

1. Anywhere in `steps`: the order of the lines does not matter. A step runs on the files of the steps it `needs`, as
   soon as they come out of them.
2. Both work on the HTML of the pages: they must run after `markdown` has turned the Markdown into HTML, and before
   `layout` wraps it in the layouts. Each step needs the one before it, which builds the chain.
3. What the steps of the workflow need outside Douze: libvips for images, the executables of command steps, and the
   tools that Douze downloads, such as Pagefind.

</details>

## What's next

You built a blog with pages, layouts, includes, shortcodes, data, collections, pagination, a feed, a sitemap, search,
and a custom workflow, and deployed it. From here:

- [Images](../guide/images.md): responsive images with the `image` tag and the `images` steps.
- [Navigation and sidebars](../guide/navigation.md): sidebars, breadcrumbs and previous and next links from data.
- [Internationalization](../guide/internationalization.md): a site in several languages.
- [douze.yml reference](../reference/configuration.md) and [the built-in steps](../reference/steps/index.md): every
  option.
