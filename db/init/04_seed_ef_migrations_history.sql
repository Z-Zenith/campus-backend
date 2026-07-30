-- EF Core migration adoption (see MIGRATIONS.md's "Baseline adoption" section).
--
-- Runs after 01_schema.sql on a fresh container boot, so a freshly-created database already
-- has the full schema and this just marks the InitialBaseline migration as already applied --
-- `dotnet ef database update` against a fresh container becomes a no-op.
--
-- For an EXISTING local dev database (created before this migration adoption), insert this
-- same row once, by hand, as documented in db/README.md -- after that one-time step,
-- `dotnet ef database update` only ever applies genuinely new migrations from then on.
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId"    character varying(150) NOT NULL,
    "ProductVersion" character varying(32)  NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260730223702_InitialBaseline', '10.0.9')
ON CONFLICT ("MigrationId") DO NOTHING;
