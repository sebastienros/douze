# Astro, with the blog example of the Astro repository at the release tag (what create-astro --template blog copies;
# the original article used Astro's blog example too). Its sample posts are removed: the benchmark copies the Markdown
# samples into src/content/blog/. The samples only have a title, so the collection schema gets defaults for the two
# other fields it requires (description, pubDate); everything else is the example as it is.
ARG NODE_IMAGE
FROM ${NODE_IMAGE}
ARG ASTRO_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates git && rm -rf /var/lib/apt/lists/*
RUN git clone -q --depth 1 --branch "astro@${ASTRO_VERSION}" --filter=blob:none --sparse https://github.com/withastro/astro.git /tmp/astro \
 && cd /tmp/astro && git sparse-checkout set examples/blog \
 && cp -r examples/blog /site && rm -rf /tmp/astro \
 && cd /site \
 && npm install --no-audit --no-fund \
 && npm install --no-audit --no-fund --save-exact "astro@${ASTRO_VERSION}" \
 && rm -rf src/content/blog/* \
 && sed -i -e "s/description: z.string(),/description: z.string().default(''),/" \
           -e "s/pubDate: z.coerce.date(),/pubDate: z.coerce.date().default(new Date('2022-07-29')),/" src/content.config.ts \
 && grep -q "default(new Date" src/content.config.ts \
 && npx astro telemetry disable
COPY --from=samples . /samples
COPY --from=bench bench.sh /bench.sh
WORKDIR /site
ENTRYPOINT ["bash", "/bench.sh", "astro"]
