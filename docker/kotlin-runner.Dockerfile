# SEK-01 code-run image for the 'kotlin' language (see DockerCodeRunner.cs).
# Submissions run with --network none, so kotlinc needs to be pre-installed here rather
# than fetched at run time. Build once: `docker build -t campus-kotlin-runner:local -f
# docker/kotlin-runner.Dockerfile .` — see campus-backend/CLAUDE.md for the full setup.
FROM eclipse-temurin:21-jdk

# Installs kotlinc via the official JetBrains release archive (SDKMAN's own installer
# needs network access at *build* time only, which is fine — the resulting image still
# has --network none applied at *run* time by DockerCodeRunner, same as every other
# language). Pinned to a specific release rather than "latest" so this image is
# reproducible.
ARG KOTLIN_VERSION=1.9.24
RUN apt-get update && apt-get install -y --no-install-recommends unzip curl && \
    curl -fsSL -o /tmp/kotlin.zip "https://github.com/JetBrains/kotlin/releases/download/v${KOTLIN_VERSION}/kotlin-compiler-${KOTLIN_VERSION}.zip" && \
    unzip -q /tmp/kotlin.zip -d /opt && \
    rm /tmp/kotlin.zip && \
    apt-get purge -y unzip curl && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*
ENV PATH="/opt/kotlinc/bin:${PATH}"
