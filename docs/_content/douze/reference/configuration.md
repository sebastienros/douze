---
title: douze.yml
description: The full schema of douze.yml, the site, markdown and steps sections, with defaults and validation errors.
liquid: false
---

`douze.yml` at the root of the source folder, or the file given with `-w`, configures a site. It is a mapping with
three optional sections: `site`, `markdown` and `steps`. A site without the file, or a file without `steps`, builds
with the [default workflow](#default-workflow). An empty file is the same as no file.

```yaml title="douze.yml"
site:
  content: docs
  url: https://example.com

markdown:
  extensions: [pipetables, footnotes, alerts, customcontainers, attributes]

steps:
  site-data:
    inputs: "_data/*.json"
    uses: data
    with:
      scope: site
  pages:
    inputs: "docs/**"
    needs: site-data
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
    with:
      root: docs
  render:
    needs: permalink
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

Any other top-level key is an error: `unknown key 'foo'; expected 'site', 'markdown' or 'steps'`.

## site

The folders of the site and the values every template sees under `site`.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `content` | folder | `_content` if it exists, else the source folder | Pages, folder data files and any other file to publish. |
| `data` | folder | `_data` if it exists, else none | Site data files. |
| `includes` | folder | `_includes` if it exists, else the source folder | Layouts, includes and templates. |
| `shortcodes` | folder | `shortcodes` in the includes folder | Shortcode templates. |
| `output` | folder | `_site` in the current folder | The output folder, relative to the source folder. `-o` wins. |
| `url` | string | none | The site's url: `site.url` in templates, used by `absolute_url` and `relative_url`. |
| `parallelism` | positive integer | the processor count | The maximum number of units that run at once. `-p` wins. |
| `locales` | mapping | none | The locales of the site, see [locales](#sitelocales). |

Folders are relative to the source folder and must stay inside it (no `..`). The output folder, the cache folder and
`.douze` are never sources when they are inside the source folder.

`site.url` is merged with the site data named `site` (`_data/site.json`): a template can read `site.title` from the
data file and `site.url` from `douze.yml`, unless the data file defines `url` too. See
[template data](template-data.md#site).

```yaml title="douze.yml"
site:
  content: docs
  data: data
  includes: templates
  shortcodes: templates/components
  output: public
  url: https://example.com
  parallelism: 8
```

### site.locales

```yaml title="douze.yml"
site:
  locales:
    default: en
    root: docs
    fallback: true
    strings: i18n
    languages:
      en:
        label: English
      ja:
        label: 日本語
        lang: ja
      ar:
        label: العربية
        lang: ar
        dir: rtl
```

| Key | Type | Default | Meaning |
|---|---|---|---|
| `default` | locale folder name | required | The locale whose pages have no url prefix. |
| `root` | folder | the content folder | The folder, relative to the content folder, whose subfolders are the locales. |
| `fallback` | `true` or `false` | `false` | The [localize step](steps/localize.md) copies the default locale's untranslated pages under every other locale. |
| `strings` | string | `i18n` | The site data of the UI strings for the `t` filter: `_data/i18n/en.yml`, `_data/i18n/ja.yml`. |
| `languages` | mapping | none | The locale folders, each with its settings. The default locale may be listed for its settings; it has no folder. |

Each language takes:

| Key | Default | Meaning |
|---|---|---|
| `label` | the key | The name shown in a language switcher. |
| `lang` | the key | The `lang` attribute value. |
| `dir` | `ltr` | `ltr` or `rtl`. |

A page of `docs/ja/` is a page of `ja`, any other page under `docs` a page of `en`. Templates see the settings as
`site.locales`: `default`, `fallback`, `strings` and `languages`, the list of every locale (`locale`, `label`, `lang`,
`dir`, `prefix`), the default first. See [Internationalization](../guide/internationalization.md).

## markdown

How Markdown renders, for the [markdown step](steps/markdown.md) and everything else that renders Markdown (the
`markdownify` filter, `include_markdown`, shortcode bodies). Without the section, Markdown is CommonMark without
extensions. `markdown:` without a value is the same as no section.

```yaml title="douze.yml"
markdown:
  extensions: [pipetables, footnotes, tasklists, definitionlists, attributes, autolinks, alerts, customcontainers]
  aside:
    tag: aside
    class: aside
    kinds: [note, tip, caution, danger]
    titles:
      note: Note
  links: true
  code:
    class-prefix: language-
    passthrough: [mermaid]
  images:
    widths: [640, 1280]
    formats: [webp]
    quality: 80
    output: assets/images
    loading: lazy
```

| Key | Type | Default | Meaning |
|---|---|---|---|
| `extensions` | string or list | `[pipetables, footnotes, tasklists, definitionlists, attributes, autolinks, alerts, customcontainers]` | Markdig extensions, applied in order but `attributes` last. |
| `aside` | mapping | see below | How `:::note` containers and `> [!NOTE]` alerts render. |
| `links` | `true` or `false` | `true` | Relative links to `.md` files of the content folder become the urls of their pages. |
| `code.class-prefix` | string | `language-` | The prefix of the language class of a fence's `code` element. |
| `code.passthrough` | string or list | `[mermaid]` | Fence languages rendered as `<pre class="mermaid">` without `code`, for a client script. |
| `images` | mapping | see below | The defaults of the image features. |

### extensions

The names are Markdig's: `common`, `advanced`, `alerts`, `pipetables`, `gfm-pipetables`, `emphasisextras`,
`listextras`, `hardlinebreak`, `footnotes`, `footers`, `citations`, `attributes`, `gridtables`, `abbreviations`,
`emojis`, `definitionlists`, `customcontainers`, `figures`, `mathematics`, `bootstrap`, `medialinks`, `smartypants`,
`tasklists`, `diagrams`, `nofollowlinks`, `noopenerlinks`, `noreferrerlinks`, `nohtml`, `yaml`, `nonascii-noescape`,
`autolinks`, `globalization`, `cjk-friendly-emphasis`, plus `none`, and `strikethrough`, Douze's `emphasisextras`
without subscript, superscript, inserted and marked text. `advanced` is Markdig's set without `autoidentifiers`:
heading ids are the [headings step](steps/headings.md)'s.

| Error | Cause |
|---|---|
| `unknown Markdown extension 'tabels'; did you mean 'pipetables'?` | An unknown name; common other spellings (`tables`, `admonitions`, `math`) suggest the right one. |
| `use the headings step for heading ids` | `autoidentifiers`. |

The `extensions` option of a `markdown` step replaces the extensions for that step and keeps the other settings.

### aside

| Key | Default | Meaning |
|---|---|---|
| `tag` | `aside` | The element. |
| `class` | `aside` | The block class; `<class>--<kind>`, `<class>__title` and `<class>__content` derive from it. |
| `kinds` | `[note, tip, caution, danger]` | The known kinds. Another kind renders as the first, with a warning. |
| `titles` | `Note`, `Tip`, `Caution`, `Danger` | The default title per kind; a kind without one is its name, capitalized. |
| `icons` | none | Markup (an inline SVG) written before the title, per kind. |
| `titles-from` | none | Read and validated, not used yet. |

A `:::note` container renders as:

```html
<aside aria-label="Note" class="aside aside--note"><p class="aside__title" aria-hidden="true">Note</p><div class="aside__content">...</div></aside>
```

`:::tip[Before you start]` sets the title. The alerts `IMPORTANT` and `WARNING` are `note` and `caution`. `titles` and
`icons` keys must be kinds of `kinds`.

### images

The defaults of the `image` tag, the `image_url` filter and the [images](steps/images.md) and
[pictures](steps/pictures.md) steps.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `widths` | list of positive integers | the source's width | The widths of the variants. |
| `formats` | string or list | the source's format | `webp`, `avif`, `png`, `jpeg`. |
| `quality` | integer, 1 to 100 | `80` | The quality of lossy formats. |
| `output` | folder | `/assets/images/` | The folder of the variants. |
| `loading` | `lazy` or `eager` | `lazy` | The `loading` attribute of the `<img>`. |

## steps

`steps` maps step names to steps. Source files enter the steps whose `inputs` match them; the files that come out of
a step enter the steps that `need` it. A step consumes the entering files its `match` selects; the others pass
through it unchanged. Steps run as soon as their files are available, in parallel, so their order in the file does not
matter. `steps:` without a value is the default workflow. See [Workflow architecture](../guide/workflow-architecture.md).

Step names are lowercase letters, digits and dashes, start with a letter, and have at most 64 characters. `source` is
reserved.

A step has either `uses` (a [built-in step](steps/index.md)) or `run` (a [command](commands.md)).

| Field | Type | Default | Meaning |
|---|---|---|---|
| `inputs` | glob or list | none | The source files that enter this step, relative to the source folder. |
| `needs` | step name or list | none | The steps whose output files, and data, enter this step. |
| `match` | glob or list | the step's own | The entering files the step consumes. The others pass through. |
| `uses` | built-in step name | | The built-in step to run. |
| `with` | mapping | none | The options of the built-in step, or for `mode: worker`, the options sent to the worker. |
| `group-by` | `file`, `dir` or a Liquid template | none | Processes the files in groups, one unit per group. Only some steps support it (`liquid`, batch commands). |
| `incremental` | `true` or `false` | `true` | `false` runs the step on every build instead of reusing cached results. |
| `enabled` | `true` or `false` | `true` | `false` leaves the step out: the steps that need it need what it needs instead. |
| `run` | string | | The command line of a command step. |
| `tool` | `name` or `name@version` | | A tool Douze downloads for the command. |
| `mode` | `filter`, `batch` or `worker` | `batch` | How files reach the command. |
| `outputs` | glob or list | every file | Batch only: the files read back from the outputs folder. |
| `sources` | glob or list | none | Files whose content is part of the command's cache key (its scripts). |
| `version` | string | empty | Part of the command's cache key: change it to run the command again. |
| `env` | mapping of names to strings | none | Environment variables of the command; values are templates. |
| `cwd` | folder | the source folder | The working directory of the command, relative to the source folder. |
| `timeout` | duration | none | How long the command, or each worker request, may take. |
| `stderr` | `fail`, `warn` or `ignore` | `fail` | What output on stderr means. |
| `shell` | `true` or `false` | `true` | Run through `/bin/sh -c` (`cmd.exe /d /s /c` on Windows), or split the line and run the program directly. |

`tool`, `mode`, `outputs`, `sources`, `version`, `env`, `cwd`, `timeout`, `stderr` and `shell` are only valid with
`run`. The command fields are described in [Command steps and workers](commands.md).

### needs, inputs and match

- A step with `inputs` receives the source files that match them. A step with `needs` receives the files that come out
  of those steps, and sees the data they published (site data, folder data, collections). A step can have both. A
  step with neither is an error.
- `match` selects among the entering files. It is matched against a file's current path, which changes when a
  [permalink step](steps/permalink.md) moves files to their output paths (`docs/guide.md` becomes
  `/guide/index.html`).

### group-by

- `file`: one group per file path.
- `dir`: one group per folder.
- Any other value is a Liquid template rendered for each file with `file` (`path`, `dir`, `name`, `ext`) and `data`
  (the file's data); files with the same result are a group.

A step that does not support grouping reports `step '<name>' does not support 'group-by'`.

### enabled

`enabled: false` is a switch between two steps: the disabled step is removed, and the steps that need it need its
own `needs`.

```yaml title="douze.yml"
steps:
  pages:
    inputs: "**"
    uses: frontmatter
  minify:
    needs: pages
    run: "npx html-minifier-terser"
    mode: filter
    match: "**/*.html"
    enabled: false
  write:
    needs: minify
    uses: write
```

With `minify` disabled, `write` needs `pages`.

### Globs

Globs match virtual paths, which start at the source folder (`docs/a.md` is `/docs/a.md`; a leading `/` in a glob is
ignored). Matching is case sensitive.

| Syntax | Matches |
|---|---|
| `*` | Any characters within a folder. |
| `**` | Any characters across folders. `docs/**` also matches the folder itself. |
| `?` | One character, not `/`. |
| `{a,b}` | Alternatives, which can nest. |
| `[a-z]`, `[abc]` | A character of the class. |
| `[!a-z]` | A character outside the class. |
| `\` | Escapes the next character. |
| `!` (leading) | Excludes. In a list, the last matching glob decides. |

`["docs/**", "!**/*.yml"]` is every file of `docs` but the YAML files. An invalid glob is an error such as
`invalid pattern '[a': unterminated character class`; the other messages are `empty pattern`, `unmatched '}'`,
`trailing escape character` and `unterminated alternative`.

### Durations

`timeout` is a positive number followed by a unit: `500ms`, `30s`, `5m`, `1h`. Anything else is an error:
`invalid timeout '10'; use 30s, 5m or 1h`.

## Default workflow

Without `steps`, Douze runs this workflow for a site with `_content`, `_data` and `_includes` folders.
`douze --print-workflow` prints it with one line per step:

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

For a site whose content is the source folder (no `_content`) and without `_data`, there is no `site-data` step and
the data, includes and output folders and `douze.yml` are excluded from the pages:

```yaml
steps:
  folder-data:
    inputs: ["**/*.{yml,yaml,json,xml}", "!douze.yml"]
    uses: data
    with:
      scope: folder
  pages:
    inputs: ["**", "!**/*.{yml,yaml,json,xml}", "!douze.yml"]
    needs: folder-data
    uses: frontmatter
  permalink:
    needs: pages
    uses: permalink
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

The output of `--print-workflow` is valid `douze.yml`: copy it into the file to start from the default workflow.
Fields with their default value are left out.

## Validation errors

Every error of the file is reported before the build stops with exit code 2, each with the position of the value it
is about, or of the step's name for the errors about how steps fit together:

```text
douze.yml(12,5): error: unknown field 'if' on step 'render'
douze.yml(14,3): error: step 'render' needs unknown step 'markdwn'; did you mean 'markdown'?
```

The errors of the file's shape are reported first; the errors of the graph (unknown steps, cycles, step options) once
the shape is valid.

| Message | Cause |
|---|---|
| `a workflow file must be a mapping with 'site', 'markdown' and 'steps'` | The file is a list or a scalar. |
| `duplicate key '<key>'`, `keys must be strings` | Any mapping. |
| `unknown key '<key>' in 'site'; expected ...` | Also for `markdown`, `markdown.aside`, `markdown.code`, `markdown.images`, `site.locales` and its languages. |
| `'<key>' must be a folder inside the source folder` | A `site` folder with `..`. |
| `'parallelism' must be a positive integer` | |
| `'site.locales' needs 'default', the locale whose pages have no url prefix` | |
| `'default' must be a locale folder name`, `'<name>' is not a locale folder name` | Names with `/`, `\`, spaces, `.` or `..`. |
| `'dir' must be 'ltr' or 'rtl'` | |
| `unknown aside kind '<kind>'; expected ...` | A `titles` or `icons` key that is not in `kinds`. |
| `'kinds' must list at least one kind` | |
| `'quality' must be an integer from 1 to 100`, `'loading' must be 'lazy' or 'eager'`, `'widths' must be a list of positive integers` | `markdown.images`. |
| `'steps' must be a mapping of step names to steps` | |
| `invalid step name '<name>': use lowercase letters, digits and dashes` | |
| `step name '<name>' is longer than 64 characters` | |
| `'source' is a reserved step name` | |
| `step '<name>' needs 'uses' or 'run'` | |
| `step '<name>' cannot have both 'uses' and 'run'` | |
| `unknown field '<field>' on step '<name>'` | With a suggestion when a field is close. |
| `'<field>' must be <type>` | A field of the wrong type, such as `'needs' must be a string or a list of strings`. |
| `'<field>' is only valid with 'run'` | A command field on a `uses` step. |
| `'with' is only valid with 'uses' or with 'mode: worker'` | |
| `'outputs' is only valid with 'mode: batch'` | |
| `step '<name>' is defined twice` | |
| `step '<name>' needs unknown step '<need>'` | With a suggestion. |
| `step '<name>' has no 'needs' and no 'inputs', so nothing enters it` | |
| `steps form a cycle: layout -> markdown -> layout` | |
| `unknown built-in step '<uses>'` | With a suggestion. |
| `step '<name>' does not support 'group-by'` | |
| `unknown option '<option>' for step '<name>'` | A `with` key the built-in step does not declare. |
| `option '<option>' of step '<name>' must be <type>` | A `with` value of the wrong type. |
| `step '<name>' needs the option '<option>'` | A required `with` option is missing. |

A workflow without a `write` step builds with a warning:
`no 'write' step: nothing will be written to the output folder`.
