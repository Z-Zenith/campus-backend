using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// SDA-25: usage-pattern telemetry reported by the student desktop app, gathered only
// while a class session or assignment window is active (SDA-01/11/12/13). The client only
// ever claims an AssignmentId; for events without one, this endpoint resolves the active
// class session itself via ClassSessionLookup — the same server-side authority SDA-12/
// TWA-08 use — rather than trusting a client-supplied session id. AC: "no telemetry
// reported outside active windows" is enforced here by rejecting any event that has
// neither an AssignmentId nor a currently-active class session, not just by the client's
// own gating (which the client also does, but a server can't rely on that alone).
[ApiController]
[Route("api/v1/telemetry")]
[Authorize]
public class TelemetryController(AppDbContext db, IHttpClientFactory httpClientFactory, ILogger<TelemetryController> logger) : ControllerBase
{
    [HttpPost("usage")]
    public async Task<ActionResult<SubmitTelemetryResponse>> SubmitUsage(SubmitTelemetryRequest request)
    {
        var studentId = CurrentUserId();
        var student = await db.Users.FindAsync(studentId);
        if (student is null || student.AccountType != AccountType.Student)
        {
            return Forbid();
        }

        var events = request.Events ?? [];
        foreach (var e in events)
        {
            if (string.IsNullOrWhiteSpace(e.EventType))
            {
                return BadRequest(new { error = "event_type_required" });
            }
        }

        // Resolved once per request (all events in a batch share "now"), not per-event —
        // avoids redundant lookups/ClassSession creation when a batch has several
        // class-window events queued from the same short polling interval.
        var activeSession = await ClassSessionLookup.FindOrStartActiveSessionAsync(db, studentId, DateTime.UtcNow);

        // #32: AssignmentId is client-supplied and previously never checked against the
        // submitting student's actual enrollment — unlike Submit/AutoSubmit
        // (AssignmentsController.IsEnrolledInAssignmentSubjectAsync), a student could pollute
        // any other college's assignment telemetry window just by naming its AssignmentId.
        // Cached per-assignment since a single batch commonly repeats the same id across
        // several events.
        var assignmentSubjectCache = new Dictionary<Guid, Guid?>();

        var records = new List<UsageTelemetry>();
        var resolvedEvents = new List<(TelemetryEventRequest Event, Guid? ClassSessionId)>();
        foreach (var e in events)
        {
            var classSessionId = e.AssignmentId is null ? activeSession?.ClassSessionId : null;
            if (e.AssignmentId is null && classSessionId is null)
            {
                return BadRequest(new { error = "window_required", message = "No active class session or assignment for this event." });
            }

            if (e.AssignmentId is { } assignmentId)
            {
                if (!assignmentSubjectCache.TryGetValue(assignmentId, out var subjectId))
                {
                    subjectId = await db.Assignments
                        .Where(a => a.Id == assignmentId)
                        .Select(a => (Guid?)a.SubjectId)
                        .FirstOrDefaultAsync();
                    assignmentSubjectCache[assignmentId] = subjectId;
                }

                var enrolled = subjectId is not null
                    && await AssignmentEnrollment.IsEnrolledInAssignmentSubjectAsync(db, studentId, subjectId.Value);
                if (!enrolled)
                {
                    return BadRequest(new
                    {
                        error = "invalid_assignment",
                        message = "AssignmentId must reference an assignment you are enrolled in.",
                    });
                }
            }

            records.Add(new UsageTelemetry
            {
                Id = Guid.NewGuid(),
                StudentId = studentId,
                ClassSessionId = classSessionId,
                AssignmentId = e.AssignmentId,
                EventType = e.EventType,
                Metadata = JsonSerializer.Serialize(e.Metadata ?? []),
                RecordedAt = e.RecordedAt,
            });
            resolvedEvents.Add((e, classSessionId));
        }

        db.UsageTelemetries.AddRange(records);
        await db.SaveChangesAsync();

        // AI Services is a Track-2-owned stub — forwarding is best-effort. If it's
        // unreachable, the raw telemetry is still safely persisted above for a later
        // batch pass; a student's request must never fail just because the anomaly
        // service happens to be down.
        var flagsRaised = await TryFlagSuspiciousBehaviourAsync(studentId, resolvedEvents);

        return Ok(new SubmitTelemetryResponse(records.Count, flagsRaised));
    }

    private async Task<int> TryFlagSuspiciousBehaviourAsync(Guid studentId, List<(TelemetryEventRequest Event, Guid? ClassSessionId)> events)
    {
        try
        {
            var client = httpClientFactory.CreateClient("AiServices");
            // Wire shape unchanged (still just `events`, no `min_confidence`), but casing now
            // comes from the one shared source of truth — AiServicesClient.JsonOptions
            // (SnakeCaseLower) — instead of hand-written snake_case literals + a separate
            // local options bag. PascalCase properties here map to snake_case on the wire.
            var payload = new
            {
                Events = events.Select(e => new
                {
                    StudentId = studentId.ToString(),
                    ClassSessionId = e.ClassSessionId?.ToString(),
                    AssignmentId = e.Event.AssignmentId?.ToString(),
                    EventType = e.Event.EventType,
                    Metadata = e.Event.Metadata ?? [],
                    RecordedAt = e.Event.RecordedAt,
                }),
            };

            var response = await client.PostAsJsonAsync("/api/v1/suspicious-behaviour", payload, AiServicesClient.JsonOptions);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("AI Services suspicious-behaviour check returned {Status}", response.StatusCode);
                return 0;
            }

            var result = await response.Content.ReadFromJsonAsync<SuspiciousBehaviourResponse>(AiServicesClient.JsonOptions);
            if (result?.Flags is not { Count: > 0 } flags)
            {
                return 0;
            }

            foreach (var flag in flags)
            {
                db.SuspiciousFlags.Add(new SuspiciousFlag
                {
                    Id = Guid.NewGuid(),
                    StudentId = studentId,
                    ClassSessionId = flag.ClassSessionId is { } csid ? Guid.Parse(csid) : null,
                    AssignmentId = flag.AssignmentId is { } aid ? Guid.Parse(aid) : null,
                    ConfidenceScore = (decimal)flag.ConfidenceScore,
                    FlaggedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
            return flags.Count;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Could not reach AI Services for suspicious-behaviour analysis.");
            return 0;
        }
    }

    // Response casing is handled by AiServicesClient.JsonOptions (SnakeCaseLower +
    // case-insensitive), the single source of truth for the ai-services JSON contract, so
    // these records stay plain PascalCase — no per-property [JsonPropertyName] drift.
    private record SuspiciousBehaviourResponse(List<SuspiciousFlagResponse> Flags);

    private record SuspiciousFlagResponse(
        string StudentId,
        string? ClassSessionId,
        string? AssignmentId,
        double ConfidenceScore,
        List<string> Reasons);

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
