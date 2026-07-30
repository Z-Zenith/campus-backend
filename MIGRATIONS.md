# Migration policy

Today this project is database-first, not code-first: `db/init/*.sql` is hand
-written and is the schema's single source of truth, applied automatically by
the `postgres` container on first boot (see `db/README.md`). `Data/Entities/*.cs`
is generated from that schema via `dotnet-ef dbcontext scaffold` and must never
be hand-edited to a shape the SQL doesn't already have — re-scaffold instead.

When EF Core migrations are introduced (not yet — this repo has none today),
this is where they will live, with this policy:

- Migrations are added under `Migrations/` (this repo's root — the `services/backend-api/`
  path in earlier drafts of this doc predates the monorepo split and no longer applies).
- Every migration ships both `Up` and `Down`.
- The corresponding schema change also lands in `db/init/` (a new numbered
  file, or an edit to an existing one for pre-release schema, per the existing
  numbering convention) so a fresh container boot and a migration-applied
  existing database converge on the same schema.
- `Data/Entities/*.cs` is re-scaffolded after the migration lands, not hand-edited.

## Baseline adoption (`InitialBaseline`)

The first migration (`InitialBaseline`) is a special case: it's added against a schema that
already exists via `db/init/01_schema.sql`, not a schema EF Core is creating from scratch. Its
generated `Up()` is **not** expected to be a faithful reproduction of `01_schema.sql` — EF Core
doesn't scaffold `CHECK` constraints, the enum-creation `DO` blocks, the `pgcrypto` extension,
the deferred `departments_hod_fk` (added via `ALTER TABLE` after `role_bindings` exists), or
triggers (`set_updated_at()` and its attach loop). `01_schema.sql` stays authoritative for a
fresh container boot; migrations are the authoritative mechanism for advancing an **existing**
dev database incrementally, without the `docker compose down -v` volume wipe the old workflow
required. Don't chase an exact `pg_dump` diff between the two paths — expect differences limited
to the categories above.

`db/init/04_seed_ef_migrations_history.sql` creates `__EFMigrationsHistory` and marks
`InitialBaseline` as already applied, so a fresh container boot (which already has the full
schema from `01_schema.sql`) treats `dotnet ef database update` as a no-op. An **existing**
local dev database gets that same history row inserted once, manually, the first time this is
adopted against it — a one-time step, documented in `db/README.md`.

Migrations are applied by a human (or CI) running `dotnet ef database update` as the `campus`
role — never by the app process itself. The containerized `backend-api` connects as
`campus_app`, which has no DDL grants (see `db/init/03_create_app_role.sh`), so there is no
`Database.Migrate()`/`EnsureCreated()` call in `Program.cs` and there should never be one.
