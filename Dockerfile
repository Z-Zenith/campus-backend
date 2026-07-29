FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY BackendApi.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# SEK-01 code execution (DockerCodeRunner.cs) and its integrated terminal (TerminalSessionService.cs)
# shell out to the `docker` CLI for every submission. When backend-api runs as its own container
# (this Dockerfile) rather than the bare `dotnet run` dev setup, it needs that CLI plus the host's
# Docker socket bind-mounted in (see docker-compose.yml's backend-api service) — Docker-outside-
# of-Docker. Copied from the upstream `-cli` variant rather than apt-get installing the Debian
# `docker.io` package, which drags in dockerd/containerd this process never runs.
COPY --from=docker:27-cli /usr/local/bin/docker /usr/local/bin/docker
# The docker CLI defaults its config dir to $HOME/.docker — $HOME is still /root (the image's
# built-in default, untouched by docker-entrypoint.sh's setpriv, which changes uid/gid but not
# environment variables) even after dropping to non-root $APP_UID, so every `docker run`/`exec`
# would otherwise fail to write there and print a "permission denied" warning on its stderr —
# noise a student would see mixed into their own program's stderr on every single Run. /tmp is
# world-writable (see the base image), so this needs no extra chown.
ENV DOCKER_CONFIG=/tmp/.docker
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# #131: Data Protection key ring (used to encrypt TOTP secrets) is persisted here via a
# named volume (see docker-compose.yml) so keys survive container restarts/redeploys.
# Must be owned by the non-root $APP_UID the app runs as, or PersistKeysToFileSystem
# fails to write on first startup.
RUN mkdir -p /keys && chown $APP_UID:$APP_UID /keys
# Starts as root (no USER here, unlike before) so docker-entrypoint.sh can read the bind-mounted
# Docker socket's GID — only known at container start, not at image build time — before dropping
# to $APP_UID via setpriv. See that script's comment; the app process itself still never runs as
# root.
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
