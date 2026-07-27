# SEK-01 image for the Coding app's integrated terminal (see TerminalSessionService.cs).
# One general-purpose sandbox with common CLI tools pre-installed, unlike
# DockerCodeRunner's per-language Run images: the terminal is for exploring/building a
# student's own project (compiling, git, running scripts) rather than per-submission
# judging, so it needs a broadly-useful toolset rather than one runtime pinned exactly.
# Build once: `docker build -t campus-dev-terminal:local -f docker/dev-terminal.Dockerfile .`
FROM debian:12-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    git build-essential python3 nodejs npm curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
