# PostgreSQL setup

Local Postgres container seeded from [`docs/campus-platform-db-api-schema.md`](../docs/campus-platform-db-api-schema.md).

## Start the container

`POSTGRES_PASSWORD` is required — `docker compose up` fails fast without it
(#137, no default committed to `docker-compose.yml` anymore). Set it in a
local `.env` (copy `.env.example`; the documented dev value is `campus_dev`)
or export it in your shell before starting:

```bash
docker compose up -d postgres
```

The first run executes everything in `db/init/` alphabetically on a fresh
data volume. The schema lives in `01_schema.sql`; default roles/permissions
in `02_seed_roles_and_permissions.sql`; `03_create_app_role.sh` provisions a
least-privilege `campus_app` role (#137) that the containerized backend-api
service now connects as via `docker-compose.yml`'s `ConnectionStrings__Campus`
override — see that script's header comment. Local `dotnet run` outside
Docker Compose still uses `campus` (this file's connection string above and
`appsettings.json`'s dev placeholder are unaffected).

Connection (matches the credentials in `docker-compose.yml`, assuming the
documented `.env.example` dev value):

| Setting      | Value        |
|--------------|--------------|
| host         | `localhost`  |
| port         | `5432`       |
| database     | `campus`     |
| user         | `campus`     |
| password     | `campus_dev` |

Connection string for the .NET backend:

```
Host=localhost;Port=5432;Database=campus;Username=campus;Password=campus_dev
```

## Useful commands

```bash
# Tail logs
docker compose logs -f postgres

# Open a psql shell
docker compose exec postgres psql -U campus -d campus

# Tear down (keeps the named volume)
docker compose down

# Nuke the volume too — next `up` will re-seed
docker compose down -v
```

## Resetting the schema without losing the volume

The init scripts only run on a fresh data directory. To re-apply after editing
`db/init/`, drop the volume:

```bash
docker compose down -v && docker compose up -d postgres
```

This still works for a genuine fresh start, but as of the `InitialBaseline` EF Core migration
(see `MIGRATIONS.md`) it's no longer the only option for picking up a schema change on an
**existing** local dev database — see below.

## Applying a schema change without wiping your data

For a local `dotnet run` (connects as `campus`, the only role with DDL rights — the
containerized `backend-api` connects as `campus_app`, which is DML-only and can't run
migrations, see `db/init/03_create_app_role.sh`):

```bash
dotnet ef migrations add <Name>      # after changing Data/Entities/*.cs (re-scaffolded, not hand-edited)
dotnet ef database update            # applies just the new migration(s) to your existing DB
```

**One-time step if your local database predates this migration adoption:** before the first
`dotnet ef database update` against it, insert the baseline row by hand so EF Core doesn't try
to re-create tables that already exist:

```sql
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260730223702_InitialBaseline', '10.0.9')
ON CONFLICT ("MigrationId") DO NOTHING;
```

(A fresh `docker compose up -d postgres` doesn't need this — `db/init/04_seed_ef_migrations_history.sql`
already inserts it as part of the normal init sequence.)
