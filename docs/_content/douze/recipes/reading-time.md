---
title: Add reading time
description: Count the words of a page and show an estimated reading time, with Liquid filters in a layout.
---

A layout receives the page's HTML as `content`. Removing the tags and splitting the text on spaces gives a word count,
and dividing it by a reading speed gives minutes. No step or plugin is needed.

## Compute it in the layout

```liquid
<!doctype html>
<html>
<body>
  <article>
    {%- assign words = content | strip_html | split: " " | size %}
    {%- assign minutes = words | divided_by: 200.0 | ceil | at_least: 1 %}
    <p class="reading-time">{{ minutes }} min read</p>
    {{ content }}
  </article>
</body>
</html>
```

For a page of 461 words, it writes:

```html
<p class="reading-time">3 min read</p>
```

- `strip_html` removes tags, comments, scripts and styles.
- `split: " "` splits on any whitespace, newlines included, and skips empty strings, so `size` is the number of
  words.
- `divided_by: 200.0` divides as a decimal number (with `200` it would be an integer division), `ceil` rounds up and
  `at_least: 1` avoids "0 min read" on short pages. 200 words per minute is a common estimate for technical text;
  change it to suit your content.
- The text of code blocks counts as words.

:::caution[Filters that do not exist]
Jekyll's `number_of_words` is not a Liquid filter of Douze, and an unknown filter does not fail the build: it returns
its input unchanged. Check the output when you port a template.
:::

## Reuse it

Put the two lines in an include, `_includes/reading-time.liquid`:

```liquid
{%- assign words = content | strip_html | split: " " | size -%}
{%- assign minutes = words | divided_by: 200.0 | ceil | at_least: 1 -%}
<p class="reading-time">{{ minutes }} min read</p>
```

and use it in any layout:

```liquid
{% include 'reading-time.liquid' %}
```

## Show it in a list of posts

The items of a collection have the data of each page, not its HTML: the default `collections` step runs before
rendering. To compute reading times in an index page, add a `collect` step after `markdown` with `content: true`, and
read `post.content` in the list. [Add an RSS feed](rss.md#include-the-full-content) shows that workflow.

## See also

- [Layouts](../guide/layouts.md)
- [Includes and shortcodes](../guide/shortcodes.md)
- [Liquid filters and tags](../reference/liquid.md)
- [Collections](../guide/collections.md)
