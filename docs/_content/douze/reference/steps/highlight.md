---
title: highlight
description: "The highlight step: highlights code fences with TextMate grammars and themes, without Node."
---

`uses: highlight` highlights the code fences of HTML pages with TextMate grammars and themes (those of VS Code and
Shiki), without Node, and renders each fence with a Liquid template: its frame, title, lines, markers, collapsed
sections, line numbers, copy button, and the popups of the fences that a code hover step annotated.

| | |
|---|---|
| Shape | map (no `group-by`) |
| Default `match` | `**/*.html` |
| Default workflow | no |

## What it does

The step finds the fences as the [`markdown`](markdown.md) step writes them:
`<pre data-lang="csharp" data-meta="{3-4}"><code>...</code></pre>`, a `<pre>` with only `data-lang`, `data-meta` and
`data-twoslash` attributes whose only child is a `<code>` holding text. Other `<pre>` elements (`class="mermaid"`,
code that is already highlighted) are left alone. It runs after `markdown`, and after the hover steps when the site
has some.

Each fence is replaced by the output of the block template. The code is tokenized in every theme at once: each token
is a `<span style="--0:#color;--1:#color">`, one CSS variable per theme by its index (with `--0fs`, `--0fw` and
`--0td` for italic, bold and text decorations), so that a stylesheet switches themes without another render.

- Grammars: every grammar of [tm-grammars](https://github.com/shikijs/textmate-grammars-themes) is bundled, and
  `grammars` adds files of the source folder (they win over bundled grammars of the same name). `aliases` maps fence
  languages to grammar names. `text`, `txt`, `plain` and `plaintext` are plain text. A fence whose language no
  grammar knows is plain text too, with one warning per build that lists the languages and their fence counts.
- Code: lines are trimmed at their end and blank lines around the code are removed; `tab-width` replaces tabs first.
- Twoslash: a fence with a `data-twoslash` attribute (from [hover-typescript](hover-typescript.md),
  [hover-csharp](hover-csharp.md) or [hover-go](hover-go.md)) gets its hovers, errors,
  queries, highlights, completions and tags, each rendered by the popup template. A hover's type is highlighted as a
  block of its own, in the fence's language, and its documentation is rendered as Markdown.
- `stylesheet` adds a CSS file to the outputs with the theme rules for the default markup.

The step changes the content of the pages only; it sets no metadata.

### Fence options

The meta of a fence (the rest of its info string) takes the options that Markdown highlighters for the web share.
Keys compare case insensitively, and the last option of a key wins.

| Option | Meaning |
|---|---|
| `title="Program.cs"` | the title; without one, a file name in a comment on one of the first four lines is the title and its line is removed (`title-from-comment`) |
| `frame=` `code`, `terminal`, `none` or `auto` | the frame; `auto` (default) gives a terminal frame to terminal languages (`bash`, `sh`, `shell`, `powershell`, `console`...) unless the title is a file name or the code starts with `#!` |
| `{1,3-5}` or `mark={...}` | marked lines; `ins={2}` (or `add=`) and `del={3}` (or `rem=`) inserted and deleted lines; `{"Label":2-3}` labels them |
| `"text"`, `/regex/` | inline markers, keyed the same way (`ins="text"`); a regex with groups marks its groups |
| `diff` language | `+` and `-` lines are inserted and deleted; `lang=ts` highlights the code in that language; `useDiffSyntax` reads a fence of another language as a diff |
| `collapse={4-10}` | collapsed sections; `collapseStyle=` `github`, `collapsible-start`, `collapsible-end` or `collapsible-auto`, `collapsePreserveIndent` |
| `showLineNumbers`, `startLineNumber=10` | line numbers (`line-numbers` sets the default) |
| `wrap`, `preserveIndent`, `hangingIndent=2` | line wrapping |

Every option is also given to the block template, in `code.options`.

### Templates

The default markup is `<figure class="code code-<frame>"><figcaption>...</figcaption><pre><code><span class="line">...`,
with a copy button, and the twoslash classes of other renderers (`twoslash-hover`, `twoslash-popup`,
`twoslash-error`...). `templates` replaces it with Liquid files of the includes folder, rendered with the page's data
(its `lang`, the `t` filter) and:

| Value | Meaning |
|---|---|
| `code.language`, `code.grammar` | the fence's language, and the grammar that highlighted it |
| `code.meta`, `code.options` | the meta as written, and its options by key |
| `code.title`, `code.frame`, `code.terminal` | the title, the frame (`code`, `terminal`, `none`), whether it is a terminal |
| `code.copy` | the code to copy (without the comment lines of a terminal, unless `copy-terminal-comments`) |
| `code.lines` | each line: `html`, `text`, `number`, `marker` (`mark`, `ins`, `del`), `label`, `indent` |
| `code.rows` | the lines grouped: each row has `lines` and `section` (`style`, `indent`, `from`, `to`) or nil |
| `code.line_numbers`, `code.number_width` | whether to show line numbers, and the width of the largest one |
| `code.wrap`, `code.max_line` | whether lines wrap, and the length of the longest line |
| `code.popup` | whether the block is a type in a popup |
| `node.kind` (popup template) | `hover`, `query`, `error`, `underline`, `highlight`, `tag` or `completion` |
| `node.content`, `node.type`, `node.docs`, `node.tags` | the hovered code, its type and documentation (HTML), and its tags (`name`, `html`, `dash`) |
| `node.text`, `node.code`, `node.level`, `node.name`, `node.character` | the text of an error or tag, its code, level, tag name, position |
| `node.completions`, `node.completions_prefix` | the completions (`name`, `kind`, `deprecated`) |

## Options

| Option | Type | Default | Meaning |
|---|---|---|---|
| `themes` | string or list | `[github-dark, github-light]` | bundled theme names (every theme of tm-themes), or paths of VS Code JSON theme files of the source folder; the first is the default, the others are selected by CSS |
| `min-contrast` | number, 0 or more | `5.5` | the WCAG contrast ratio token colors are raised to, on the background and on marked lines; `0` keeps the theme's colors |
| `grammars` | mapping of names to paths | none | grammar files of the source folder (`mylang: syntax/mylang.json`); each needs a `scopeName` |
| `aliases` | mapping of names to names | none | fence languages to grammar names |
| `templates` | mapping | Douze's markup | `block` and `popup`: Liquid files of the includes folder |
| `markers` | mapping | derived from each theme | the background of `mark`, `ins` and `del` lines: a color per theme, in the order of `themes` |
| `title-from-comment` | boolean | `true` | take the title from a file name in a comment of the first lines |
| `copy-terminal-comments` | boolean | `false` | keep the comment lines of a terminal block in the code to copy |
| `line-numbers` | boolean | `false` | show line numbers when the fence does not say (`showLineNumbers`) |
| `tab-width` | integer, 0 to 16 | `0` | replace tabs with that many spaces; `0` keeps them |
| `stylesheet` | string | none | a path of the output (`assets/code.css`): a stylesheet of the themes for the default markup |

## Requirements

None to install: the grammars, the themes and Oniguruma (a native library next to `douze`, like libvips) ship with
Douze.

## Caching

The tokens of each fence are kept in the cache by its language, its code and the step's settings (themes,
contrast, grammars, aliases, markers and the other options), so that a changed page highlights only its new fences.
The pages themselves are units like those of any map step: a page whose content did not change is not rendered again.

## Errors

```
douze.yml(3,3): error: option 'themes' needs at least one theme
douze.yml(3,3): error: option 'tab-width' of step 'highlight' must be 0 to 16
douze.yml(3,3): error: option 'min-contrast' must be a number, 0 or more
douze.yml(3,3): error: option 'templates' of step 'highlight' has an unknown template 'line': block or popup
douze.yml(3,3): error: option 'markers' of step 'highlight': 'add' must be mark, ins or del, with a color per theme (2)
```

During the build: `step 'highlight': unknown theme '...'; the bundled themes are ...`, `the grammar file '...' of '...'
does not exist`, `the grammar file '...' has no scopeName`, `the template '...' does not exist in the includes
folder`, and `invalid frame type '...' in a code fence: code, terminal, none or auto`.

## Example

```yaml title="douze.yml"
site:
  content: docs

steps:
  pages:
    inputs: "docs/**"
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: docs
  markdown:
    needs: permalink
    uses: markdown
  highlight:
    needs: markdown
    uses: highlight
    with:
      themes: [github-dark, github-light]
      stylesheet: assets/code.css
  write:
    needs: highlight
    uses: write
```

````md
```csharp title="Program.cs" {1}
var x = 1;
Console.WriteLine(x);
```
````

renders as a `<figure class="code code-code has-title">` with the caption `Program.cs`, the first line marked, and a
copy button; `/assets/code.css` holds the theme rules.

## Related

- [Syntax highlighting guide](../../guide/syntax-highlighting.md)
- [Code hovers guide](../../guide/code-hovers.md)
- [markdown](markdown.md), [hover-typescript](hover-typescript.md),
  [hover-csharp](hover-csharp.md), [hover-go](hover-go.md)
- [The `markdown.code` settings](../configuration.md)
