# Docs-heavy scenario, Douze: built from the repository sources (context "src", made by run-docs-heavy.sh with git
# archive HEAD) as the native AOT binary, exactly as docker/douze.Dockerfile builds it.
ARG DOTNET_SDK_IMAGE
ARG DOTNET_RUNTIME_DEPS_IMAGE

FROM ${DOTNET_SDK_IMAGE} AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src
COPY --from=src . .
RUN dotnet publish Douze -c Release -o /douze

FROM ${DOTNET_RUNTIME_DEPS_IMAGE}
COPY --from=build /douze /opt/douze
RUN ln -s /opt/douze/douze /usr/local/bin/douze
COPY --from=samples . /samples
COPY --from=scenario . /site
COPY --from=bench bench-docs-heavy.sh /bench.sh
WORKDIR /site
ENTRYPOINT ["bash", "/bench.sh", "douze"]
