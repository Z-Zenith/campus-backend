#!/bin/sh
# One-command bootstrap for SEK-01 code execution (DockerCodeRunner.cs) and the Coding
# app's integrated terminal (TerminalSessionService.cs): pulls every base image
# DockerCodeRunner.Languages references and builds the three custom images that need a
# runtime pre-installed (submissions run with --network none, so nothing can be fetched
# at container-run time). See campus-backend/CLAUDE.md's "SEK-01 code execution" section
# for why each of these is needed.
#
# Usage: ./docker/setup-code-images.sh (from anywhere — resolves the repo root itself).
set -e

cd "$(dirname "$0")/.."

echo "Pulling base language images..."
for image in \
  python:3.12-slim \
  gcc:13 \
  eclipse-temurin:21-jdk \
  node:20-slim \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  keinos/sqlite3:latest \
  golang:1.22-alpine \
  rust:1-slim \
  ruby:3-slim \
  php:8-cli \
  bash:5; do
  echo "  docker pull $image"
  docker pull "$image"
done

echo "Building custom images..."
docker build -t campus-ts-runner:local -f docker/ts-runner.Dockerfile .
docker build -t campus-kotlin-runner:local -f docker/kotlin-runner.Dockerfile .
docker build -t campus-dev-terminal:local -f docker/dev-terminal.Dockerfile .

echo "All SEK-01 code-execution images are ready."
