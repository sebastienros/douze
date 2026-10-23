---
title: "Unit 1: Setup"
description: Install Douze, create the folder of the blog, write a first page and run the development server.
---

In this unit you will:

- install Douze and check that it runs,
- create the folder of the blog and its first page,
- run the development server and see the page reload as you edit it,
- build the site and look at what Douze writes.

## Step 1: install Douze

Run the install script of your system:

{% tabs %}

{% tab "macOS and Linux" %}

```sh
curl -fsSL https://raw.githubusercontent.com/sebastienros/douze/main/install.sh | sh -s -- --preview
```

{% endtab %}

{% tab "Windows (PowerShell)" %}

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/sebastienros/douze/main/install.ps1))) -Preview
```

{% endtab %}

{% endtabs %}

Open a new terminal, so that it sees the updated `PATH`, and check the installation:

```sh
douze --version
```

It prints the installed version, such as `1.0.0-preview.36`. If your shell does not find `douze`, see
[Installation](../guide/install.md#check-the-installation) for other ways to install it and for troubleshooting.

## Step 2: create the site folder

A Douze site is a folder. Create one for the blog, with a `_content` folder inside it:

```sh
mkdir my-blog
cd my-blog
mkdir _content
```

`_content` holds the pages of the site and every other file to publish. Douze turns each page into a file of the
output folder, at the same place: `_content/index.md` becomes the home page.

:::note[Why _content?]
Without a `_content` folder, the whole site folder is the content. Keeping the pages in `_content` leaves room next to
it for the folders that are not published: layouts and includes (`_includes`), data (`_data`) and configuration. See
[Project structure](../guide/project-structure.md).
:::

## Step 3: write the home page

Create `_content/index.md` with this content:

```md title="_content/index.md"
---
title: My blog
---
# Welcome to my blog

This is where I write about learning Douze.
```

The block between the two `---` lines is the page's **front matter**: YAML data about the page. Here it gives the page a
title, which you will use in unit 2. The rest is Markdown.

## Step 4: start the development server

From the `my-blog` folder, run:

```sh
douze serve
```

Douze builds the site, then serves it and watches your files:

```txt
✓ folder-data       0 files    0.00 s
✓ pages             1 file     0.00 s
✓ permalink         1 file     0.00 s
✓ collections       1 file     0.00 s
✓ paginate          1 file     0.00 s
✓ render            1 file     0.00 s
✓ markdown          1 file     0.00 s
✓ layout            1 file     0.00 s
✓ write             1 file     0.00 s
Build succeeded: 9 steps, 1 file written in 0.03 s, 9 units executed
Serving _site at http://localhost:8080/
Watching . for changes (Ctrl+C to stop)
```

Each line is a **step** of the build: reading data files, front matter, computing output paths, rendering Liquid and
Markdown, applying layouts, writing files. You will meet each of them in this tutorial, and change them in unit 6.

Open `http://localhost:8080/` in your browser. You see the heading "Welcome to my blog" and the paragraph under it.

Now edit `_content/index.md`: change the paragraph, and save. The terminal prints one line for the rebuild, and the page
in the browser reloads by itself:

```txt
_content/index.md changed: 1 file written in 0.01 s, 8 units executed, 1 cached
```

Keep `douze serve` running while you follow the tutorial: every page you add or change shows up in the browser when you
save it.

:::tip[Another port]
If port 8080 is taken, run `douze serve --port 3000`.
:::

## Step 5: look at the output

Douze writes the site to the `_site` folder. Open `_site/index.html` in your editor:

```html
<h1>Welcome to my blog</h1>
<p>This is where I write about learning Douze.</p>
```

That is the whole page: the HTML of your Markdown, and nothing else yet. There is no `<html>` or `<head>`, because the
page has no layout. You will add one in unit 4.

When the development server serves a page, it adds a small live reload script to it. The file in `_site` does not have
it: what is in `_site` is what you publish.

You can also build the site once, without a server, by running `douze` alone (`douze build` does the same). Stop the
server with Ctrl+C first, or run it from another terminal:

```sh
douze
```

Next to `_site`, Douze created a `.douze` folder. It is the build cache: Douze stores the result of each piece of work,
and the next build reuses what did not change. That is why a second build without changes says `0 units executed`. You
can delete it at any time, or build without it with `douze --no-cache`. See [Cache and incremental
builds](../guide/cache.md).

## Step 6: ignore the generated folders

If you keep the blog in Git (you will need it to deploy in unit 6), the output and the cache should stay out of it.
Create a `.gitignore` file in the `my-blog` folder:

```txt title=".gitignore"
_site/
.douze/
```

Then create the repository:

```sh
git init
git add .
git commit -m "My blog"
```

:::tip[Check your understanding]

1. Which folder holds the pages of the site?
2. What does `douze serve` do that `douze` does not?
3. Which folders should not be committed?

:::

<details>
<summary>Answers</summary>

1. `_content`. Without it, the site folder itself holds the pages.
2. It serves the site on `http://localhost:8080/`, rebuilds it when a file changes, and reloads the pages in the
   browser.
3. `_site`, the output, and `.douze`, the build cache.

</details>

## Next

In [unit 2](2-pages.md), you will add pages, blog posts and links between them, and use front matter in templates.
