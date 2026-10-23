# Jekyll, with the blog that `jekyll new` creates (minima theme, jekyll-feed, jekyll-seo-tag). Its welcome post is
# removed: the benchmark copies the Markdown samples into _posts/, with a date prefix since Jekyll requires one.
ARG RUBY_IMAGE
FROM ${RUBY_IMAGE}
ARG JEKYLL_VERSION
ARG MINIMA_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends build-essential && rm -rf /var/lib/apt/lists/*
RUN gem install --no-document jekyll -v "${JEKYLL_VERSION}" \
 && jekyll new /site \
 && cd /site \
 && sed -i -e "s/^gem \"minima\".*/gem \"minima\", \"${MINIMA_VERSION}\"/" -e "s/^gem \"jekyll\".*/gem \"jekyll\", \"${JEKYLL_VERSION}\"/" Gemfile \
 && bundle install \
 && rm -f _posts/*
COPY --from=samples . /samples
COPY --from=bench bench.sh /bench.sh
WORKDIR /site
ENV JEKYLL_ENV=production
ENTRYPOINT ["bash", "/bench.sh", "jekyll"]
