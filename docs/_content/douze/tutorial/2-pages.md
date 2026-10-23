---
title: "Unit 2: Pages"
description: Add Markdown and HTML pages and blog posts, link them together, choose where they are written, and use front matter in Liquid.
---

In this unit you will:

- write pages in Markdown and in HTML,
- give pages data with front matter, and show it with Liquid,
- add three blog posts in a folder of their own,
- link pages together,
- learn where Douze writes each page, and change it with `permalink`.

Keep `douze serve` running from unit 1, and open each new page in the browser as you create it.

## Step 1: add an about page

Create `_content/about.md`:

````md title="_content/about.md"
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
````

Open `http://localhost:8080/about/`. The page shows its title, its description, the list of skills and the sentence
about learning Liquid.

Douze renders every page as a [Liquid](https://shopify.github.io/liquid/) template before it renders its Markdown.
Liquid has two kinds of markup:

{% raw %}

- `{{ ... }}` outputs a value. `{{ title }}` is the `title` of the front matter.
- `{% ... %}` is a tag: logic such as `{% for %}` loops and `{% if %}` conditions.
{% endraw %}

Every key of the front matter is a variable of the page: `title`, `description`, `skills` and `learning` here. They
can be strings, numbers, booleans, lists or objects, anything YAML can write.

Change `learning: true` to `learning: false` and save: the sentence disappears from the page.

:::tip[Whitespace control]
{% raw %}
A dash inside a tag, as in `{%- endfor %}`, removes the whitespace before it. Here it keeps the list items on
consecutive lines, which Markdown needs to make one list.
{% endraw %}
:::

## Step 2: add an HTML page

A page can also be HTML. Create `_content/projects.html`:

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

Open `http://localhost:8080/projects/`. An HTML page has front matter and Liquid like a Markdown page; Douze only skips
the Markdown rendering.

## Step 3: write blog posts

Posts are pages too. Keep them together in a `posts` folder: create `_content/posts/` and these three files.

````md title="_content/posts/first-post.md"
---
title: My first post
description: Why I started this blog.
date: 2026-09-01
author: Ada
tags: [post, douze]
---
# {{ title }}

Published on {{ date | date: "%B %-d, %Y" }} by {{ author }}.

This is the first post of my new blog. I am building it with Douze, a static site generator: I write Markdown, and
Douze turns it into HTML.

## What I want to learn

- How pages become HTML files.
- How to share a layout between pages.
- How to list my posts automatically.
````

````md title="_content/posts/second-post.md"
---
title: Pages and permalinks
description: How Douze decides where each page goes.
date: 2026-09-15
author: Ada
tags: [post, douze]
---
# {{ title }}

Published on {{ date | date: "%B %-d, %Y" }} by {{ author }}.

Every file of the content folder ends up in the output folder. A page named `about.md` is written to
`about/index.html`, so its address is `/about/`, without `.html`.

A `permalink` in the front matter picks another path.
````

````md title="_content/posts/third-post.md"
---
title: What I learned about Liquid
description: Variables, filters and loops, the three things I use the most.
date: 2026-10-01
author: Ada
tags: [post, liquid]
---
# {{ title }}

Published on {{ date | date: "%B %-d, %Y" }} by {{ author }}.

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

Open `http://localhost:8080/posts/first-post/`. Under the title, the post shows "Published on September 1, 2026 by
Ada".

{% raw %}

- `date` is a date in the front matter. The `date` filter formats it: `%B` is the month's name, `%-d` the day without a
  leading zero, `%Y` the year.
- `tags` is a list of tags. You will use them in unit 5 to list the posts.
- The code samples of the third post show Liquid that Douze does not run: the content of Markdown code fences is never
  rendered as Liquid, so `{{ title }}` stays as it is.
{% endraw %}

## Step 4: link the pages together

Links between pages are ordinary links, to the address of each page. Replace `_content/index.md` with:

```md title="_content/index.md" ins={8-15}
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

The home page now links to every page. In unit 5, Douze will build the list of posts for you.

## Step 5: see where pages are written

Look at the `_site` folder:

```txt
_site/
├── index.html
├── about/index.html
├── projects/index.html
└── posts/
    ├── first-post/index.html
    ├── second-post/index.html
    └── third-post/index.html
```

Douze writes each page at its path in `_content`, in a folder named after the page, so that its address has no
extension:

| Source | Output | Address |
|---|---|---|
| `_content/index.md` | `_site/index.html` | `/` |
| `_content/about.md` | `_site/about/index.html` | `/about/` |
| `_content/projects.html` | `_site/projects/index.html` | `/projects/` |
| `_content/posts/first-post.md` | `_site/posts/first-post/index.html` | `/posts/first-post/` |

Files that are not pages, such as images or stylesheets, are copied as they are.

## Step 6: choose a path with permalink

A `permalink` in the front matter overrides the output path. Static hosts, and `douze serve`, show a `404.html` page at
the root of the site when a page does not exist. Create one:

```md title="_content/404.md"
---
title: Page not found
permalink: /404.html
---
# Page not found

There is nothing at this address. Go back to the [home page](/).
```

Without the permalink, this page would be written to `404/index.html`. Open `http://localhost:8080/nothing-here/`: the
server answers with your 404 page.

{% raw %}
A permalink without an extension is a folder: `permalink: /hello/` writes `hello/index.html`. It is a Liquid
template too, so `permalink: /blog/{{ title | slug }}/` builds the path from the title. `permalink: false` does not
write the page at all, which is handy for drafts.
{% endraw %}

See [Routing and permalinks](../guide/routing.md) for every rule.

:::tip[Check your understanding]

1. What is the address of a page saved as `_content/posts/hello.md`?
2. What does `{% raw %}{{ skills.size }}{% endraw %}` output in the about page?
3. How do you write a page to `/feed.xml` instead of `/feed/index.html`?

:::

<details>
<summary>Answers</summary>

1. `/posts/hello/`, written to `_site/posts/hello/index.html`.
2. `4`, the number of items in the `skills` list. `size` works on lists and strings.
3. With `permalink: /feed.xml` in its front matter. Unit 5 shows another way, with a `.liquid` file.

</details>

## Next

The home and about pages repeat the same links, and every post repeats its title and date line. In
[unit 3](3-includes-and-shortcodes.md), you will share markup between pages with includes and shortcodes.
