# SEK-01 code-run image for the 'typescript' language (see DockerCodeRunner.cs).
# Submissions run with --network none, so typescript needs to be baked in here rather
# than npm-installed at run time. Build once: `docker build -t campus-ts-runner:local
# -f docker/ts-runner.Dockerfile .` — see campus-backend/CLAUDE.md for the full setup.
FROM node:20-slim
RUN npm install -g typescript
