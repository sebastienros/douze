# A clean Linux machine without .NET, with the prerequisites of the installation page only (curl, ca-certificates,
# unzip and the ICU library of the distribution, libicu<version>), and a user of its own. Douze is installed when the
# container runs, by tutorial.sh, with the command of the installation page, so that every run tests the latest
# preview.
ARG BASE=ubuntu:24.04
FROM ${BASE}

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates unzip \
        $(apt-cache pkgnames libicu | grep -E '^libicu[0-9]+$' | sort -V | tail -n 1) \
    && rm -rf /var/lib/apt/lists/*

RUN useradd --create-home --shell /bin/bash reader
USER reader
WORKDIR /home/reader
