# Docs-heavy scenario, Hugo (extended), the release binary from GitHub. Chroma and heading ids are built in.
ARG DEBIAN_IMAGE
FROM ${DEBIAN_IMAGE}
ARG HUGO_VERSION
ARG TARGETARCH
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && curl -fsSL "https://github.com/gohugoio/hugo/releases/download/v${HUGO_VERSION}/hugo_extended_${HUGO_VERSION}_linux-${TARGETARCH}.tar.gz" \
    | tar -xz -C /usr/local/bin hugo \
 && apt-get purge -y curl && apt-get autoremove -y && rm -rf /var/lib/apt/lists/* \
 && hugo version
COPY --from=samples . /samples
COPY --from=scenario . /site
COPY --from=bench bench-docs-heavy.sh /bench.sh
WORKDIR /site
ENTRYPOINT ["bash", "/bench.sh", "hugo"]
