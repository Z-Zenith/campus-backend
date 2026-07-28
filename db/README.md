# SQL Server setup

Local SQL Server container seeded from [`docs/campus-platform-db-api-schema.md`](../docs/campus-platform-db-api-schema.md).

Migrated off PostgreSQL — see `MIGRATIONS.md` and this repo's git history for the rationale
and translation notes (enum handling, array-column replacement, etc.). `campus-platform`'s
Judge0-based code execution stack (a separate, unrelated Postgres instance) is gone too — see
that repo's docker-compose.yml history; it stopped being used once `Services/DockerCodeRunner.cs`
replaced Judge0 (commit `91ab918`), so removing its leftover Postgres container as part of this
migration was pure cleanup, not new scope.

## Start the container

`MSSQL_SA_PASSWORD` is required — `docker compose up` fails fast without it (same pattern
`POSTGRES_PASSWORD` used to follow). Set it in a local `.env` (copy `.env.example`) or export
it in your shell before starting:

```bash
docker compose up -d mssql
```

Unlike the official `postgres` image, `mcr.microsoft.com/mssql/server` has no
`docker-entrypoint-initdb.d` auto-init mechanism — a one-shot `mssql-init` sidecar service
applies everything in `db/init/` (alphabetically, same ordering convention as before) via
`sqlcmd` once the `mssql` service reports healthy. The database itself
(`00_create_database.sql`), the schema (`01_schema.sql`), default roles/permissions
(`02_seed_roles_and_permissions.sql`), and the least-privilege `campus_app` login (#137,
`03_create_app_role.sql`) that the containerized backend-api service connects as (via
`docker-compose.yml`'s `ConnectionStrings__Campus` override) all run through that sidecar —
see its definition in `campus-platform/docker-compose.yml`. Local `dotnet run` outside Docker
Compose still uses the `campus` SQL login (this file's connection string below and
`appsettings.json`'s dev placeholder are unaffected).

Connection (matches the credentials in `docker-compose.yml`, assuming the documented
`.env.example` dev value):

| Setting      | Value        |
|--------------|--------------|
| host         | `localhost`  |
| port         | `1433`       |
| database     | `campus`     |
| user         | `campus`     |
| password     | `campus_dev` |

Connection string for the .NET backend:

```
Server=localhost,1433;Database=campus;User Id=campus;Password=campus_dev;TrustServerCertificate=true
```

## Useful commands

```bash
# Tail logs
docker compose logs -f mssql

# Open a sqlcmd shell
docker compose exec mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U campus -P campus_dev -C -d campus

# Tear down (keeps the named volume)
docker compose down

# Nuke the volume too — next `up` will re-seed
docker compose down -v
```

## Resetting the schema without losing the volume

The init scripts only apply against a fresh database (each file is itself idempotent via
`IF NOT EXISTS` guards, but `mssql-init` only runs once per `docker compose up`). To re-apply
after editing `db/init/`, drop the volume and re-run the init sidecar:

```bash
docker compose down -v && docker compose up -d mssql mssql-init
```

## Known verification gap

These init scripts were written and parsed for T-SQL syntax validity but **not run against a
live SQL Server** — no Docker daemon was available in the session that produced this
migration. Whoever next has Docker available should bring the stack up from a clean volume and
confirm: the database/schema/seed data/app login all apply cleanly, `dotnet ef dbcontext
scaffold` against the resulting `campus` database produces `Data/Entities/*.cs` matching what's
currently hand-edited in this branch (re-scaffold and diff — see `MIGRATIONS.md`), and
backend-api's own test suite plus a manual smoke test of a few endpoints pass end-to-end.
