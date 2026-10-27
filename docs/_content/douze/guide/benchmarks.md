---
title: Benchmarks
description: Build times of Douze, Hugo, Eleventy, Astro and Jekyll on Zach Leatherman's Markdown benchmark and on a docs-heavy scenario, and the internal benchmarks of large Douze sites.
---

This page compares the build time of Douze with other generators on a public benchmark, then summarizes the internal
benchmarks that track Douze on large sites. Every number comes from scripts in the repository, so you can run them
again.

## The Markdown build benchmark

In 2022 Zach Leatherman published
[Which generator builds Markdown the fastest?](https://www.zachleat.com/web/build-benchmark/), with its scripts in
[zachleat/bench-framework-markdown](https://github.com/zachleat/bench-framework-markdown). Each generator builds a
blog from 250, 500, 1000, 2000 and 4000 Markdown files, borrowed from Sean C Davis's SSG build performance
comparison. The output folder and the generator's caches are deleted before each run, each size runs 3 times, and the
fastest run is kept.

The rerun here uses the same Markdown files, at commit `fbda1c1` of the benchmark repository, with the current
versions of Hugo, Eleventy, Astro and Jekyll, and with Douze. The original also tested Gatsby, Next.js and Remix, which
are not part of this rerun. Jekyll was not in the original.

Each Markdown file has a `title` in its front matter and three paragraphs of lorem ipsum, about 1 KB. There are no
headings, lists, code blocks or images.

### What each generator builds

Each generator starts from the starter the original article used, at its current version, and the benchmark copies
the Markdown files where that starter keeps its posts.

| Generator | Version | Starter | Pages for N posts | Other outputs |
|---|---|---|---|---|
| Douze | preview, commit `001838c` | A blog written for this benchmark as the equivalent of eleventy-base-blog (below) | N posts, home, archive, about, tags, 404 | Atom feed, sitemap |
| Hugo | 0.168.0 extended | The benchmark repository's quickstart: no theme, one template for posts | N posts | RSS, sitemap (Hugo's built-in templates) |
| Eleventy | 3.1.7 | [eleventy-base-blog](https://github.com/11ty/eleventy-base-blog) v9, sample posts removed | N posts, home, archive, about, tags, 404 | Atom feed with the post contents, sitemap |
| Astro | 7.4.0 | The blog example of the Astro repository, sample posts removed | N posts, home, about, blog index | RSS feed, sitemap, one optimized image |
| Jekyll | 4.4.1 | `jekyll new` with minima 2.5.2, the welcome post removed | N posts, home (lists every post), about, 404 | Atom feed, minima's Sass compiled |

Notes on the setup:

- Astro's blog example requires a `description` and a `pubDate` on every post, which the Markdown files do not have.
  The collection schema gives them defaults; the files are not changed.
- Jekyll only reads posts named `YYYY-MM-DD-title.md`, so the files are copied as `2022-07-29-<name>.md`.
- Hugo writes only the post pages: the quickstart of the benchmark has no template for the home page or the lists.
  This is the setup of the original article, and it gives Hugo less to do than the other blogs.

The Douze blog follows eleventy-base-blog page by page, with the default workflow:

- A base layout with eleventy-base-blog's stylesheet inlined, the site metadata and a navigation menu from a data file.
- A post layout with the title, date, tags, content, and links to the previous and next posts, found with the
  `previous_item` and `next_item` filters.
- A data file in the posts folder that gives every post the `posts` tag and the post layout.
- A home page with the 3 latest posts, an archive of every post, an about page, a tags page, a 404 page, an Atom feed of
  the 10 latest posts and a sitemap.

It leaves out what the Markdown files do not use: image processing, heading anchors and syntax highlighting. Its feed
has the titles and links of the posts, not their contents, and the stylesheets are not bundled per page.

### How it is measured

- Each generator runs in its own Docker image, built from pinned versions. Douze is built from the repository sources
  inside Docker, the same way as the binary that the releases publish.
- The site lives in the container's file system, not in a folder shared with the host.
- Before each run, the script deletes the output folder, the caches and the posts, then copies the N posts in.
- The time is the wall clock of the build command only, measured inside the container. Starting the container,
  cleaning and copying are not timed.
- Each size runs 3 times. The tables show the fastest run, as the original did, and the median.
- Each run counts the HTML pages written. Every run of every generator wrote one page per post plus its other pages
  (for 4000 posts: Douze and Eleventy 4005 pages, Astro and Jekyll 4003, Hugo 4000), and no build failed.

| Generator | Build command | Deleted before each run |
|---|---|---|
| Douze | `douze --no-cache -q` | `_site`, `.douze` |
| Hugo | `hugo --quiet` | `public`, `resources/_gen`, Hugo's cache folder |
| Eleventy | `npx @11ty/eleventy --quiet` | `_site`, `.cache`, `node_modules/.cache` |
| Astro | `npx astro build --silent` | `dist`, `.astro`, `node_modules/.astro`, `node_modules/.vite` |
| Jekyll | `JEKYLL_ENV=production bundle exec jekyll build --quiet` | `_site`, `.jekyll-cache`, `.sass-cache` |

### Environment

| | |
|---|---|
| Machine | Apple M4 Pro, 14 cores, 48 GB, macOS 15.8.1 |
| Docker | Docker Desktop 28.0.4, Linux 6.10.14 arm64 VM with 14 CPUs and 7.7 GB of memory |
| Douze | Commit `001838c`, built from source like a release binary |
| Hugo | 0.168.0 extended, release binary on `debian:trixie-slim` |
| Eleventy, Astro | Node.js 24.22.1 (`node:24.22.1-trixie-slim`) |
| Jekyll | Ruby 3.4.11 (`ruby:3.4.11-slim-trixie`), sass-embedded 1.106.1 |
| Date | 2026-10-27 |

Every container gets all the CPUs and memory of the Docker VM.

## Results

Seconds, lower is better. Fastest of 3 runs.

| Generator | 250 | 500 | 1000 | 2000 | 4000 |
|---|---:|---:|---:|---:|---:|
| Douze | 0.035 | 0.060 | 0.109 | 0.207 | 0.387 |
| Hugo | 0.051 | 0.073 | 0.118 | 0.216 | 0.412 |
| Jekyll | 0.345 | 0.470 | 0.666 | 1.020 | 1.793 |
| Eleventy | 0.635 | 0.894 | 1.468 | 2.639 | 6.363 |
| Astro | 1.123 | 1.364 | 1.613 | 2.346 | 3.295 |

Median of 3 runs:

| Generator | 250 | 500 | 1000 | 2000 | 4000 |
|---|---:|---:|---:|---:|---:|
| Douze | 0.037 | 0.061 | 0.109 | 0.211 | 0.403 |
| Hugo | 0.058 | 0.074 | 0.119 | 0.220 | 0.414 |
| Jekyll | 0.358 | 0.472 | 0.673 | 1.038 | 1.857 |
| Eleventy | 0.657 | 0.912 | 1.501 | 2.680 | 6.420 |
| Astro | 1.137 | 1.422 | 1.648 | 2.388 | 3.302 |

<div class="bench-chart">
<h4>1,000 files</h4>
<div class="bench-row" data-generator="douze"><span class="bench-label">Douze</span><span class="bench-bar" style="--value: 0.068"></span><span class="bench-value">0.109 s</span></div>
<div class="bench-row" data-generator="hugo"><span class="bench-label">Hugo</span><span class="bench-bar" style="--value: 0.073"></span><span class="bench-value">0.118 s</span></div>
<div class="bench-row" data-generator="jekyll"><span class="bench-label">Jekyll</span><span class="bench-bar" style="--value: 0.413"></span><span class="bench-value">0.666 s</span></div>
<div class="bench-row" data-generator="eleventy"><span class="bench-label">Eleventy</span><span class="bench-bar" style="--value: 0.91"></span><span class="bench-value">1.468 s</span></div>
<div class="bench-row" data-generator="astro"><span class="bench-label">Astro</span><span class="bench-bar" style="--value: 1"></span><span class="bench-value">1.613 s</span></div>
<h4>4,000 files</h4>
<div class="bench-row" data-generator="douze"><span class="bench-label">Douze</span><span class="bench-bar" style="--value: 0.061"></span><span class="bench-value">0.387 s</span></div>
<div class="bench-row" data-generator="hugo"><span class="bench-label">Hugo</span><span class="bench-bar" style="--value: 0.065"></span><span class="bench-value">0.412 s</span></div>
<div class="bench-row" data-generator="jekyll"><span class="bench-label">Jekyll</span><span class="bench-bar" style="--value: 0.282"></span><span class="bench-value">1.793 s</span></div>
<div class="bench-row" data-generator="eleventy"><span class="bench-label">Eleventy</span><span class="bench-bar" style="--value: 1"></span><span class="bench-value">6.363 s</span></div>
<div class="bench-row" data-generator="astro"><span class="bench-label">Astro</span><span class="bench-bar" style="--value: 0.518"></span><span class="bench-value">3.295 s</span></div>
</div>

Douze is the fastest at every size, narrowly ahead of Hugo from 1000 files up. At 4000 files it builds the blog in
0.387 s, against 0.412 s for Hugo, 1.793 s for Jekyll, 3.295 s for Astro and 6.363 s for Eleventy. Hugo writes only the
post pages, so it does less work for the same number of files.

### Rebuilds from the disk cache

The runs above use `--no-cache`, so that every Douze build is a clean build like the others. A plain `douze` build also
writes its disk cache (`.douze/`), and the next build reuses every result whose inputs did not change. Rebuilding the
same site with that cache and no change, fastest of 3 runs:

| | 250 | 500 | 1000 | 2000 | 4000 |
|---|---:|---:|---:|---:|---:|
| Douze, rebuild with the cache | 0.018 | 0.025 | 0.039 | 0.069 | 0.125 |

A rebuild of 4000 posts takes about a third of the clean build. The other generators were not measured this way: their
caches were deleted before each run, as the benchmark requires.

## Caveats

- The sites do not do the same work. Hugo writes only the post pages. Eleventy's base blog runs HTML transforms (image
  optimization, heading ids) over every page and puts the post contents in its feed. Astro optimizes an image and
  bundles its assets. Jekyll compiles minima's Sass. The Douze blog is close to eleventy-base-blog, but it is written by
  the Douze project, and it leaves out what the list above says.
- Startup is part of the time. Douze and Hugo are native executables that start in milliseconds. Eleventy and Astro
  start Node.js and `npx`, and Jekyll starts Ruby and Bundler, which accounts for much of their time at 250 files.
- Douze and Hugo use every core of the VM. Eleventy and Jekyll do most of their work on one thread.
- Docker Desktop on macOS runs a Linux VM. The sites are in the container's file system to keep the macOS file sharing
  out of the measure, but the numbers are those of that VM, not of macOS or of a Linux machine.
- Douze has no stable release yet: this is a build of the repository at commit `001838c`.
- 3 runs per size is few, and the Markdown files are small and plain. On a real site, templates, images and code
  highlighting take most of the build time.

## Run it again

```sh
bench/ssg-comparison/run.sh                    # every generator, 250 to 4000 files, 3 runs
bench/ssg-comparison/run.sh douze hugo         # some generators
SIZES="250 1000" RUNS=1 bench/ssg-comparison/run.sh
```

It needs Docker and git. It builds the images, runs every size, then writes `results.json` (every run) and
`results.md` next to the script. The versions are pinned in `versions.env`, and
[`bench/ssg-comparison/README.md`](https://github.com/sebastienros/douze/blob/main/bench/ssg-comparison/README.md)
describes each scenario.

## Docs-heavy

The Markdown benchmark has almost no work per page. The docs-heavy scenario gives each generator documentation pages
that need real work: highlighting code at build time and giving headings ids. It is part of this repository, not of a
public benchmark.

A seeded script writes N pages of about 8 KB (6 to 10 KB for 90% of them), each with a `title` and a `description` in
front matter, 3 or 4 h2 sections with h3 subsections, lists, a table, inline code and 3 to 6 code fences in js, ts,
csharp, yaml, bash, json and html. Every generator highlights the fences and adds heading ids with its usual tooling,
and renders the pages in one layout with a sidebar of the 50 first pages, read from the same data file.

| Generator | Highlighting | Heading ids | Table of contents |
|---|---|---|---|
| Douze | `highlight` step, github-dark | `headings` step | Yes, nested |
| Hugo | Chroma, github-dark, inline styles | Built in | Yes, nested (`.TableOfContents`) |
| Eleventy | `@11ty/eleventy-plugin-syntaxhighlight` 5.0.2 (Prism) | `markdown-it-anchor` 10.0.0 | No |
| Astro | Shiki, github-dark (its default) | Built in | Yes, a flat list |
| Jekyll | Rouge 4.7.0 | kramdown `auto_ids` | No |

The measure is the same as above: wall clock of the build command in the container, output and caches deleted before
each run, 3 runs, on the same machine and Docker VM (14 CPUs, 7.7 GB). A run counts only if every page is written, has
highlighted tokens and has h2 ids. Builds were capped at 15 minutes; none reached it.

Seconds, lower is better. Fastest of 3 runs:

| Generator | 1,000 | 4,000 | 16,000 |
|---|---:|---:|---:|
| Douze | 0.284 | 0.963 | 4.138 |
| Hugo | 0.476 | 1.639 | 6.105 |
| Eleventy | 1.565 | 5.947 | 38.122 |
| Astro | 4.844 | 16.244 | failed |
| Jekyll | 5.192 | 20.051 | 81.746 |

Median of 3 runs:

| Generator | 1,000 | 4,000 | 16,000 |
|---|---:|---:|---:|
| Douze | 0.287 | 0.973 | 4.632 |
| Hugo | 0.477 | 1.706 | 6.265 |
| Eleventy | 1.594 | 5.986 | 39.139 |
| Astro | 5.048 | 16.474 | failed |
| Jekyll | 5.199 | 20.081 | 82.701 |

<div class="bench-chart">
<h4>1,000 pages</h4>
<div class="bench-row" data-generator="douze"><span class="bench-label">Douze</span><span class="bench-bar" style="--value: 0.055"></span><span class="bench-value">0.284 s</span></div>
<div class="bench-row" data-generator="hugo"><span class="bench-label">Hugo</span><span class="bench-bar" style="--value: 0.092"></span><span class="bench-value">0.476 s</span></div>
<div class="bench-row" data-generator="eleventy"><span class="bench-label">Eleventy</span><span class="bench-bar" style="--value: 0.301"></span><span class="bench-value">1.565 s</span></div>
<div class="bench-row" data-generator="astro"><span class="bench-label">Astro</span><span class="bench-bar" style="--value: 0.933"></span><span class="bench-value">4.844 s</span></div>
<div class="bench-row" data-generator="jekyll"><span class="bench-label">Jekyll</span><span class="bench-bar" style="--value: 1"></span><span class="bench-value">5.192 s</span></div>
<h4>4,000 pages</h4>
<div class="bench-row" data-generator="douze"><span class="bench-label">Douze</span><span class="bench-bar" style="--value: 0.048"></span><span class="bench-value">0.963 s</span></div>
<div class="bench-row" data-generator="hugo"><span class="bench-label">Hugo</span><span class="bench-bar" style="--value: 0.082"></span><span class="bench-value">1.639 s</span></div>
<div class="bench-row" data-generator="eleventy"><span class="bench-label">Eleventy</span><span class="bench-bar" style="--value: 0.297"></span><span class="bench-value">5.947 s</span></div>
<div class="bench-row" data-generator="astro"><span class="bench-label">Astro</span><span class="bench-bar" style="--value: 0.81"></span><span class="bench-value">16.244 s</span></div>
<div class="bench-row" data-generator="jekyll"><span class="bench-label">Jekyll</span><span class="bench-bar" style="--value: 1"></span><span class="bench-value">20.051 s</span></div>
<h4>16,000 pages</h4>
<div class="bench-row" data-generator="douze"><span class="bench-label">Douze</span><span class="bench-bar" style="--value: 0.051"></span><span class="bench-value">4.138 s</span></div>
<div class="bench-row" data-generator="hugo"><span class="bench-label">Hugo</span><span class="bench-bar" style="--value: 0.075"></span><span class="bench-value">6.105 s</span></div>
<div class="bench-row" data-generator="eleventy"><span class="bench-label">Eleventy</span><span class="bench-bar" style="--value: 0.466"></span><span class="bench-value">38.122 s</span></div>
<div class="bench-row" data-generator="astro"><span class="bench-label">Astro</span><span class="bench-bar" style="--value: 0"></span><span class="bench-value">failed</span></div>
<div class="bench-row" data-generator="jekyll"><span class="bench-label">Jekyll</span><span class="bench-bar" style="--value: 1"></span><span class="bench-value">81.746 s</span></div>
</div>

Astro fails at 16,000 pages, on every run, after about 52 s: writing its content layer data store throws
`Invalid string length` (the store is serialized as one string, larger than V8 allows).

Douze takes 60 to 70% of the time of Hugo, the next fastest: 16,000 pages in 4.1 s against 6.1 s. Eleventy takes 38 s,
Jekyll 82 s, and Astro cannot build that many. Douze and Hugo use about 10 of the 14 cores,
the others one or two.

### CPU utilization

User plus system CPU time of the build, divided by its wall clock time, mean of the runs. A value of 1 is one core
busy; the VM has 14.

| Generator | 1,000 | 4,000 | 16,000 |
|---|---:|---:|---:|
| Douze | 9.7 | 10.5 | 10.7 |
| Hugo | 7.7 | 8.5 | 9.7 |
| Jekyll | 1.0 | 1.0 | 1.0 |
| Eleventy | 1.3 | 1.3 | 2.0 |
| Astro | 1.3 | 1.2 | 1.1 |

### Rebuilds with nothing changed

Fastest of 3 runs, after a first build that writes the cache:

| Generator | 1,000 | 4,000 | 16,000 |
|---|---:|---:|---:|
| Douze, disk cache | 0.034 | 0.082 | 0.360 |
| Jekyll, `--incremental` | 0.264 | 0.557 | 1.744 |
| Astro, content layer cache | 1.826 | 5.241 | failed |

Hugo has no persistent cache for pages, and Eleventy's `--incremental` only applies to `--watch` and `--serve`, so they
were not measured this way.

### Caveats

- The features are close but not equal. Eleventy and Jekyll render no table of contents, Astro's is a flat list. Prism
  and Rouge write classes and no stylesheet is shipped for them; Douze, Hugo and Astro write the colors into the HTML,
  so their pages are larger.
- The highlighters are different: Douze and Shiki use TextMate grammars, which are slower and more precise than Chroma,
  Prism or Rouge.
- Each site was written for this scenario by the Douze project, from each generator's documentation.
- The same as above: Docker Desktop on macOS, a build of Douze at commit `001838c`, and 3 runs per size.

`bench/ssg-comparison/run-docs-heavy.sh` runs it again, and `results-docs-heavy.md` next to it has every number, the
page counts and the versions.

## Internal benchmarks

`bench/Douze.Bench` measures the native `douze` binary on large generated sites, directly on macOS (Apple M4 Pro,
14 cores, 48 GB), and
[`bench/README.md`](https://github.com/sebastienros/douze/blob/main/bench/README.md) records the results of each
change. The latest measurements, from 2026-10-22, median of 3 runs:

| Site | Build | Wall |
|---|---|---:|
| Synthetic, 32,000 pages of about 4 KB (2 layouts, includes, a navigation of 50 items, 5 data files, a paginated page, 200 assets) | Clean, no cache | 4.6 s |
| Same site, one page edited | From the disk cache | 1.0 s |
| API reference: 19,712 pages rendered from 78 MB of JSON data, each with a sidebar of 342 packages | Clean, no cache | 2.0 s |
