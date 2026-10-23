---
title: Build a blog with Douze
description: A hands-on tutorial that builds a small blog with Douze, from an empty folder to a site deployed on GitHub Pages.
---

This tutorial builds a small blog, one feature at a time, starting from an empty folder. By the end you will have a
site with:

- a home page, an about page and a projects page, written in Markdown and HTML,
- blog posts that share a layout, with their date, author and tags,
- a header, a footer and reusable shortcodes,
- a blog index, a page per tag, an RSS feed and a sitemap built from your posts,
- heading anchors, a table of contents, highlighted code and full-text search,
- a GitHub Actions workflow that publishes it to GitHub Pages.

Every unit adds to the site of the previous one and shows each file it creates or changes in full, so you can follow
along by copying them.

## What you need

- A terminal and a text editor.
- Some knowledge of HTML and Markdown. You will learn the Liquid template language as you go.
- Nothing else: Douze is a single executable. There is no Node, npm or .NET to install.

## Units

| Unit | You will learn |
|---|---|
| [1. Setup](1-setup.md) | Install Douze, create a site, run the development server, look at the output |
| [2. Pages](2-pages.md) | Markdown and HTML pages, front matter, output paths and permalinks, Liquid variables |
| [3. Includes and shortcodes](3-includes-and-shortcodes.md) | Share markup with includes, write your own shortcodes |
| [4. Layouts](4-layouts.md) | A base layout, a post layout chained to it, folder data, a stylesheet |
| [5. Data and collections](5-data-and-collections.md) | Data files, tags, the blog index, tag pages with pagination, an RSS feed and a sitemap |
| [6. Workflows](6-workflows.md) | `douze.yml`, heading anchors and a table of contents, syntax highlighting, search, a build timeline, deployment |

Each unit takes 10 to 20 minutes. Units 1 to 5 use no configuration file at all: Douze builds the site with its default
workflow. Unit 6 adds a `douze.yml` to change that workflow.

## The finished site

At the end of the tutorial, the folder of the blog looks like this:

```txt
my-blog/
├── .github/workflows/deploy.yml
├── .gitignore
├── douze.yml
├── _content/
│   ├── 404.md
│   ├── about.md
│   ├── blog.md
│   ├── feed.xml.liquid
│   ├── index.md
│   ├── projects.html
│   ├── search.html
│   ├── sitemap.xml.liquid
│   ├── tags.md
│   ├── posts/
│   │   ├── posts.yml
│   │   ├── first-post.md
│   │   ├── second-post.md
│   │   └── third-post.md
│   └── styles/global.css
├── _data/
│   ├── layout.yml
│   ├── navigation.yml
│   ├── site.yml
│   └── topics.yml
└── _includes/
    ├── base.html
    ├── footer.html
    ├── header.html
    ├── post.html
    └── shortcodes/
        ├── badge.liquid
        └── callout.liquid
```

:::tip[Checked on every change]
The files of this tutorial are extracted from these pages and built, unit by unit, in a clean Linux container where
Douze is installed as the [installation page](../guide/install.md) says. If something does not work as described, it is a bug: please
[open an issue](https://github.com/sebastienros/douze/issues).
:::

Ready? [Start with unit 1](1-setup.md).
