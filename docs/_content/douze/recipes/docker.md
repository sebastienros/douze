---
title: Build your site with Docker
description: A multi-stage Dockerfile that installs Douze, builds the site with a cache mount, and serves the output with nginx.
---

A multi-stage Dockerfile builds the site in a Debian image with Douze, then copies the output into an nginx image.
The final image holds only nginx and the static files.

## The Dockerfile

```dockerfile
# Build the site
FROM debian:trixie-slim AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl unzip libicu76 \
 && rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://raw.githubusercontent.com/sebastienros/douze/main/install.sh \
    | sh -s -- --preview --install-dir /opt/douze --no-modify-path
ENV PATH="/opt/douze/bin:$PATH"
WORKDIR /src
COPY . .
RUN --mount=type=cache,target=/src/.douze douze --plain -o /site

# Serve it
FROM nginx:stable-alpine
COPY --from=build /site /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
```

- **The build image** must use glibc: Douze does not run on musl distributions such as Alpine. It needs glibc 2.38 or
  later (Debian 13 "trixie", Ubuntu 24.04) and ICU, the `libicu` package (`libicu76` on Debian 13, `libicu74` on
  Ubuntu 24.04). Without ICU, the install script stops with `the downloaded douze does not run`. libvips, for the
  image steps, is in the Douze package.
- **The install script** needs `curl` (or `wget`) and `unzip`. `--install-dir /opt/douze` installs Douze outside the
  home folder, and `--no-modify-path` skips the shell profile; `ENV PATH` adds its `bin` folder instead. Add
  `--version <version>` to pin a version.
- **The cache mount** keeps `/src/.douze`, the [disk cache](../guide/cache.md), between image builds: a rebuild after an
  edit runs only what the edit affects. `COPY` keeps the modification times of the files, so unchanged pages stay
  cached. The mount needs BuildKit, the default builder of current Docker versions.
- **`--plain`** prints one line per step, which reads better in the build log than the live view.
- **The serving image** can be Alpine, since it only runs nginx.

## The nginx configuration

The default nginx configuration serves `index.html` for folder URLs (`/docs/a/`). This one adds the site's 404 page:

```nginx title="nginx.conf"
server {
    listen 80;
    root /usr/share/nginx/html;
    error_page 404 /404.html;
}
```

The 404 page is a page with its own path:

```html
---
permalink: /404.html
sitemap: false
---
<h1>Not found</h1>
```

## Ignore build folders

A `.dockerignore` keeps the local output, cache and Git history out of the build context:

```
_site/
.douze/
.git/
```

If the site has no `_content` folder, its content is the whole source folder: the `Dockerfile`, `nginx.conf` and
`.dockerignore` would then be copied to the output like any other file. Keep the content in `_content`, or exclude them
with `site.content` or the `inputs` of the workflow.

## Build and run

```sh
docker build -t my-site .
docker run --rm -p 8080:80 my-site
```

The site is on `http://localhost:8080/`.

:::tip[Fonts and tools]
A container usually has no system fonts: the [social-image step](../guide/social-images.md) loads its fonts from files
(`fontfile`). Tools that Douze downloads, such as Pagefind, are installed into `.douze/tools/` on first use, which the
cache mount keeps.
:::

## See also

- [Installation](../guide/install.md)
- [Deploy to GitHub Pages](github-pages.md)
- [Cache and incremental builds](../guide/cache.md)
- [Memory and parallelism](../guide/memory-and-parallelism.md), for builds in memory-limited containers
