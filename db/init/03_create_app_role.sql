-- Least-privilege application login for backend-api (#137 part 3).
--
-- The sa login used to run 00/01/02 above is a full sysadmin — this script provisions a
-- non-admin `campus_app` SQL login scoped to row-level CRUD only, and
-- campus-platform/docker-compose.yml's backend-api service connects as `campus_app`
-- (ConnectionStrings__Campus), not `sa`. `sa` is still used for 00_create_database.sql/
-- 01_schema.sql/02_seed_roles_and_permissions.sql and for local `dotnet run` without docker
-- compose (appsettings.json's dev placeholder) — only the containerized app connection was
-- cut over, per CLAUDE.md's "ask before changing the DB schema" rule (explicitly approved)
-- and reviewed here.
--
-- Ported from PostgreSQL's db/init/03_create_app_role.sh: that script ran psql with GRANT ...
-- ON ALL TABLES IN SCHEMA public plus ALTER DEFAULT PRIVILEGES so future tables stayed covered
-- automatically. SQL Server's schema-level GRANT (below) already covers both present AND
-- future objects in the schema on its own — no default-privileges-style follow-up needed.
--
-- Verified: this file is syntactically valid T-SQL (parsed, not executed — no live SQL Server
-- available in this session; see PR description). NOT verified end-to-end against a live SQL
-- Server. Whoever next has Docker available should bring up `mssql` + `backend-api` and
-- confirm login/CRUD-through-the-app works before treating this as fully verified.
--
-- Scope verification: GRANT below targets SCHEMA::dbo rather than an enumerated table list,
-- because that scope was cross-checked against Data/AppDbContext.cs — every table created in
-- 01_schema.sql has a corresponding DbSet (or, for role_default_permissions/
-- event_restricted_years/event_restricted_departments, an EF-managed join table) on
-- AppDbContext, and there are no tables AppDbContext maps to that 01_schema.sql doesn't
-- create. If a future migration adds a table backend-api does NOT need, revisit this file to
-- exclude it explicitly rather than relying on the schema-level grant to keep covering it.

SET XACT_ABORT ON;
GO

-- CREATE LOGIN is a server-level (master-scoped) operation.
IF NOT EXISTS (SELECT * FROM sys.server_principals WHERE name = 'campus_app')
BEGIN
    -- Dev-only fallback so this script never blocks the init sidecar for contributors who
    -- haven't set MSSQL_APP_PASSWORD yet — see campus-platform/docker-compose.yml. sqlcmd's
    -- $(APP_PASSWORD) scripting variable is passed in via `sqlcmd -v APP_PASSWORD=...` by the
    -- init sidecar; falls back to the same dev-only default if unset.
    CREATE LOGIN campus_app WITH PASSWORD = '$(APP_PASSWORD)', CHECK_POLICY = OFF;
END
ELSE
BEGIN
    ALTER LOGIN campus_app WITH PASSWORD = '$(APP_PASSWORD)';
END
GO

USE campus;
GO

IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = 'campus_app')
BEGIN
    CREATE USER campus_app FOR LOGIN campus_app;
END
GO

-- Row-level access on every table backend-api's AppDbContext maps to today (see the
-- scope-verification note at the top of this file). Schema-level, so it also covers any table
-- added to dbo later without a follow-up grant.
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO campus_app;

-- Explicitly no CREATE/DROP/ALTER on the schema, no role/database administration, no
-- sysadmin/db_owner membership — campus_app can read and write rows in existing tables and
-- nothing else.
GO
