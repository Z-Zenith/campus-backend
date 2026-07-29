#!/bin/sh
set -e

# Docker-outside-of-Docker (SEK-01 code execution + the Coding app's integrated terminal):
# when docker-compose.yml's backend-api service bind-mounts the host's Docker socket in, its
# group ownership reflects whatever GID owns it on THAT host — unknowable at image-build time,
# so $APP_UID's access to it has to be reconciled here, at container start, not baked into the
# image. This script only runs as root long enough to read that GID, then drops to $APP_UID
# (with the socket's group added) via setpriv before exec'ing the app — the app itself never
# runs as root.
#
# If the socket isn't mounted (the ordinary case — see campus-backend/CLAUDE.md, bare
# `dotnet run` is still the default dev setup), this is a no-op privilege drop to $APP_UID,
# identical to the plain `USER $APP_UID` this replaced.
if [ -S /var/run/docker.sock ]; then
    SOCK_GID=$(stat -c '%g' /var/run/docker.sock)
    exec setpriv --reuid="$APP_UID" --regid="$APP_UID" --groups="$SOCK_GID" -- dotnet BackendApi.dll
fi

exec setpriv --reuid="$APP_UID" --regid="$APP_UID" -- dotnet BackendApi.dll
