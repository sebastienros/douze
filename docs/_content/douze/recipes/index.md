---
title: Recipes
description: Short, task-oriented guides for common Douze tasks, each with a complete example.
---

Each recipe solves one task with a complete example that builds with the current Douze. They assume a site with the
default folders (`_content`, `_includes`, `_data`) and, when they change the workflow, start from the default one that
`douze --print-workflow` prints.

## Outputs

| Recipe | What it does |
|---|---|
| [Add an RSS feed](rss.md) | A `feed.xml.liquid` template over a collection, with the full content of the posts as an option |
| [Add a sitemap](sitemap.md) | A `sitemap.xml.liquid` template over `collections.all`, and a `robots.txt` that points to it |
| [Customize output paths](output-paths.md) | `permalink` in front matter and folder data, folders, extensions and dates in paths |

## Content

| Recipe | What it does |
|---|---|
| [Add reading time](reading-time.md) | A word count and a reading time computed in a layout with Liquid filters |
| [Add last modified time](modified-time.md) | `page.date`, and the date of the last Git commit of each page in CI |
| [Add icons to external links](external-links.md) | A class on external links from the layout, and an icon in CSS |

## Workflow and tools

| Recipe | What it does |
|---|---|
| [Add search with Pagefind](pagefind.md) | A command step with `tool: pagefind`, which Douze downloads |
| [Minify HTML with a command step](minify-html.md) | A `filter` command step that runs `html-minifier-terser` on every page |
| [Write a Node worker step](node-worker.md) | A long-running Node process that transforms pages over JSON lines, with `douze worker-helper` |

## Deployment

| Recipe | What it does |
|---|---|
| [Deploy to GitHub Pages](github-pages.md) | A GitHub Actions workflow that installs Douze, builds with the cache and deploys with `actions/deploy-pages` |
| [Build your site with Docker](docker.md) | A multi-stage Dockerfile that builds the site with Douze and serves it with nginx |

## See also

- [Workflow architecture](../guide/workflow-architecture.md)
- [Command steps](../guide/command-steps.md)
- [douze.yml reference](../reference/configuration.md)
- [Built-in steps](../reference/steps/index.md)
