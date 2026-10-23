---
title: Data files
description: Site data and folder data from JSON, YAML and XML files, how names resolve, data-driven pages, and fetching remote data.
liquid: false
---

Templates read data from three places: the front matter of the page, data files that apply to a folder, and site data
files that every page sees. Data files are JSON (`.json`), YAML (`.yml`, `.yaml`) or XML (`.xml`). Douze has no
JavaScript data files and fetches nothing by itself. Remote data comes from a command step or a script that runs
before the build. This page covers the three kinds of data, how a name resolves, pages generated from data files, and
remote data.

## Site data

Each file of the data folder (`_data` when it exists) is a value named after the file, whatever it holds: a list, an
object or a single value.

| File | Name in templates |
|---|---|
| `_data/colors.yml` | `colors` |
| `_data/site.json` | `site` |
| `_data/pkgs/Contoso.Hosting.json` | `pkgs["Contoso.Hosting"]` |
| `_data/feed.xml` | `feed` |

A subfolder is an object of its files, and the name of a file is everything before its last extension, so a dot in a
file name stays in the key. Two files cannot define the same name (`colors.yml` and `colors.json`): the build fails
and names both.

```yaml title="_data/colors.yml"
- red
- green
```

```liquid title="_content/index.html"
{{ colors | join: ", " }}
{{ pkgs["Contoso.Hosting"].version }}
```

`site` is special: `_data/site.json` is merged with the `site` section of `douze.yml`, so `{{ site.title }}` can come
from the data file and `{{ site.url }}` from `douze.yml`. A key that both define comes from the data file.

An XML file becomes an object with its root element by name. Each element is `{ name, attributes, elements }`, plus
`text` when it holds only text:

```xml title="_data/feed.xml"
<feed><item id="1">First</item><item id="2">Second</item></feed>
```

```liquid
{% for item in feed.feed.elements %}{{ item.attributes.id }}={{ item.text }} {% endfor %}
```

## Folder data

A data file in the content folder applies its top-level keys to the pages of its folder and of its subfolders. Its
name does not matter: `blog/blog.yml` and `blog/defaults.json` do the same, and when a folder has several data files,
they are read in path order and a later file's keys win.

```yaml title="_content/blog/blog.yml"
layout: post.liquid
tags: post
author: Grace
```

Every page of `blog/` now has a layout, the `post` tag, and an `author`, unless its front matter sets them. A folder
data file must hold an object. It is not published.

:::caution
In the default workflow, every `.json`, `.yml`, `.yaml` and `.xml` file of the content folder is folder data, so it is
not copied to the output. A JSON file you want to publish is a template named `name.json.liquid`, or lives outside
the folders that the default `inputs` read.
:::

## How a name resolves

A name in a template is looked up in this order, and the first that has it wins:

1. The page's own data: front matter, `page.date`, `url`, variables captured or assigned in the page.
2. The data of the page's folder, then of each parent folder.
3. Site data.
4. The `site` section of `douze.yml`.
5. The front matter of the page's layouts.

So a page can set `layout` itself, inherit it from its folder, or from a `_data/layout.yml` that holds `post.liquid`.
A layout's front matter gives defaults that everything above overrides:

```liquid title="_includes/post.liquid"
---
footer: Written with Douze
---
<article>{{ content }}</article>
<footer>{{ footer }}, by {{ author }}</footer>
```

## Pages from data files

The `parse` step reads a data file into the file's own data, under `as` (`data` by default), and keeps it in the
workflow. With a `liquid` step that has a `template` and `group-by: file`, each data file becomes a page:

```yaml title="douze.yml"
steps:
  packages:
    inputs: "packages/*.json"
    uses: parse
    with:
      as: package
  package-pages:
    needs: packages
    uses: liquid
    group-by: file
    with:
      template: package.liquid
      permalink: "packages/{{ group.files[0].data.package.name | slug }}/"
  write:
    needs: package-pages
    uses: write
```

```json title="packages/hosting.json"
{ "name": "Contoso.Hosting", "version": "1.2.0" }
```

```liquid title="_includes/package.liquid"
{%- assign package = group.files[0].data.package -%}
<h1>{{ package.name }}</h1>
<p>Version {{ package.version }}</p>
```

This writes `packages/contoso-hosting/index.html`. In a group, the template sees the group's files as `group.files`,
each with its `data`. Add the steps to your full workflow (`douze --print-workflow` prints the default one) and make
`write` need both branches. To list the packages on another page, make them site data too, or paginate a site data
file (see [Pagination](pagination.md)).

## Remote data

Douze fetches nothing over the network during a build, which keeps builds reproducible. There are two ways to use
remote data.

**A script before the build.** Write the data into `_data`, then build. This is the simplest, and the cache treats
the file like any other data file:

```sh
curl -fsSL https://api.github.com/repos/sebastienros/douze/releases -o _data/releases.json
douze
```

Add the file to `.gitignore` if it should not be committed.

**A command step.** A batch command that writes into `{{ outputs }}` adds the files it writes to the build, at the
same path. Put a `data` step after it, and the files under `_data` become site data:

```sh title="scripts/fetch.sh"
#!/bin/sh
# $1: the folder the command writes its files to
mkdir -p "$1/_data"
curl -fsSL https://api.github.com/repos/sebastienros/douze/releases -o "$1/_data/releases.json"
```

```yaml title="douze.yml"
steps:
  fetch:
    inputs: scripts/fetch.sh
    run: "./scripts/fetch.sh {{ outputs }}"
    incremental: false
  site-data:
    inputs: "_data/**"
    needs: fetch
    uses: data
    with:
      scope: site
  pages:
    inputs: "_content/**"
    needs: site-data
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
  layout:
    needs: markdown
    uses: layout
  write:
    needs: layout
    uses: write
```

```liquid
{% for release in releases %}<li>{{ release.tag_name }}</li>{% endfor %}
```

A step needs `inputs` or `needs`, so the script itself is the step's input, and a change to the script runs it again.
`incremental: false` runs it on every build, since the remote data can change without any local file changing. When
the data comes back the same, the pages that read it are not rendered again. In `douze serve`, the step runs again only
when something triggers a rebuild.

## Data and the cache

A page depends on the data it reads, not on whole files: editing `_data/site.json` renders again the pages that read
the values that changed. See [Cache and incremental builds](cache.md).

## See also

- [data step reference](../reference/steps/data.md)
- [parse step reference](../reference/steps/parse.md)
- [Template data](../reference/template-data.md)
- [Command steps](command-steps.md)
- [Collections](collections.md)
- [Pagination](pagination.md)
