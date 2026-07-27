# campus-backend

Backend API — the single API and DB layer serving SDA, TWA, AWA, and PRT in the Campus
Digitalization Platform. Split out of the `Omega` monorepo; see
[campus-platform/docs/Campus platform architecture.md](https://github.com/Z-Zenith/campus-platform/blob/main/docs/Campus%20platform%20architecture.md)
and [campus-platform/docs/campus-platform-db-api-schema.md](https://github.com/Z-Zenith/campus-platform/blob/main/docs/campus-platform-db-api-schema.md).

This repo combines two source paths from the original monorepo (`services/backend-api/` and
`db/`), each split independently via `git subtree split` and merged together — see
`README.md`'s "`db/` and `backend-api` move together" section for why they're one repo. Commits
from the original monorepo that didn't touch either path appear as no-op entries, a known cost
of the split, not a bug.

## Tech stack

ASP.NET Core, .NET 10 (LTS). PostgreSQL, EF Core database-first (`db/init/*.sql` is the schema
source of truth; `Data/Entities/*.cs` is scaffolded from it via `dotnet-ef dbcontext scaffold`,
never hand-edited to a different shape). RBAC is enforced directly in `Services/PermissionService.cs`
against Postgres tables — OpenFGA (in `campus-platform`) is reference-only, not wired in.

## Build & test

```bash
docker compose up -d postgres   # from campus-platform, or point at any Postgres 16+
dotnet build
dotnet run
dotnet test BackendApi.Tests
```

See `README.md` for local Postgres setup and `MIGRATIONS.md` for schema-change policy.

## SEK-01 code execution (`DockerCodeRunner`)

Each Coding-app Run shells out to `docker run` for a throwaway per-submission
container — see `Services/DockerCodeRunner.cs`'s doc comment for why this replaced a
Judge0-backed `Judge0Client` (isolate's cgroup v1 requirement vs. this environment's
cgroup v2 host). Requires:
- `docker` on `PATH` and reachable from wherever this process runs (true for a bare
  `dotnet run`; a containerized `backend-api` would need the host's Docker socket
  bind-mounted in — a real security tradeoff, not currently wired up, see the doc
  comment).
- The base images in `DockerCodeRunner.Languages` pulled at least once (`python:3.12-slim`,
  `gcc:13`, `eclipse-temurin:21-jdk`, `node:20-slim`, `mcr.microsoft.com/dotnet/sdk:8.0`,
  `keinos/sqlite3:latest`) — first Run pays the pull cost otherwise.
- `campus-ts-runner:local` built once (typescript needs to be pre-installed since
  submissions run with `--network none`): `docker build -t campus-ts-runner:local -f
  docker/ts-runner.Dockerfile .`

`campus-platform/docker-compose.yml`'s `judge0-*` services are no longer in this
execution path — left in place rather than removed as part of this change, since
nothing here depends on deleting them.

## Code conventions

Match the surrounding code's style and controller/service folder layout. Feature IDs referenced
in this repo span most of the platform's backend-facing features (see the architecture doc's
Section 2/7 for the full list) — this is the shared Backend API container, not scoped to one
app's feature IDs.
