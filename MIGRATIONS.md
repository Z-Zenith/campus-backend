# Migration policy

Today this project is database-first, not code-first: `db/init/*.sql` is hand
-written and is the schema's single source of truth, applied by the `mssql-init`
sidecar against a fresh `mssql` container on first boot (see `db/README.md` —
migrated off PostgreSQL, whose official image could apply these automatically;
SQL Server has no equivalent, hence the sidecar). `Data/Entities/*.cs`
is generated from that schema via `dotnet-ef dbcontext scaffold` and must never
be hand-edited to a shape the SQL doesn't already have — re-scaffold instead.

When EF Core migrations are introduced (not yet — this repo has none today),
this is where they will live, with this policy:

- Migrations are added under `services/backend-api/Migrations/`.
- Every migration ships both `Up` and `Down`.
- The corresponding schema change also lands in `db/init/` (a new numbered
  file, or an edit to an existing one for pre-release schema, per the existing
  numbering convention) so a fresh container boot and a migration-applied
  existing database converge on the same schema.
- `Data/Entities/*.cs` is re-scaffolded after the migration lands, not hand-edited.
