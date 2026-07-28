-- Creates the `campus` database itself.
--
-- The official postgres image auto-creates a database named after POSTGRES_DB before running
-- anything in docker-entrypoint-initdb.d/ — mcr.microsoft.com/mssql/server has no equivalent
-- (it only ever provisions `master` and the other system databases), so this step, implicit
-- before under Postgres, is now an explicit first file here. Runs against the server's default
-- database (master for the sa login) — every later init file starts with `USE campus;` so it
-- doesn't matter which database the sqlcmd invocation itself was pointed at.

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'campus')
BEGIN
    CREATE DATABASE campus;
END
GO
