---
title: Syntax highlighting
description: Highlight code fences at build time with the highlight step, its TextMate grammars and themes, fence options and templates.
liquid: false
---

The `highlight` step highlights the code fences of HTML pages at build time, with the TextMate grammars and themes that
VS Code and Shiki use. It runs inside Douze, without Node. The page gets highlighted HTML and needs no script to show
it. This page shows how to add the step, choose themes, use the fence options and change the markup.

## Add the step

The default workflow has no `highlight` step: fences come out of the `markdown` step as
`<pre data-lang="..." data-meta="..."><code class="language-...">`, ready for a client-side highlighter. To highlight
at build time, add the step after `markdown` and before `layout`:

```yaml title="douze.yml" {14-18}
steps:
  pages:
    inputs: "**/*.md"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
  render:
    needs: permalink
    uses: liquid
  markdown:
    needs: render
    uses: markdown
  highlight:
    needs: markdown
    uses: highlight
    with:
      stylesheet: assets/code.css
  layout:
    needs: highlight
    uses: layout
  write:
    needs: layout
    uses: write
```

`douze --print-workflow` prints the default workflow, so you can start from it. Link the stylesheet from your layout:

```html title="_includes/base.html"
<link rel="stylesheet" href="/assets/code.css">
```

The step reads the HTML that `markdown` wrote, so it only finds fences in `.html` files after that step (its default
`match` is `**/*.html`). Other `<pre>` elements, such as `<pre class="mermaid">`, are left alone.

## Grammars and languages

Every grammar of [tm-grammars](https://github.com/shikijs/textmate-grammars-themes) is bundled, in the version that
Shiki 4.5 uses, so a fence's language is a name or an alias that Shiki knows: `csharp`, `cs`, `ts`, `yaml`, `sh`...
`text`, `txt`, `plain` and `plaintext` are plain text.

A fence whose language has no grammar is rendered as plain text, and the build prints one warning listing those
languages:

```
warning: highlight: no grammar for 'foo' (1 fence): rendered as plain text; add one with the option 'grammars' or map the language with 'aliases'
```

`aliases` maps a fence language to a grammar, and `grammars` adds grammar files (TextMate JSON, with a `scopeName`) of
the source folder. A site grammar wins over a bundled one of the same name:

```yaml title="douze.yml"
highlight:
  needs: markdown
  uses: highlight
  with:
    grammars:
      mylang: syntax/mylang.json
    aliases:
      conf: ini
      foo: mylang
```

## Themes, light and dark

`themes` lists bundled theme names (every theme of tm-themes) or paths of VS Code theme files. The default is
`[github-dark, github-light]`. A page is rendered once for all the themes: each token is a span with a CSS variable per
theme, `--0` for the first, `--1` for the second, plus `--0fs`, `--0fw` and `--0td` for italic, bold and decorations:

```html
<span style="--0:#F97583;--1:#BF3441">const</span>
```

A stylesheet picks the variable to show. The one that `stylesheet` writes uses the first theme by default, and the first
theme of the other type (light or dark) when the reader's system prefers it (`prefers-color-scheme`), or when the root
element has `data-theme="light"` or `data-theme="dark"`. To default to light, list the light theme first:

```yaml
      themes: [github-light, github-dark]
```

A site with a theme toggle sets `data-theme` on `<html>`, and the code follows it.

`min-contrast` (default 5.5, a WCAG contrast ratio) adjusts token colors that are hard to read on the background or on
marked lines, as Expressive Code does. `min-contrast: 0` keeps the theme's colors. `markers` sets the background of
`mark`, `ins` and `del` lines, one color per theme:

```yaml
markers:
  mark: ["#174a9080", "#9ab6ff80"]
  ins: ["#1e571580", "#8ec77d80"]
  del: ["#862d2780", "#ff9c8e80"]
```

## Fence options

Options go in the fence's meta, after the language. They use the syntax that Expressive Code and other highlighters
share.

### Titles and frames

````md
```js title="hello.js"
console.log("hello");
```
````

Without `title`, a file name in a comment on one of the first lines becomes the title, and the comment is removed
(`// Program.cs`, `# config.yml`). `title-from-comment: false` on the step turns that off.

`frame=code`, `frame=terminal` or `frame=none` picks the frame. Shell languages (`sh`, `bash`, `powershell`, `console`
and the like) get a terminal frame by default. The text a terminal frame's copy button copies leaves comment lines out,
unless the step sets `copy-terminal-comments: true`.

### Line and text markers

````md
```js {1} ins={2} del={3} "world" /h\w+o/
const a = 1;
console.log("hello world");
console.log("bye");
```
````

- `{1,3-5}` marks lines, and `ins={2}` and `del={3}` mark them as inserted or deleted. `{"Label":2-3}` gives the lines
  a label.
- `"text"` and `/regex/` mark text in the lines, and can be keyed the same way (`ins="text"`).

### Diff

A `diff` fence marks `+` and `-` lines as inserted and deleted. With `lang=ts`, the code is highlighted as TypeScript
and the `+` and `-` are removed:

````md
```diff lang=ts
-const a = 1;
+const a = 2;
```
````

### Collapse, line numbers and wrap

````md
```cs showLineNumbers startLineNumber=10 collapse={2-3}
var a = 1;
var b = 2;
var c = 3;
var d = 4;
```
````

- `collapse={2-3}` folds lines into a `<details>` section. `collapseStyle` is `github` (the default),
  `collapsible-start`, `collapsible-end` or `collapsible-auto`.
- `showLineNumbers` numbers the lines, from `startLineNumber`. `line-numbers: true` on the step numbers every fence,
  and `showLineNumbers=false` turns it off for one.
- `wrap` wraps long lines instead of scrolling.

Every option is also given to the block template as `code.options`, so a template can read options of its own.

## The markup

The default template writes:

```html
<figure class="code code-code has-title" data-language="js">
  <figcaption class="code-title">hello.js</figcaption>
  <pre><code><span class="line mark">...</span>
</code></pre>
  <button class="code-copy" type="button" title="Copy" data-code="...">Copy</button>
</figure>
```

`stylesheet` writes a stylesheet for that markup, the themes and the twoslash popups (see
[Code hovers](code-hovers.md)). The copy button is only markup: Douze adds no script, so a site that wants copying
adds a small script that reads `data-code`.

`tab-width` replaces tabs with spaces. The code is trimmed as Markdown highlighters do: no trailing spaces, no blank
lines around it.

## Custom templates

`templates` replaces the markup with Liquid templates of the includes folder: `block` for a code block, `popup` for a
twoslash annotation.

```yaml title="douze.yml"
highlight:
  needs: markdown
  uses: highlight
  with:
    templates:
      block: code/block.liquid
```

```liquid title="_includes/code/block.liquid"
<div class="snippet" data-lang="{{ code.language }}">
  {%- if code.title %}<p class="snippet-title">{{ code.title }}</p>{% endif -%}
  <pre><code>{% for line in code.lines %}<span class="line{% if line.marker %} {{ line.marker }}{% endif %}">{{ line.html }}</span>
{% endfor %}</code></pre>
</div>
```

The block template gets `code`: `language`, `grammar`, `meta`, `options`, `title`, `frame`, `terminal`, `copy`,
`wrap`, `line_numbers`, `popup` (true for a type shown in a popup), `lines` (each with `html`, `text`, `number`,
`marker`, `label`, `indent`) and `rows` (the lines grouped in collapsed `section`s). Douze's own template is
`Douze/Highlighting/Templates/block.liquid` in the repository, a good starting point. Templates read the page's data
like any template, so they can use its `lang` or the `t` filter.

## Check the samples

Highlighting shows code; it does not tell whether the code is valid. The [check-code](../reference/steps/check-code.md)
step, before `highlight`, parses the YAML, JSON, XML and Liquid fences and fails the build on a sample that does not
parse. A ` ```yaml title="douze.yml" ` sample is also checked against the workflow schema, so an option renamed in a
new version of Douze is caught in the documentation that still shows it:

```yaml title="douze.yml"
check-code:
  needs: markdown
  uses: check-code
highlight:
  needs: check-code
  uses: highlight
```

A sample that is wrong on purpose takes the `nocheck` word: ` ```yaml nocheck `.

## Caching

The tokens of each fence are kept in the disk cache by language and code, together with the step's settings. A page
that changes highlights only its new fences, and a theme change highlights everything again. See
[Cache and incremental builds](cache.md).

## See also

- [highlight step reference](../reference/steps/highlight.md)
- [Code hovers](code-hovers.md)
- [check-code step reference](../reference/steps/check-code.md)
- [markdown step reference](../reference/steps/markdown.md)
- [Markdown](markdown.md)
