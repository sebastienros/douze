# Eleventy, with the eleventy-base-blog starter (what the original article used), installed from its package-lock.json.
# Its five sample posts are removed: the benchmark copies the Markdown samples into content/blog/.
ARG NODE_IMAGE
FROM ${NODE_IMAGE}
ARG ELEVENTY_BASE_BLOG_COMMIT
ARG ELEVENTY_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates git && rm -rf /var/lib/apt/lists/*
RUN git init -q /site && cd /site \
 && git fetch -q --depth 1 https://github.com/11ty/eleventy-base-blog.git "${ELEVENTY_BASE_BLOG_COMMIT}" \
 && git checkout -q FETCH_HEAD && rm -rf .git \
 && npm ci --no-audit --no-fund \
 && test "$(npx @11ty/eleventy --version)" = "${ELEVENTY_VERSION}" \
 && find content/blog -mindepth 1 ! -name blog.11tydata.js -exec rm -rf {} +
COPY --from=samples . /samples
COPY --from=bench bench.sh /bench.sh
WORKDIR /site
ENTRYPOINT ["bash", "/bench.sh", "eleventy"]
