# syntax = docker/dockerfile:1

# Campfire (.NET) — a single-machine image like the Rails one: the app, background jobs and
# cable server in one process, behind Thruster for TLS (Let's Encrypt), HTTP/2 and compression.

ARG DOTNET_VERSION=10.0
ARG THRUSTER_VERSION=0.1.26

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Campfire.Web/Campfire.Web.csproj src/Campfire.Web/
RUN dotnet restore src/Campfire.Web/Campfire.Web.csproj
COPY src/ src/
RUN dotnet publish src/Campfire.Web/Campfire.Web.csproj -c Release --no-restore -o /app


# Thruster ships as a static binary inside its platform gem; no Ruby needed to unpack it.
FROM debian:bookworm-slim AS thruster
ARG THRUSTER_VERSION
ARG TARGETARCH
RUN apt-get update -qq && apt-get install --no-install-recommends -y curl ca-certificates && \
    case "${TARGETARCH:-amd64}" in arm64) platform=aarch64-linux ;; *) platform=x86_64-linux ;; esac && \
    curl -fsSL "https://rubygems.org/downloads/thruster-${THRUSTER_VERSION}-${platform}.gem" \
      | tar -xO data.tar.gz | tar -xzO "exe/${platform}/thrust" > /usr/local/bin/thrust && \
    chmod +x /usr/local/bin/thrust


FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}

# ffmpeg gives video attachments their poster frames; libvips comes with NetVips.Native.
RUN apt-get update -qq && \
    apt-get install --no-install-recommends -y ffmpeg && \
    rm -rf /var/lib/apt/lists /var/cache/apt/archives

# Run as uid/gid 1000 like the Rails image, so existing storage volumes keep their ownership.
RUN (userdel --remove ubuntu 2>/dev/null || true) && \
    groupadd --system --gid 1000 rails && \
    useradd rails --uid 1000 --gid 1000 --create-home --shell /bin/bash && \
    mkdir -p /rails/storage && chown -R 1000:1000 /rails

WORKDIR /rails
COPY --from=build --chown=1000:1000 /app ./
COPY --from=thruster /usr/local/bin/thrust /usr/local/bin/thrust
COPY hooks /hooks
RUN chmod 755 /hooks/*

ARG OCI_DESCRIPTION
LABEL org.opencontainers.image.description="${OCI_DESCRIPTION}"
ARG OCI_SOURCE
LABEL org.opencontainers.image.source="${OCI_SOURCE}"
LABEL org.opencontainers.image.licenses="MIT"

ARG APP_VERSION
ENV APP_VERSION=$APP_VERSION
ARG GIT_REVISION
ENV GIT_REVISION=$GIT_REVISION

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://127.0.0.1:3000 \
    STORAGE_PATH=/rails/storage \
    TARGET_PORT=3000 \
    HTTP_IDLE_TIMEOUT=60 \
    HTTP_READ_TIMEOUT=300 \
    HTTP_WRITE_TIMEOUT=300 \
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1

USER 1000:1000
EXPOSE 80 443

CMD ["thrust", "dotnet", "Campfire.Web.dll"]
