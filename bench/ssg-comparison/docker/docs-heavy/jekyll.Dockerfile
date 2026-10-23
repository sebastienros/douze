# Docs-heavy scenario, Jekyll with kramdown (GFM input, auto ids) and Rouge, its defaults; no theme.
ARG RUBY_IMAGE
FROM ${RUBY_IMAGE}
ARG JEKYLL_VERSION
ENV JEKYLL_VERSION=${JEKYLL_VERSION} JEKYLL_ENV=production
RUN apt-get update && apt-get install -y --no-install-recommends build-essential && rm -rf /var/lib/apt/lists/*
COPY --from=scenario . /site
WORKDIR /site
RUN bundle install
COPY --from=samples . /samples
COPY --from=bench bench-docs-heavy.sh /bench.sh
ENTRYPOINT ["bash", "/bench.sh", "jekyll"]
