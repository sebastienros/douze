---
title: "Unit 3: Includes and shortcodes"
description: Share a header and a footer between pages with includes, and write block and inline shortcodes.
---

In this unit you will:

- create an `_includes` folder for the markup that pages share,
- add a header with the site's navigation and a footer, with the `include` tag,
- write two shortcodes, your own Liquid tags, and use them in a post.

## Step 1: create a header include

An include is a file of the `_includes` folder that a template inserts with the `include` tag. Douze does not publish
the files of `_includes`: they are only used by pages.

Create the folder `_includes` next to `_content`, and in it `header.html`:

```html title="_includes/header.html"
<header>
  <a class="site-title" href="/">My blog</a>
  <nav>
    <a href="/">Home</a>
    <a href="/about/">About</a>
    <a href="/projects/">Projects</a>
  </nav>
</header>
```

## Step 2: create a footer include

Create `_includes/footer.html`:

```html title="_includes/footer.html"
<footer>
  <p>Written by Ada. This page changed on {{ page.date | date: "%B %-d, %Y" }}.</p>
</footer>
```

An include sees the variables of the page that includes it. {% raw %}`{{ page.date }}`{% endraw %} is a variable that
Douze sets on every page: the date the page's file was last changed. So the footer of each page shows its own date.

## Step 3: include them in the pages

Add the header at the top of the home, about and projects pages, and the footer at the bottom:

{% tabs %}

{% tab "index.md" %}

```md title="_content/index.md" ins={4,17}
---
title: My blog
---
{% include "header.html" %}
# Welcome to my blog

This is where I write about learning Douze.

- [About me](/about/)
- [My projects](/projects/)

## Posts

- [My first post](/posts/first-post/)
- [Pages and permalinks](/posts/second-post/)
- [What I learned about Liquid](/posts/third-post/)
{% include "footer.html" %}
```

{% endtab %}

{% tab "about.md" %}

````md title="_content/about.md" ins={11,29}
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
{% include "header.html" %}
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
{% include "footer.html" %}
````

{% endtab %}

{% tab "projects.html" %}

```html title="_content/projects.html" ins={4,12}
---
title: My projects
---
{% include "header.html" %}
<h1>{{ title }}</h1>
<p>Things I built while learning.</p>
<ul>
  <li><a href="https://github.com/sebastienros/douze">Douze</a> builds this blog.</li>
  <li>A <a href="/about/">page about me</a>, written in Markdown.</li>
</ul>
<p><a href="/">Back to the home page</a></p>
{% include "footer.html" %}
```

{% endtab %}

{% endtabs %}

Open `http://localhost:8080/about/`: the navigation is at the top and the footer at the bottom. Change a link in
`_includes/header.html` and save: Douze renders the three pages again, because they use the include.

The header is HTML in a Markdown page, which Markdown keeps as it is because it starts with a block tag (`<header>`)
and has no blank line. Keep blank lines out of HTML that you include in Markdown, or Markdown reads what follows a
blank line as Markdown.

:::note[include and render]
{% raw %}
`{% include %}` shares the page's variables with the include. Liquid's `{% render %}` tag runs the include with only
the variables you pass it: `{% render "card.html", title: "Hello" %}`. Use it for small components that should not
depend on the page.
{% endraw %}
:::

The posts do not have the header yet. Rather than adding it to every post, you will put it in a layout in unit 4.

## Step 4: write a block shortcode

An include inserts a file. A **shortcode** is a file that becomes a Liquid tag of its own, with arguments, and with a
body for a paired tag. Shortcodes live in `_includes/shortcodes/`, and the file name is the name of the tag.

Create `_includes/shortcodes/callout.liquid`:

```liquid title="_includes/shortcodes/callout.liquid"
---
block: true
args: [kind]
---
<div class="callout callout-{{ kind | default: "note" }}">{{ body }}</div>
```

The front matter says what kind of tag it is:

{% raw %}

- `block: true` makes it a paired tag, `{% callout %}...{% endcallout %}`, whose content is `body`.
- `args: [kind]` names the first argument, so that `{% callout "tip" %}` sets `kind` to `"tip"`.
{% endraw %}

In a Markdown page, the body is Markdown: Douze renders it to HTML before it gives it to the shortcode.

## Step 5: write an inline shortcode

Create `_includes/shortcodes/badge.liquid`:

```liquid title="_includes/shortcodes/badge.liquid"
---
args: [text]
---
<span class="badge">{{ text }}</span>
```

Without `block: true`, a shortcode is a single tag that Douze writes in the text where you use it:
{% raw %}`{% badge "New" %}`{% endraw %}.

## Step 6: use the shortcodes in a post

Replace `_content/posts/second-post.md` with:

````md title="_content/posts/second-post.md" ins={10,17-19}
---
title: Pages and permalinks
description: How Douze decides where each page goes.
date: 2026-09-15
author: Ada
tags: [post, douze]
---
# {{ title }}

Published on {{ date | date: "%B %-d, %Y" }} by {{ author }}. {% badge "Updated" %}

Every file of the content folder ends up in the output folder. A page named `about.md` is written to
`about/index.html`, so its address is `/about/`, without `.html`.

A `permalink` in the front matter picks another path.

{% callout "tip" %}
A page with `permalink: false` is not written at all. That is handy for **drafts**.
{% endcallout %}
````

Open `http://localhost:8080/posts/second-post/`. The page has the badge after the date line, and the callout:

```html
<div class="callout callout-tip"><p>A page with <code>permalink: false</code> is not written at all. That is handy for <strong>drafts</strong>.</p>
</div>
```

The Markdown of the callout's body became HTML. The callout has no style yet: you will add a stylesheet in unit 4.

:::tip[Check your understanding]

1. What is the difference between an include and a shortcode?
2. Which folder holds the shortcodes, and how is a shortcode's tag named?
3. Why does the footer of each page show a different date?

:::

<details>
<summary>Answers</summary>

1. An include is a file inserted with the `include` tag. A shortcode is a tag of its own, named after its file, that
   takes arguments and, when it is paired, a body.
2. `_includes/shortcodes/`. `callout.liquid` is the `callout` tag.
3. The footer uses `page.date`, the date of the including page's file, and each page has its own.

</details>

See [Includes and shortcodes](../guide/shortcodes.md) for named arguments, slots and every option.

## Next

In [unit 4](4-layouts.md), you will move the header and footer into a layout, give every page a full HTML document,
and add a stylesheet.
