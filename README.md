# Backend API

ASP.NET Core / .NET 10 backend, database-first against PostgreSQL via EF Core
(`Data/Entities/*.cs` are scaffolded with `dotnet-ef dbcontext scaffold`, not
hand-authored). See `db/README.md` for local Postgres setup and connection
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
docker compose up -d postgres   # or the full stack: docker compose up -d
dotnet build
dotnet run
```

## AIS-02 (Copyleaks) local setup

`Services/CopyleaksClient.cs` is real, wired-up code, but this environment has no live
Copyleaks sandbox or credentials to verify it against — the wire format is best-effort
against Copyleaks' public v3 API docs (see the client's own doc comment). To actually
exercise the plagiarism-check flow end-to-end:

1. Sign up at [copyleaks.com](https://copyleaks.com) for an Email/ApiKey pair (this is an
   account Tejo needs to create directly — not something that can be scaffolded).
2. Set `COPYLEAKS_EMAIL`, `COPYLEAKS_API_KEY`, and `COPYLEAKS_WEBHOOK_SECRET` (any string
   you generate yourself, e.g. `openssl rand -base64 32`) in `campus-platform/.env` — see
   that repo's `.env.example` for the full variable list. Left unset, the plagiarism-check
   endpoint fails closed with a 503 (`ExternalServiceNotConfiguredException`) instead of
   crashing.
3. Copyleaks calls back `POST /api/v1/webhooks/copyleaks/{scanId}/{status}` from the public
   internet once a scan completes — `backend-api` needs to be reachable through a tunnel
   (ngrok, Cloudflare Tunnel, etc.) in local dev for that callback to ever arrive; a bare
   `localhost` or compose-internal address won't work. In production, this is whatever
   public domain the deployment already uses.
4. Submit one real assignment through `POST /submissions/{id}/plagiarism-check` and confirm
   the webhook lands and `GET /submissions/{id}/plagiarism-report` returns a populated
   report. If `CopyleaksClient.ParseWebhookResult`'s guessed field names
   (`results.score.aggregatedScore`, `results.internet[].url`) don't match the real payload
   shape, that's a small, isolated fix to make once the actual response is visible.

## AIS-05 (Pangram) local setup

Same situation as AIS-02 above: `Services/PangramClient.cs` is real code, unverified against
a live Pangram account. Unlike Copyleaks, this is a synchronous request/response call (no
webhook, no tunnel needed) — sign up at [pangram.com](https://pangram.com) for an API key, set
`PANGRAM_API_KEY` in `campus-platform/.env`, then call
`POST /api/v1/submissions/{id}/ai-detection` and confirm the response shape matches what
`PangramClient.DetectAsync` expects (`aiLikelihoodScore`, `reportId`) — adjust
`PangramDetectResponseBody` if the real API differs. Left unset, the endpoint fails closed
with a 503.
