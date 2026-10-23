---
title: "Unit 4: Layouts"
description: Wrap every page in a base layout, chain a post layout to it, set layouts for a whole folder, and add a stylesheet.
---

In this unit you will:

- write a base layout that turns every page into a complete HTML document,
- write a post layout, chained to the base layout, that shows a post's title, date and author,
- set the layout of every page, and of every post, without touching their front matter,
- add a stylesheet.

## Step 1: write the base layout

A layout is a template of the `_includes` folder that wraps a page. Douze renders the page first, then renders the
layout with the page's variables, and with the HTML of the page in {% raw %}`{{ content }}`{% endraw %}.

Create `_includes/base.html`:

```html title="_includes/base.html"
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>{{ title }}</title>
    <link rel="stylesheet" href="/styles/global.css">
  </head>
  <body>
    {% include "header.html" %}
    <main>
      {{ content }}
    </main>
    {% include "footer.html" %}
  </body>
</html>
```

The header and footer now come from the layout, once for every page.

## Step 2: use it for every page

A page picks its layout with `layout` in its front matter, such as `layout: base.html`. But every page of the blog
should use it, so set it once for the whole site instead. Create a `_data` folder next to `_content`, and in it
`layout.yml`:

```yaml title="_data/layout.yml"
base.html
```

Each file of `_data` is a site-wide variable named after the file: `_data/layout.yml` sets `layout` for every page,
unless the page, or its folder, sets its own. You will add more data files in unit 5.

## Step 3: remove the includes from the pages

The pages included the header and footer themselves; now the layout does. Remove the two `include` lines from the
three pages:

{% tabs %}

{% tab "index.md" %}

```md title="_content/index.md"
---
title: My blog
---
# Welcome to my blog

This is where I write about learning Douze.

- [About me](/about/)
- [My projects](/projects/)

## Posts

- [My first post](/posts/first-post/)
- [Pages and permalinks](/posts/second-post/)
- [What I learned about Liquid](/posts/third-post/)
```

{% endtab %}

{% tab "about.md" %}

```md title="_content/about.md"
---
title: About me
description: Who writes this blog, and why.
skills:
  - HTML
  - CSS
  - Markdown
  - Liquid
learning: true
---
# {{ title }}

{{ description }}

I am a web developer who likes small tools. This blog is my notebook while I learn Douze.

## My skills

{% for skill in skills %}
- {{ skill }}
{%- endfor %}

{% if learning %}
I am still learning Liquid, so expect a few mistakes.
{% endif %}

[Back to the home page](/)
```

{% endtab %}

{% tab "projects.html" %}

```html title="_content/projects.html"
---
title: My projects
---
<h1>{{ title }}</h1>
<p>Things I built while learning.</p>
<ul>
  <li><a href="https://github.com/sebastienros/douze">Douze</a> builds this blog.</li>
  <li>A <a href="/about/">page about me</a>, written in Markdown.</li>
</ul>
<p><a href="/">Back to the home page</a></p>
```

{% endtab %}

{% endtabs %}

Open `http://localhost:8080/` and view the page source: it is a complete document now, with a `<head>`, the header, the
page in `<main>`, and the footer. The posts and the 404 page have the header and footer too, without any change.

## Step 4: write a post layout

Posts need more than the base layout: their title, date and author, which every post repeats today. A layout can have
a layout of its own. Create `_includes/post.html`:

```html title="_includes/post.html"
---
layout: base.html
---
<article class="post">
  <h1>{{ title }}</h1>
  <p class="meta">Published on <time datetime="{{ date | date: "%Y-%m-%d" }}">{{ date | date: "%B %-d, %Y" }}</time> by {{ author }}</p>
  {{ content }}
</article>
```

{% raw %}
Douze renders the post into the post layout's `{{ content }}`, then the result into the base layout's
`{{ content }}`. This is layout chaining.
{% endraw %}

## Step 5: set the layout of every post with folder data

The posts should use `post.html`. A data file in a folder of `_content` applies to every page of that folder and its
subfolders. Create `_content/posts/posts.yml`:

```yaml title="_content/posts/posts.yml"
layout: post.html
author: Ada
```

Every post now has `layout: post.html`, which wins over `_data/layout.yml` because it is closer to the page, and
`author: Ada`. A value in a post's front matter still wins over its folder's.

Data files are not published: there is no `posts.yml` in `_site`.

## Step 6: simplify the posts

The layout shows the title, the date and the author, and the folder data sets the author. Remove the title, the date
line and the `author` from the three posts (the badge of the second post moves to its first paragraph):

{% tabs %}

{% tab "first-post.md" %}

```md title="_content/posts/first-post.md"
---
title: My first post
description: Why I started this blog.
date: 2026-09-01
tags: [post, douze]
---
This is the first post of my new blog. I am building it with Douze, a static site generator: I write Markdown, and
Douze turns it into HTML.

## What I want to learn

- How pages become HTML files.
- How to share a layout between pages.
- How to list my posts automatically.
```

{% endtab %}

{% tab "second-post.md" %}

```md title="_content/posts/second-post.md"
---
title: Pages and permalinks
description: How Douze decides where each page goes.
date: 2026-09-15
tags: [post, douze]
---
Every file of the content folder ends up in the output folder. A page named `about.md` is written to
`about/index.html`, so its address is `/about/`, without `.html`. {% badge "Updated" %}

A `permalink` in the front matter picks another path.

{% callout "tip" %}
A page with `permalink: false` is not written at all. That is handy for **drafts**.
{% endcallout %}
```

{% endtab %}

{% tab "third-post.md" %}

````md title="_content/posts/third-post.md"
---
title: What I learned about Liquid
description: Variables, filters and loops, the three things I use the most.
date: 2026-10-01
tags: [post, liquid]
---
Liquid is the template language of Douze. Three of its features cover most of what I need.

## Variables

Every key of the front matter is a variable:

```liquid
<h1>{{ title }}</h1>
```

## Filters

A filter transforms a value, after a `|`:

```liquid
{{ date | date: "%B %-d, %Y" }}
```

## Loops

A `for` tag repeats its content for each item of a list:

```liquid
{% for skill in skills %}
  <li>{{ skill }}</li>
{% endfor %}
```
````

{% endtab %}

{% endtabs %}

Open `http://localhost:8080/posts/first-post/`. The post has the header, its title, the date and author line from the
post layout, and the footer.

## Step 7: add a stylesheet

Any file of `_content` that is not a page or a data file is copied to `_site` as it is. Create
`_content/styles/global.css`:

```css title="_content/styles/global.css"
:root {
  --text: #1f2328;
  --muted: #59636e;
  --accent: #d1461b;
  --surface: #f6f8fa;
  color-scheme: light dark;
}

@media (prefers-color-scheme: dark) {
  :root {
    --text: #e6edf3;
    --muted: #9198a1;
    --accent: #ff7b4f;
    --surface: #161b22;
  }
}

body {
  max-width: 44rem;
  margin: 0 auto;
  padding: 1.5rem 1rem;
  font-family: system-ui, sans-serif;
  line-height: 1.6;
  color: var(--text);
}

a {
  color: var(--accent);
}

header {
  display: flex;
  flex-wrap: wrap;
  gap: 1rem;
  align-items: baseline;
  justify-content: space-between;
  padding-bottom: 1rem;
  border-bottom: 1px solid var(--surface);
}

header nav {
  display: flex;
  gap: 1rem;
}

.site-title {
  font-weight: 700;
  text-decoration: none;
}

.meta,
footer {
  color: var(--muted);
  font-size: 0.9rem;
}

footer {
  margin-top: 3rem;
  padding-top: 1rem;
  border-top: 1px solid var(--surface);
}

.callout {
  padding: 0.25rem 1rem;
  border-left: 4px solid var(--accent);
  background: var(--surface);
}

.badge {
  padding: 0.1rem 0.5rem;
  border-radius: 1rem;
  background: var(--accent);
  color: white;
  font-size: 0.75rem;
}

/* For the heading anchors and the table of contents of unit 6. */
.heading-anchor {
  margin-left: 0.4rem;
  text-decoration: none;
  opacity: 0.4;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}

.toc {
  padding: 0.5rem 1rem;
  background: var(--surface);
  font-size: 0.9rem;
}
```

The base layout already links it, with `<link rel="stylesheet" href="/styles/global.css">`. Every page is styled now,
and the callout and badge of the second post too. Edit a color and save: `douze serve` swaps the stylesheet in the
browser without reloading the page.

:::tip[Check your understanding]

1. A page sets no `layout`. Which layout does it get, and why?
2. Which layout does a post get, and why does it also get the header?
3. How would you give one post another author?

:::

<details>
<summary>Answers</summary>

1. `base.html`, from the site data file `_data/layout.yml`.
2. `post.html`, from the folder data file `_content/posts/posts.yml`, which is closer to the post than the site data.
   `post.html` has `layout: base.html` in its front matter, so the base layout, with the header, wraps it.
3. Set `author` in that post's front matter: the page's own data wins over its folder's.

</details>

See [Layouts](../guide/layouts.md) and [Data files](../guide/data.md) for the details.

## Next

The home page still lists the posts by hand. In [unit 5](5-data-and-collections.md), you will let Douze list them, by
date and by tag, and generate an RSS feed and a sitemap.
