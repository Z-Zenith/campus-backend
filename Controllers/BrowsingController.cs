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

// Track 2 surface (whitelisted browser, telemetry, AI services) — stubbed here only to
// keep the shared API contract complete; implementation belongs to Track 2.
[ApiController]
[Route("api/v1")]
[Authorize]
public class BrowsingController(AppDbContext db, IAiServicesClient aiServices, IPermissionService permissions, INotificationRouter notifications) : ControllerBase
{
    // SDA-03: whitelist_sites is college-scoped (not per-class) — this is the already-
    // decided design that SDA-04's "approval applies institution-wide" acceptance
    // criterion depends on, so every class in the college shares one list.
    [HttpGet("whitelist")]
    public async Task<ActionResult<WhitelistResponse>> GetWhitelist()
    {
        var user = await CurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        var sites = await db.WhitelistSites
            .Where(s => s.CollegeId == user.CollegeId)
            .OrderBy(s => s.Url)
            .ToListAsync();

        return Ok(new WhitelistResponse(sites.Select(s => new WhitelistSiteDto(s.Id, s.Url, s.ApprovedAt)).ToList()));
    }

    // SDA-04
    [HttpPost("whitelist/requests")]
    public async Task<ActionResult<WhitelistRequestDto>> RequestWhitelist(CreateWhitelistRequestRequest request)
    {
        var user = await CurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }

        if (!TryNormalizeUrl(request.Url, out var normalizedUrl))
        {
            return BadRequest(new { error = "invalid_url", message = "URL must be an absolute http:// or https:// address." });
        }

        var alreadyWhitelisted = await db.WhitelistSites
            .AnyAsync(s => s.CollegeId == user.CollegeId && s.Url == normalizedUrl);
        if (alreadyWhitelisted)
        {
            return Conflict(new { error = "already_whitelisted", message = "This site is already approved for your college." });
        }

        var existingPending = await db.WhitelistRequests
            .Include(r => r.RequestedByNavigation)
            .FirstOrDefaultAsync(r => r.Url == normalizedUrl
                && r.Status == WhitelistRequestStatus.Pending
                && r.RequestedByNavigation.CollegeId == user.CollegeId);
        if (existingPending is not null)
        {
            return Ok(ToDto(existingPending));
        }

        var newRequest = new WhitelistRequest
        {
            Id = Guid.NewGuid(),
            Url = normalizedUrl,
            RequestedBy = user.Id,
            Status = WhitelistRequestStatus.Pending,
        };
        db.WhitelistRequests.Add(newRequest);
        await db.SaveChangesAsync();

        return Ok(ToDto(newRequest));
    }

    // SDA-04: not in the documented API map, but "Teacher sees a pending-request queue"
    // (acceptance criterion) has no way to be satisfied without a list endpoint.
    [HttpGet("whitelist/requests")]
    public async Task<ActionResult<List<WhitelistRequestDto>>> ListPendingRequests()
    {
        var user = await CurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }
        if (user.AccountType is not (AccountType.Teacher or AccountType.AdminTier))
        {
            return Forbid();
        }

        var pending = await db.WhitelistRequests
            .Include(r => r.RequestedByNavigation)
            .Where(r => r.Status == WhitelistRequestStatus.Pending && r.RequestedByNavigation.CollegeId == user.CollegeId)
            .ToListAsync();

        return Ok(pending.Select(ToDto).ToList());
    }

    // SDA-04
    [HttpPost("whitelist/requests/{id}/approve")]
    public async Task<ActionResult<ApproveWhitelistRequestResponse>> ApproveWhitelistRequest(Guid id)
    {
        var reviewer = await CurrentUserAsync();
        if (reviewer is null)
        {
            return Unauthorized();
        }
        if (reviewer.AccountType is not (AccountType.Teacher or AccountType.AdminTier))
        {
            return Forbid();
        }

        var whitelistRequest = await db.WhitelistRequests
            .Include(r => r.RequestedByNavigation)
            .FirstOrDefaultAsync(r => r.Id == id);
        // Same college-scoping as ListPendingRequests: a request from another college is
        // treated as not found rather than Forbidden, so this reviewer can't act on it or
        // even confirm it exists — matching the queue they're allowed to see.
        if (whitelistRequest is null || whitelistRequest.RequestedByNavigation.CollegeId != reviewer.CollegeId)
        {
            return NotFound();
        }
        if (whitelistRequest.Status != WhitelistRequestStatus.Pending)
        {
            return Conflict(new { error = "already_reviewed", message = "This request has already been reviewed." });
        }

        whitelistRequest.Status = WhitelistRequestStatus.Approved;
        whitelistRequest.ReviewedBy = reviewer.Id;

        var requesterCollegeId = whitelistRequest.RequestedByNavigation.CollegeId;
        var site = await db.WhitelistSites
            .FirstOrDefaultAsync(s => s.CollegeId == requesterCollegeId && s.Url == whitelistRequest.Url);
        var isNewSite = site is null;
        if (isNewSite)
        {
            site = new WhitelistSite
            {
                Id = Guid.NewGuid(),
                CollegeId = requesterCollegeId,
                Url = whitelistRequest.Url,
                ApprovedAt = DateTime.UtcNow,
            };
            db.WhitelistSites.Add(site);
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (isNewSite)
        {
            // Two pending requests for the same (college, url) approved concurrently: the
            // other one won the unique-index race and created the site first. Drop our
            // speculative insert, reuse the one that now exists, and retry so this
            // request's own status update still persists.
            db.Entry(site!).State = EntityState.Detached;
            site = await db.WhitelistSites.SingleAsync(s => s.CollegeId == requesterCollegeId && s.Url == whitelistRequest.Url);
            await db.SaveChangesAsync();
        }

        // Notification Router (shared) — SDA-04's requester finds out their request was
        // approved without polling ListPendingRequests. Single, unambiguous recipient
        // (the original requester), so this is a natural fit for the router unlike some
        // other notification_type values that would need a "who is Admin" resolution.
        await notifications.RouteAsync(whitelistRequest.RequestedBy, NotificationType.WhitelistRequest, new
        {
            whitelistRequestId = whitelistRequest.Id,
            url = whitelistRequest.Url,
            status = whitelistRequest.Status.ToString(),
            reviewedBy = reviewer.Id,
        });

        return Ok(new ApproveWhitelistRequestResponse(
            whitelistRequest.Id,
            whitelistRequest.Status.ToString(),
            new WhitelistSiteDto(site!.Id, site.Url, site.ApprovedAt)));
    }

    // SDA-03: curated engineering/CS reference sites a teacher/admin can pre-approve in one
    // click instead of waiting for students to request each one individually.
    private static readonly string[] DefaultEngineeringSites =
    [
        "https://github.com",
        "https://stackoverflow.com",
        "https://developer.mozilla.org",
        "https://docs.python.org",
        "https://www.w3schools.com",
        "https://www.geeksforgeeks.org",
        "https://leetcode.com",
        "https://www.hackerrank.com",
        "https://www.overleaf.com",
        "https://arxiv.org",
        "https://replit.com",
        "https://www.npmjs.com",
        "https://pypi.org",
        "https://www.wolframalpha.com",
        "https://www.freecodecamp.org",
        "https://www.khanacademy.org",
    ];

    // SDA-03
    [HttpPost("whitelist/defaults/engineering")]
    public async Task<ActionResult<AddDefaultSitesResponse>> AddEngineeringDefaults()
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (caller.AccountType is not (AccountType.Teacher or AccountType.AdminTier))
        {
            return Forbid();
        }

        var added = new List<WhitelistSiteDto>();
        var alreadyWhitelisted = new List<string>();

        foreach (var url in DefaultEngineeringSites)
        {
            // DefaultEngineeringSites entries are already well-formed absolute https:// URLs,
            // so normalization here can only ever succeed — TryNormalizeUrl is reused purely
            // to match the exact (scheme, lowercase host, no trailing root path) shape
            // already stored for sites approved via the request/approve flow.
            TryNormalizeUrl(url, out var normalizedUrl);

            var existing = await db.WhitelistSites
                .FirstOrDefaultAsync(s => s.CollegeId == caller.CollegeId && s.Url == normalizedUrl);
            if (existing is not null)
            {
                alreadyWhitelisted.Add(normalizedUrl);
                continue;
            }

            var site = new WhitelistSite
            {
                Id = Guid.NewGuid(),
                CollegeId = caller.CollegeId,
                Url = normalizedUrl,
                ApprovedAt = DateTime.UtcNow,
            };
            db.WhitelistSites.Add(site);
            added.Add(new WhitelistSiteDto(site.Id, site.Url, site.ApprovedAt));
        }

        await db.SaveChangesAsync();

        return Ok(new AddDefaultSitesResponse(added, alreadyWhitelisted));
    }

    // AIS-01: "a role without that permission cannot see the summary anywhere, including
    // in the student's own profile view" — the permission check applies unconditionally,
    // there's no self-view exception even for the student the summary is about.
    [HttpGet("students/{id}/browsing-summary")]
    public async Task<ActionResult<BrowsingSummaryReportDto>> BrowsingSummary(Guid id)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (!await permissions.HasPermissionAsync(caller.Id, "view_browsing_history"))
        {
            return Forbid();
        }

        var student = await db.Users.FindAsync(id);
        if (student is null)
        {
            return NotFound();
        }
        // #126: view_browsing_history is a global-scoped permission enforced platform-wide
        // today; without this a holder at one college could generate/persist a browsing
        // summary for a student at any other college. Mirror UsersController.GetProfile's
        // CollegeId check and 404-collapse convention (an out-of-college target is treated
        // as not found rather than Forbidden) — the check lands before any summary is
        // generated or written.
        if (student.CollegeId != caller.CollegeId)
        {
            return NotFound();
        }

        var visits = await db.BrowsingHistories
            .Where(v => v.StudentId == id)
            .OrderByDescending(v => v.VisitedAt)
            .ToListAsync();

        var visitInputs = visits.Select(v => new BrowsingVisitInput(v.Url, v.VisitedAt, v.DurationSeconds)).ToList();
        var summaryText = await aiServices.SummarizeBrowsingAsync(visitInputs);

        var summary = new BrowsingHistorySummary
        {
            Id = Guid.NewGuid(),
            StudentId = id,
            SummaryText = summaryText,
            GeneratedAt = DateTime.UtcNow,
        };
        db.BrowsingHistorySummaries.Add(summary);
        await db.SaveChangesAsync();

        return Ok(new BrowsingSummaryReportDto(summary.Id, summary.SummaryText, summary.GeneratedAt));
    }

    // AIS-01: logs a single page visit. The whitelisted browser (SDA-03/04) is expected
    // to call this on each navigation — feeds the raw input the summary above reads.
    [HttpPost("browsing-history")]
    public async Task<IActionResult> LogBrowsingVisit(LogBrowsingVisitRequest request)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (caller.AccountType != AccountType.Student)
        {
            return Forbid();
        }
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest(new { error = "url_required", message = "URL must not be empty." });
        }

        db.BrowsingHistories.Add(new BrowsingHistory
        {
            Id = Guid.NewGuid(),
            StudentId = caller.Id,
            Url = request.Url.Trim(),
            VisitedAt = DateTime.UtcNow,
            DurationSeconds = request.DurationSeconds,
        });
        await db.SaveChangesAsync();

        return NoContent();
    }

    // SDA-25 (Track 1) — telemetry ingestion isn't this endpoint's/track's job; AIS-07
    // below only reads whatever usage_telemetry rows already exist.
    [HttpPost("telemetry")]
    public IActionResult PostTelemetry() => StatusCode(501, new { feature = "SDA-25", status = "not_implemented" });

    // AIS-07: analyzes usage-pattern telemetry (SDA-25 writes it) for one class session
    // or assignment window via the self-hosted anomaly classifier. "Never shown to the
    // student" (acceptance criterion) is enforced by requiring a teacher/admin caller,
    // not by hiding an otherwise-reachable route. Re-analyzing replaces any previous
    // flags for the same window rather than accumulating stale ones.
    // Changed from the original stub's parameterless GET to a POST scoped by query
    // param: this triggers a fresh analysis and writes rows, and needs a window to
    // scope to — a bare GET with no params could never have returned anything meaningful.
    [HttpPost("suspicious-flags")]
    public async Task<ActionResult<List<SuspiciousFlagReportDto>>> SuspiciousFlags(
        [FromQuery] Guid? classSessionId, [FromQuery] Guid? assignmentId)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (caller.AccountType is not (AccountType.Teacher or AccountType.AdminTier))
        {
            return Forbid();
        }
        if (classSessionId is null == assignmentId is null)
        {
            return BadRequest(new { error = "scope_required", message = "Exactly one of classSessionId or assignmentId is required." });
        }

        Guid scopeTeacherId;
        if (classSessionId is { } activeClassSessionId)
        {
            var session = await db.ClassSessions.Include(s => s.TimetableSlot).FirstOrDefaultAsync(s => s.Id == activeClassSessionId);
            if (session is null)
            {
                return NotFound();
            }
            scopeTeacherId = session.ActualTeacherId ?? session.TimetableSlot.TeacherId;
        }
        else
        {
            var assignment = await db.Assignments.FindAsync(assignmentId!.Value);
            if (assignment is null)
            {
                return NotFound();
            }
            scopeTeacherId = assignment.TeacherId;
        }
        if (caller.AccountType is not AccountType.AdminTier && scopeTeacherId != caller.Id)
        {
            return Forbid();
        }

        var telemetry = await db.UsageTelemetries
            .Where(t => (classSessionId != null && t.ClassSessionId == classSessionId)
                || (assignmentId != null && t.AssignmentId == assignmentId))
            .ToListAsync();

        var existingFlags = await db.SuspiciousFlags
            .Where(f => (classSessionId != null && f.ClassSessionId == classSessionId)
                || (assignmentId != null && f.AssignmentId == assignmentId))
            .ToListAsync();
        db.SuspiciousFlags.RemoveRange(existingFlags);

        if (telemetry.Count == 0)
        {
            await db.SaveChangesAsync();
            return Ok(new List<SuspiciousFlagReportDto>());
        }

        var events = telemetry.Select(t => new TelemetryEventInput(
            t.StudentId.ToString(),
            t.ClassSessionId?.ToString(),
            t.AssignmentId?.ToString(),
            t.EventType,
            JsonSerializer.Deserialize<object>(t.Metadata) ?? new { },
            t.RecordedAt)).ToList();

        var results = await aiServices.CheckSuspiciousBehaviourAsync(events, minConfidence: 0.70);

        var now = DateTime.UtcNow;
        var flags = results.Select(r => new SuspiciousFlag
        {
            Id = Guid.NewGuid(),
            StudentId = Guid.Parse(r.StudentId),
            ClassSessionId = r.ClassSessionId is { } flaggedClassSessionId ? Guid.Parse(flaggedClassSessionId) : null,
            AssignmentId = r.AssignmentId is { } flaggedAssignmentId ? Guid.Parse(flaggedAssignmentId) : null,
            ConfidenceScore = (decimal)r.ConfidenceScore,
            FlaggedAt = now,
        }).ToList();
        db.SuspiciousFlags.AddRange(flags);
        await db.SaveChangesAsync();

        return Ok(flags.Select(f => new SuspiciousFlagReportDto(f.Id, f.ConfidenceScore, f.FlaggedAt, f.AssignmentId, f.ClassSessionId)).ToList());
    }

    private static WhitelistRequestDto ToDto(WhitelistRequest r) =>
        new(r.Id, r.Url, r.RequestedBy, r.Status.ToString(), r.ReviewedBy);

    private static bool TryNormalizeUrl(string url, out string normalized)
    {
        normalized = "";
        var trimmed = url?.Trim() ?? "";
        if (trimmed.Length == 0 || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // Host is case-insensitive per RFC 3986 but Uri doesn't lowercase it for us, and a
        // bare root path is equivalent to no path — without normalizing both, the same site
        // in different casing/trailing-slash form would dodge the already_whitelisted and
        // duplicate-pending-request checks below.
        var path = uri.AbsolutePath == "/" ? "" : uri.AbsolutePath;
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        normalized = $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{port}{path}{uri.Query}";
        return true;
    }

    private async Task<User?> CurrentUserAsync()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        return await db.Users.FindAsync(userId);
    }
}
