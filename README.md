# Backend API

ASP.NET Core / .NET 10 backend, database-first against Microsoft SQL Server via EF Core
(`Data/Entities/*.cs` are scaffolded with `dotnet-ef dbcontext scaffold`, not
hand-authored). See `db/README.md` for local SQL Server setup and connection
details, and `docs/campus-platform-db-api-schema.md` for the schema itself.

## `db/` and `backend-api` move together

This service and `db/` are one unit and must be branched, reviewed, and moved
together — `db/init/*.sql` is the schema source of truth, and
`Data/Entities/*.cs` is only ever regenerated from it, never hand-edited to a
different shape. A PR that changes one without the other should be treated as
incomplete. See `MIGRATIONS.md` for how schema changes are expected to be
made once EF migrations exist.

## Build & run

```bash
docker compose up -d mssql mssql-init   # or the full stack: docker compose up -d
dotnet build
dotnet run
```
