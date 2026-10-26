# Installation and tutorial checks

These scripts follow the installation page (`guide/install.md`) and the tutorial (`tutorial/*.md`) in clean Linux
containers, as a reader would, and check what each step produces.

```bash
docs/tests/docker/validate.sh
```

It needs Docker and network access (to the preview feed, the install script on GitHub, and npm for Pagefind). It
runs, each in a new container:

| Run | Image | What it does |
|---|---|---|
| install script | `ubuntu:24.04`, `debian:trixie-slim` | Installs douze with the `curl ... install.sh \| sh -s -- --preview` line of the installation page, checks `douze --version` in a new login shell, builds and serves the page's first site, builds the tutorial's blog unit by unit, then runs the install command again (an update) and the page's uninstall command |
| unsupported | `debian:bookworm-slim` | Checks that the installer refuses it with the message the page describes (glibc 2.36 is older than 2.38) |
| .NET tool | `mcr.microsoft.com/dotnet/sdk:10.0-noble` | Runs the page's `dotnet tool install` line, the tutorial with that tool, then the update and uninstall lines |

The images have the prerequisites of the installation page and nothing else: no .NET outside the SDK image, no Node.

## How the tutorial is checked

`extract-files.awk` writes the files that a page shows: every code fence with a `title="<path>"` meta, such as
```` ```html title="_includes/base.html" ````, is written to `<path>` in the blog's folder, in the order of the page, so
a later fence of the same file replaces it. The fences of the pages are the only source of the files: change a page,
and the check builds what it now says. Fences without a title, or whose title has a space, are not files.

`tutorial.sh` then, for each unit, extracts its files, runs `douze --plain` in the blog's folder, and checks the
output: the files of `_site`, strings of their HTML, the feed and the sitemap, the default workflow that
`--print-workflow` prints against the one unit 6 shows, `douze doctor`, `--timeline`, and `douze serve` (the live
reload script, a rebuild after an edit, the 404 page). Its last check is that the tree of `tutorial/index.md` names
every file of the finished blog.

Write a page so that it stays checkable:

- show each file in full in a fence with its `title`, relative to the blog's folder (`_content/index.md`);
- mark changed lines with `ins={...}`, never with `del={...}` lines that stay in the fence (they would be written);
- when a unit changes what a page shows, update the strings that `tutorial.sh` looks for.

## Options

```bash
docs/tests/docker/validate.sh --platform linux/amd64        # the x64 build, emulated on an arm64 machine
docs/tests/docker/validate.sh --images ubuntu:24.04         # one image
docs/tests/docker/validate.sh --douze /path/to/linux-build  # a local build instead of the latest preview
docs/tests/docker/validate.sh --no-dotnet --no-unsupported
```

`--douze` mounts a folder with a Linux `douze` and its native library (`libvips.so.42`), such as
the output of `dotnet publish Douze -c Release -r linux-arm64`, to check the pages against a version that is not
published yet. It skips the install steps.

`PAGES_DIR` sets the folder of the pages, the one that holds `guide/install.md` and `tutorial/`. By default it is the
first of `docs/_content/douze`, `docs/_content` and `docs` that has them.

`tutorial.sh` also runs without Docker, against any douze, on Linux or macOS:

```bash
DOUZE=$(command -v douze) PORT=8090 docs/tests/docker/tutorial.sh docs/_content/douze
```
