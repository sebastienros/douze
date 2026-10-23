# Docs-heavy scenario, Eleventy with @11ty/eleventy-plugin-syntaxhighlight (Prism) and markdown-it-anchor.
ARG NODE_IMAGE
FROM ${NODE_IMAGE}
ARG ELEVENTY_VERSION
ARG ELEVENTY_SYNTAXHIGHLIGHT_VERSION
ARG MARKDOWN_IT_ANCHOR_VERSION
COPY --from=scenario . /site
WORKDIR /site
RUN npm init -y > /dev/null && npm pkg set type=module \
 && npm install --no-audit --no-fund --save-exact "@11ty/eleventy@${ELEVENTY_VERSION}" \
      "@11ty/eleventy-plugin-syntaxhighlight@${ELEVENTY_SYNTAXHIGHLIGHT_VERSION}" "markdown-it-anchor@${MARKDOWN_IT_ANCHOR_VERSION}"
COPY --from=samples . /samples
COPY --from=bench bench-docs-heavy.sh /bench.sh
ENTRYPOINT ["bash", "/bench.sh", "eleventy"]
