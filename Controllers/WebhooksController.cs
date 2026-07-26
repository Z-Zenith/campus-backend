using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// AIS-02: receives Copyleaks' scan-completion callback (see CopyleaksClient.SubmitScanAsync,
// which registers the URL of this shape as the scan's webhook). Unauthenticated by necessity
// — Copyleaks itself calls this, not a signed-in user — so a shared secret is the only thing
// stopping an arbitrary caller from injecting fake plagiarism results into a submission's
// report.
//
// Copyleaks does NOT sign webhook payloads (no HMAC / signature header). Per Copyleaks'
// webhook-security docs the two supported ways to authenticate a callback are (a) mutual TLS
// with client-certificate thumbprint pinning and (b) the `developerPayload` field: a secret
// string set on the scan at submit time that Copyleaks echoes back in the webhook body, which
// you compare against your expected value. We use (b): the secret travels in the request BODY
// (Copyleaks:WebhookSecret -> properties.developerPayload in CopyleaksClient), not the URL, so
// it no longer leaks into access logs / proxy logs the way the previous `?secret=` query
// parameter did. mTLS pinning (a) is the stronger documented option but is infra-level (a
// dynamic thumbprint list requiring a daily refresh), out of scope for this code change.
// Docs: https://docs.copyleaks.com/concepts/security/webhooks
[ApiController]
[Route("api/v1/webhooks")]
[AllowAnonymous]
public class WebhooksController(AppDbContext db, IConfiguration configuration, ICopyleaksClient copyleaks) : ControllerBase
{
    [HttpPost("copyleaks/{scanId}/{status}")]
    public async Task<IActionResult> CopyleaksResult(string scanId, string status, [FromBody] JsonElement payload)
    {
        var expectedSecret = configuration["Copyleaks:WebhookSecret"];

        // Copyleaks echoes the secret we set at submit time back as the top-level
        // `developerPayload` field on every webhook type (completed/error/creditsUsage).
        string? developerPayload = null;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("developerPayload", out var dp)
            && dp.ValueKind == JsonValueKind.String)
        {
            developerPayload = dp.GetString();
        }

        if (string.IsNullOrEmpty(expectedSecret) || string.IsNullOrEmpty(developerPayload)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(developerPayload), Encoding.UTF8.GetBytes(expectedSecret)))
        {
            return Unauthorized();
        }

        if (!Guid.TryParseExact(scanId, "N", out var submissionId))
        {
            return BadRequest(new { error = "invalid_scan_id", message = "scanId does not map to a known submission." });
        }

        // Copyleaks also calls back with "error"/"creditsUsage" statuses this integration
        // doesn't act on — only a completed scan produces a report.
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return Ok();
        }

        var submissionExists = await db.Submissions.AnyAsync(s => s.Id == submissionId);
        if (!submissionExists)
        {
            return NotFound();
        }

        PlagiarismScanResult result;
        try
        {
            result = copyleaks.ParseWebhookResult(payload);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return BadRequest(new { error = "malformed_payload", message = "Unrecognized Copyleaks webhook payload shape." });
        }

        // Re-scanning (e.g. a teacher retriggers after a submission edit) replaces the
        // previous report rather than accumulating stale ones — same re-check idempotency
        // AIS-03's copy-check already follows.
        var existing = await db.PlagiarismReports.Where(r => r.SubmissionId == submissionId).ToListAsync();
        db.PlagiarismReports.RemoveRange(existing);

        db.PlagiarismReports.Add(new PlagiarismReport
        {
            Id = Guid.NewGuid(),
            SubmissionId = submissionId,
            SimilarityScore = (decimal)result.SimilarityScore,
            CopyleaksScanId = scanId,
            MatchedSources = JsonSerializer.Serialize(result.MatchedSourceUrls),
            CheckedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return Ok();
    }
}
