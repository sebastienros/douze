# Docs-heavy scenario, Astro with its defaults: Shiki (github-dark) and heading ids.
ARG NODE_IMAGE
FROM ${NODE_IMAGE}
ARG ASTRO_VERSION
COPY --from=scenario . /site
WORKDIR /site
RUN npm install --no-audit --no-fund --save-exact "astro@${ASTRO_VERSION}" && npx astro telemetry disable
COPY --from=samples . /samples
COPY --from=bench bench-docs-heavy.sh /bench.sh
ENTRYPOINT ["bash", "/bench.sh", "astro"]
