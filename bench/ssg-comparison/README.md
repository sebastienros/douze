# Static site generator comparison

A rerun of Zach Leatherman's [Which generator builds Markdown the fastest?](https://www.zachleat.com/web/build-benchmark/)
([bench-framework-markdown](https://github.com/zachleat/bench-framework-markdown)) with Douze, Hugo, Eleventy, Astro and
Jekyll at their current versions, each in its own Docker image.

```bash
bench/ssg-comparison/run.sh                      # every generator, 250 to 4000 files, 3 runs
bench/ssg-comparison/run.sh douze eleventy       # some generators
SIZES="250 1000" RUNS=1 bench/ssg-comparison/run.sh
bench/ssg-comparison/run-docs-heavy.sh           # the docs-heavy scenario (below), 1000 to 16000 pages
```

It needs Docker (with BuildKit) and git. Results go to `results.json` (every run, plus the fastest and median of each
size) and `results.md`; build logs, versions and the environment to `.work/out/`.

## What it measures

As in the original benchmark: for 250, 500, 1000, 2000 and 4000 Markdown files, each generator builds its site 3 times;
before each run the output folder, the generator's caches and the posts are deleted, then the posts are copied in. The
time is the wall clock of the build command only, measured inside the container with bash's `EPOCHREALTIME` (container
startup, cleaning and copying are not timed). The original kept the fastest run; `results.md` shows the fastest and the
median.

Each run also counts the HTML files written and the post pages; a run whose build fails or whose post page count is not
the number of Markdown files is reported as failed.

The Markdown samples are the benchmark's (`_markdown-samples/<size>`, borrowed from Sean C Davis's SSG build performance
repository), at the commit pinned in `versions.env`: a `title` in front matter and three paragraphs of lorem ipsum,
about 1 KB each, no headings, lists or code.

## Files

| File | Contains |
|---|---|
| `run.sh` | Entry point, on the host: clones the benchmark at its pinned commit, archives the Douze sources (`git archive HEAD`), builds an image per generator, runs it, then writes the report |
| `bench.sh` | Entry point of every image: the clean, copy, build and count commands of each generator, and the timing loop |
| `report.mjs` | Aggregates `.work/out/*/*.jsonl` into `results.json` and `results.md` (runs in the Node image) |
| `versions.env` | Every pinned version: base images, generators, starters, benchmark commit |
| `docker/*.Dockerfile` | One image per generator |
| `scenarios/douze/` | The Douze blog |
| `run-docs-heavy.sh`, `bench-docs-heavy.sh`, `report-docs-heavy.mjs`, `docker/docs-heavy/`, `scenarios/docs-heavy/` | The docs-heavy scenario (below): its entry point, timing loop, report, images, page generator and sites |

## Scenarios

| Generator | Starter | Posts go to | Build command | Cleaned before each run |
|---|---|---|---|---|
| Douze | `scenarios/douze`, written for this comparison as the equivalent of eleventy-base-blog | `_content/blog/` | `douze --no-cache -q` | `_site`, `.douze` |
| Hugo | The benchmark repository's `hugo/quickstart` (quickstart without a theme, one `layouts/posts/single.html`) | `content/posts/` | `hugo --quiet` | `public`, `resources/_gen`, `~/.cache/hugo_cache` |
| Eleventy | [eleventy-base-blog](https://github.com/11ty/eleventy-base-blog) at the pinned commit, `npm ci`, its sample posts removed | `content/blog/` | `npx @11ty/eleventy --quiet` | `_site`, `.cache`, `node_modules/.cache` |
| Astro | The [blog example](https://github.com/withastro/astro/tree/main/examples/blog) at the release tag, its sample posts removed, `description` and `pubDate` given defaults in the collection schema (the samples only have a title) | `src/content/blog/` | `npx astro build --silent` | `dist`, `.astro`, `node_modules/.astro`, `node_modules/.vite` |
| Jekyll | `jekyll new` (minima, jekyll-feed, jekyll-seo-tag), welcome post removed, `JEKYLL_ENV=production` | `_posts/`, renamed `2022-07-29-<name>.md` (Jekyll requires the date prefix) | `bundle exec jekyll build --quiet` | `_site`, `.jekyll-cache`, `.sass-cache` |

The original article used eleventy-base-blog, Astro's blog example and Hugo's quickstart without a theme; the
benchmark repository has since been reduced to bare projects (no layout for Eleventy and Astro). This comparison uses
the starters of the article, at their current versions, and the repository's Hugo project as it is.

What each site writes for N posts:

| Generator | HTML pages | Other outputs |
|---|---|---|
| Douze | N posts + home, archive, about, tags, 404 | Atom feed (10 latest, titles and links), sitemap |
| Eleventy | N posts + home, archive, about, tags, 404 | Atom feed (10 latest, full content), sitemap, passthrough copy; the image transform and id attribute plugins parse every page |
| Astro | N posts + home, about, blog index | RSS feed, sitemap, one optimized image, bundled CSS and fonts |
| Jekyll | N posts + home (lists every post), about, 404 | Atom feed (10 latest), minima's Sass compiled |
| Hugo | N posts only (no home or list template) | RSS and sitemap, Hugo's built-in templates |

### The Douze blog

`scenarios/douze` follows eleventy-base-blog page by page, with Douze's default workflow (no `steps` in `douze.yml`,
only `site.url`):

- `_includes/base.liquid`: the page shell, with eleventy-base-blog's `index.css` inlined, a navigation list from
  `_data/nav.yml` with `aria-current`, and the metadata of `_data/metadata.json`.
- `_includes/home.liquid` (message box, its CSS inlined) and `_includes/post.liquid`: title, date, tags, content, and
  links to the previous and next posts, found with the `previous_item` and `next_item` filters.
- `_content/blog/blog.json`: every post gets the `posts` tag and the post layout.
- `index.html` (the 3 latest posts and a link to the archive), `blog.html` (every post), `about.md`, `tags.html`,
  `404.md`, `feed/feed.xml.liquid` (Atom, 10 latest posts) and `sitemap.xml.liquid`.

Not reproduced from eleventy-base-blog: the image transform (the samples have no images), heading ids and anchors (the
samples have no headings), syntax highlighting CSS on posts (no code), per-page CSS and JS bundles, the feed's XSL
stylesheet, the post contents in the feed, and tag pages (the samples have no tags but `posts`, so eleventy-base-blog
writes none either).

Douze runs with `--no-cache`, so that every run is a clean build like the others'. A normal `douze` build also writes
its cache (`.douze/`); the "rebuild, cache warm" rows are `douze -q` run again with that cache and nothing changed.

## Images

| Image | Base | Generator |
|---|---|---|
| `ssgbench-douze` | `DOTNET_SDK_IMAGE` with clang and zlib1g-dev to build, `DOTNET_RUNTIME_DEPS_IMAGE` to run | `dotnet publish Douze -c Release` from the archived sources: native AOT with the Satori GC, as released |
| `ssgbench-hugo` | `DEBIAN_IMAGE` | `hugo_extended` release binary |
| `ssgbench-eleventy` | `NODE_IMAGE` | eleventy-base-blog's `package-lock.json` |
| `ssgbench-astro` | `NODE_IMAGE` | `npm install` of the example, then `astro` at the exact version |
| `ssgbench-jekyll` | `RUBY_IMAGE` with build-essential | `gem install jekyll`, then `jekyll new` and `bundle install` |

Each container gets every CPU and all the memory of the Docker VM, and the site lives in the container's own file system
(only `/out` is mounted), so that the macOS file sharing layer is not part of the measure.

## The docs-heavy scenario

`run-docs-heavy.sh` gives each generator real work per page, so that parallelism matters. `scenarios/docs-heavy/generate.mjs`
(seeded: page i only depends on i) writes N documentation pages of about 8 KB (6 to 10 KB for 90% of them): a `title`
and a `description` in front matter, 3 or 4 h2 sections with h3s, lists, a table, inline code and 3 to 6 code fences in
js, ts, csharp, yaml, bash, json and html. It also writes `sidebar.json`, the titles and urls of the 50 first pages.

```bash
bench/ssg-comparison/run-docs-heavy.sh                   # every generator, 1000 4000 16000 pages, 3 runs
SIZES="1000" RUNS=1 bench/ssg-comparison/run-docs-heavy.sh douze hugo
```

Results go to `results-docs-heavy.json` and `results-docs-heavy.md`; logs, versions and one sample page per generator
to `.work/docs-heavy/out/`. Timing is the same as `run.sh` (wall clock of the build command inside the container, output
and caches deleted before each run, 3 runs, fastest and median), plus the CPU time of the build process tree (user + sys
from bash's `times`), reported as utilization ((user + sys) / wall). A build is stopped after 15 minutes (`TIMEOUT`). A run
fails unless its build succeeds and every one of the N pages is written, has highlighted tokens (a pattern per generator,
in `bench-docs-heavy.sh`) and has h2 ids.

Each site is written for this scenario (`scenarios/docs-heavy/<generator>/`), with one layout: the sidebar of the 50
first pages (from the data file, the same for all), the title, the content and, where it is cheap, a table of contents.

| Generator | Highlighting | Heading ids | Table of contents | Warm rebuild |
|---|---|---|---|---|
| Douze | `highlight` step, theme github-dark, CSS variables per token, writes `assets/code.css` | `headings` step (h2, h3) | `toc` from the `headings` step, nested | `douze -q` with its disk cache |
| Hugo | Chroma, `noClasses` (inline styles), github-dark | Goldmark, built in | `.TableOfContents` (h2, h3), nested | none: Hugo has no persistent build cache for pages |
| Eleventy | `@11ty/eleventy-plugin-syntaxhighlight` (Prism, classes) | `markdown-it-anchor` (h2, h3) | none (needs another plugin) | none: `--incremental` only applies to watch and serve |
| Astro | Shiki, its default (github-dark, inline styles) | built in | flat list from `render()`'s `headings` | rebuild keeping the content layer data store (`node_modules/.astro`) |
| Jekyll | Rouge (classes), kramdown GFM input | kramdown `auto_ids` | none (needs a plugin or a marker in each page) | `jekyll build --incremental` (experimental) |

Hugo also writes a `/docs/` section page (no list of pages). Prism and Rouge output classes and the sites ship no theme
stylesheet for them; Douze, Hugo and Astro put the colors in the HTML. Only Douze's layout is Liquid with a `toc` filter;
the others use their own template language.
