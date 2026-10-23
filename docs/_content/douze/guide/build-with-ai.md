---
title: Build with AI
description: Publish llms.txt and Markdown versions of your pages for language models, and work on a Douze site with an AI agent.
liquid: false
---

Language models read Markdown better than HTML. A Douze site can publish an `llms.txt` file that lists its pages,
and a Markdown twin of each page next to its HTML, with a template and one workflow step. This page shows both, then
gives tips for AI coding agents that work on a Douze site.

## `llms.txt`

[`llms.txt`](https://llmstxt.org) is a Markdown file at the root of a site that describes it and links to its pages.
In Douze it is a template in the content folder, `llms.txt.liquid`, rendered like a page and written as `llms.txt`:

```liquid title="_content/llms.txt.liquid"
# My site

> Documentation of My product.

## Pages
{% for p in collections.all %}
- [{{ p.data.title | raw }}]({{ p.data.url | replace: 'index.html', 'index.html.md' | absolute_url }}): {{ p.data.description | raw }}
{%- endfor %}
```

- `collections.all` holds every page, sorted by date then by path. Filter it with `where_exp`, or loop over a sidebar
  to follow the order of your navigation.
- `absolute_url` prepends `site.url`, from `douze.yml`.
- The `liquid` step encodes the output of `{{ }}` for HTML: `raw` keeps characters such as `&` as they are in a text
  file.
- Templates such as this one are not in the collections, and layouts only apply to HTML files.

The links point at the Markdown twins of the pages, below. Link to the HTML pages instead
(`{{ p.data.url | replace: 'index.html', '' | absolute_url }}`) if you do not publish twins.

## Markdown twins of pages

The `fork` step passes each file through and adds a copy of it. Placed after `render` (Liquid) and before
`markdown`, it copies every Markdown page with its Liquid rendered, includes and shortcodes included, before it
becomes HTML. The copy is written next to the page, at the page's URL followed by `.md`, as `llms.txt` suggests:
`/guide/intro/` has `/guide/intro/index.html.md`.

```yaml title="douze.yml"
site:
  url: https://example.com
steps:
  pages:
    inputs: "_content/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  collections:
    needs: permalink
    uses: collect
  render:
    needs: [permalink, collections]
    uses: liquid
  twins:
    needs: render
    uses: fork
    match: "**/*.md"
    with:
      permalink: "{{ url | replace: 'index.html', 'index.html.md' }}"
      data:
        markdown: false
  markdown:
    needs: twins
    uses: markdown
  layout:
    needs: markdown
    uses: layout
  write:
    needs: layout
    uses: write
```

- `permalink` is a template over the page's data; `url` is the page's HTML path (`/guide/intro/index.html`).
- `data` is merged over the copy's data: `markdown: false` keeps the `markdown` step from rendering the copy to HTML,
  and since the copy is not an `.html` file, `layout` leaves it alone.
- Code fences are not Liquid, so a page that shows `{{ }}` in a fence keeps it in its twin.
- The lines that mark raw HTML regions (the output of block shortcodes) are removed from the copy
  (`strip-regions`, on by default). The HTML of the shortcode stays.
- `content` sets another text for the copy, a template with the page's text as `content`, for instance to add the
  title as a heading: `content: "# {{ title }}\n\n{{ content }}"`.
- `skip` leaves some pages without a copy: `skip: "draft"`.

With `_site/guide/intro/index.html` and `_site/guide/intro/index.html.md` written, a "Copy as Markdown" or "View as
Markdown" link on each page is a link to `{{ url }}.md`. See the [fork step](../reference/steps/fork.md).

## Tips for AI agents

An agent that edits a Douze site works best when it can see the workflow and check its changes. Put these in your
agent instructions (`AGENTS.md`, `CLAUDE.md`):

- **Read the workflow, do not guess it.** `douze --print-workflow` prints every step a build runs, the default ones
  included, with their `inputs`, `needs` and options. It exits without building.
- **Build in plain mode.** `douze build --plain` prints one line per step and the errors, without the live view
  (agents usually run with `CI` set or a redirected output, which does the same).
- **Check the exit code.** `0` is success, `1` a build error, `2` an invalid command line or `douze.yml`.
- **Read errors as positions.** Errors have one format, with the step, the file and the position:

  ```txt
  douze.yml(6,3): error: step 'markdown' needs unknown step 'markdwn'; did you mean 'markdown'?
  error: render: /blog/first/index.md(3,1): '{% endif %}' was expected
  ```

  The file of a build error is the file's path in the workflow at that step (after `permalink`, its output path), and
  the line is in the page's body, after its front matter.
- **Check requirements before building.** `douze doctor` lists what the steps need (libvips, downloaded tools, the
  programs of command steps) and exits with `1` when one is missing.
- **Make mistakes fail.** `strict: true` on the `liquid` step makes an undefined variable an error, and the
  `frontmatter` step's `require` lists keys every page must have:

  ```yaml
  pages:
    inputs: "_content/**"
    uses: frontmatter
    with:
      require: [title, description]
  render:
    needs: paginate
    uses: liquid
    with:
      strict: true
  ```

- **Fail on purpose.** `{% error "message" %}` in a template fails the page with the position of the tag, and
  `{% warn "message" %}` logs a warning: use them to assert what a template expects.

## See also

- [fork step](../reference/steps/fork.md)
- [Feeds, sitemaps and other outputs](outputs.md)
- [Command line reference](../reference/cli.md)
- [Troubleshooting](troubleshooting.md)
- [Configuration overview](configuration.md)
