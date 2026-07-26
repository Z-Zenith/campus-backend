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

// Track 2 surface (assignments, AI services) — stubbed here only to keep the shared
// API contract complete; implementation belongs to Track 2.
[ApiController]
[Route("api/v1")]
[Authorize]
public class AssignmentsController(AppDbContext db, IAiServicesClient aiServices, ICopyleaksClient copyleaks) : ControllerBase
{
    // TWA-07. Gated by "caller teaches this subject" rather than a permission code — no
    // "create_assignment" code exists in the seeded catalog, and adding one is an
    // OpenFGA/permission-catalog change needing separate sign-off.
    [HttpPost("assignments")]
    public async Task<ActionResult<AssignmentDto>> Create(CreateAssignmentRequest request)
    {
        var userId = CurrentUserId();
        var caller = await db.Users.FindAsync(userId);
        if (caller is null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(new { error = "title_required", message = "Assignment title must not be empty." });
        }
        // An assignment with no due date cannot be published (TWA-07 acceptance
        // criterion) — DueDate is non-nullable, so the only way a caller can "omit" it
        // is by leaving it at the JSON default, which we reject explicitly here.
        if (request.DueDate == default)
        {
            return BadRequest(new { error = "due_date_required", message = "Assignment must have a due date." });
        }
        if (request.SubmissionWindowStart >= request.SubmissionWindowEnd)
        {
            return BadRequest(new { error = "invalid_window", message = "Submission window start must be before its end." });
        }
        if (request.TypeSpecificSettings is not null && !IsValidJson(request.TypeSpecificSettings))
        {
            return BadRequest(new { error = "invalid_settings", message = "typeSpecificSettings must be valid JSON." });
        }

        var subject = await db.Subjects.FindAsync(request.SubjectId);
        if (subject is null)
        {
            return BadRequest(new { error = "unknown_subject", message = "No subject exists with that id." });
        }
        if (caller.AccountType is not AccountType.AdminTier)
        {
            if (subject.TeacherId != caller.Id)
            {
                return Forbid();
            }
        }
        // #11: AdminTier bypassed the teacher-ownership check above unconditionally,
        // letting an Admin at one college create assignments for a subject taught at a
        // different college — same cross-college IDOR class CollegeScopeService closes
        // elsewhere (#126-#129).
        else if (!await IsSubjectInCollegeAsync(subject.Id, caller.CollegeId))
        {
            return Forbid();
        }

        var assignment = new Assignment
        {
            Id = Guid.NewGuid(),
            SubjectId = request.SubjectId,
            TeacherId = subject.TeacherId ?? caller.Id,
            Title = request.Title.Trim(),
            Description = request.Description,
            Type = request.Type,
            DueDate = request.DueDate,
            SubmissionWindowStart = request.SubmissionWindowStart,
            SubmissionWindowEnd = request.SubmissionWindowEnd,
            TypeSpecificSettings = request.TypeSpecificSettings,
        };
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();

        return Ok(ToDto(assignment));
    }

    // SDA-10. SDA-11 (auto-submit on exit) is Track 1 — a different trigger path
    // (student-desktop exit detection), implemented below in AutoSubmit(). Manual
    // submissions always leave IsAutosubmitted=false so the teacher's view can tell
    // the two apart (SDA-11 acceptance criterion).
    [HttpPost("assignments/{id}/submissions")]
    public async Task<ActionResult<SubmissionDto>> Submit(Guid id, SubmitAssignmentRequest request)
    {
        var userId = CurrentUserId();
        var student = await db.Users.FindAsync(userId);
        if (student is null)
        {
            return Unauthorized();
        }
        if (student.AccountType != AccountType.Student)
        {
            return Forbid();
        }

        var assignment = await db.Assignments.FindAsync(id);
        if (assignment is null)
        {
            return NotFound();
        }

        // #135: verify the caller is actually enrolled in a section this assignment's
        // subject is taught to, mirroring MarksController.CreateInternal's
        // TeacherSectionAssignments/SectionEnrollments check — without this, any
        // authenticated student could submit against any assignment id in the system
        // (IDOR). Collapsed into the same NotFound() as "assignment doesn't exist" (#93)
        // so an unenrolled caller can't distinguish the two by probing ids.
        if (!await IsEnrolledInAssignmentSubjectAsync(userId, assignment.SubjectId))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.ContentUrl))
        {
            return BadRequest(new { error = "content_required", message = "Submission content must not be empty." });
        }
        // Acceptance criterion: "a quiz-type assignment cannot be submitted as a file
        // upload" — generalized to any format mismatch, not just the quiz/file case.
        if (request.SubmissionFormat != assignment.Type)
        {
            return BadRequest(new
            {
                error = "format_mismatch",
                message = $"This assignment requires a {assignment.Type} submission, not {request.SubmissionFormat}.",
            });
        }

        // #159: mirrors AutoSubmit's own check below — a student who already has a
        // submission (manual or auto) for this assignment shouldn't be able to create an
        // unlimited number of additional ones just by calling this endpoint again. There's
        // no unique DB constraint on (assignment_id, student_id) (only a non-unique index —
        // adding one would be a schema change requiring CLAUDE.md's contract-change sign-
        // off), so this is enforced at the application level, same as AutoSubmit.
        var alreadySubmitted = await db.Submissions
            .AnyAsync(s => s.AssignmentId == id && s.StudentId == userId);
        if (alreadySubmitted)
        {
            return Conflict(new { error = "already_submitted", message = "A submission already exists for this assignment." });
        }

        var submittedAt = DateTime.UtcNow;
        var submission = new Submission
        {
            Id = Guid.NewGuid(),
            AssignmentId = id,
            StudentId = userId,
            ContentUrl = request.ContentUrl.Trim(),
            SubmittedAt = submittedAt,
            IsLate = submittedAt > assignment.DueDate,
            IsAutosubmitted = false,
        };
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        return Ok(ToSubmissionDto(submission));
    }

    // SDA-11. Called by the student desktop app when it detects app exit or focus loss
    // while an assignment window is active, to persist the student's current
    // work-in-progress as an auto-submission. Distinct from Submit() above only in that
    // it (a) requires the submission window to still be open — this is a background
    // safety net for an active window, not a way to submit after the fact — and
    // (b) sets IsAutosubmitted=true so the teacher's view can flag it as such
    // (acceptance criterion: auto-submitted must be visually/data distinct from manual).
    [HttpPost("assignments/{id}/submissions/auto-submit")]
    public async Task<ActionResult<SubmissionDto>> AutoSubmit(Guid id, SubmitAssignmentRequest request)
    {
        var userId = CurrentUserId();
        var student = await db.Users.FindAsync(userId);
        if (student is null)
        {
            return Unauthorized();
        }
        if (student.AccountType != AccountType.Student)
        {
            return Forbid();
        }

        var assignment = await db.Assignments.FindAsync(id);
        if (assignment is null)
        {
            return NotFound();
        }

        // #135: same enrollment check as Submit() above — an auto-submit request must be
        // gated by section enrollment too, not just a valid-looking assignment id.
        if (!await IsEnrolledInAssignmentSubjectAsync(userId, assignment.SubjectId))
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        if (now < assignment.SubmissionWindowStart || now > assignment.SubmissionWindowEnd)
        {
            return BadRequest(new
            {
                error = "window_inactive",
                message = "The submission window is not currently open, so this assignment cannot be auto-submitted.",
            });
        }

        if (string.IsNullOrWhiteSpace(request.ContentUrl))
        {
            return BadRequest(new { error = "content_required", message = "Submission content must not be empty." });
        }
        if (request.SubmissionFormat != assignment.Type)
        {
            return BadRequest(new
            {
                error = "format_mismatch",
                message = $"This assignment requires a {assignment.Type} submission, not {request.SubmissionFormat}.",
            });
        }

        // A student who already has a submission for this assignment (manual or a
        // prior auto-submit) doesn't need another one — exit/focus-loss can fire
        // repeatedly (e.g. losing focus after already submitting).
        var alreadySubmitted = await db.Submissions
            .AnyAsync(s => s.AssignmentId == id && s.StudentId == userId);
        if (alreadySubmitted)
        {
            return Conflict(new { error = "already_submitted", message = "A submission already exists for this assignment." });
        }

        var submittedAt = now;
        var submission = new Submission
        {
            Id = Guid.NewGuid(),
            AssignmentId = id,
            StudentId = userId,
            ContentUrl = request.ContentUrl.Trim(),
            SubmittedAt = submittedAt,
            IsLate = submittedAt > assignment.DueDate,
            IsAutosubmitted = true,
        };
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        return Ok(ToSubmissionDto(submission));
    }

    // AIS-02: kicks off an async Copyleaks scan (see CopyleaksClient) — unlike AIS-03's
    // copy-check, the similarity score isn't available synchronously; it arrives later
    // via WebhooksController.CopyleaksResult. Gated the same as copy-check: the
    // assignment's own teacher, or Admin. Changed from the original stub's GET on
    // /plagiarism-report to a separate POST /plagiarism-check that triggers the scan —
    // /plagiarism-report (below) stays a GET since it now just reads whatever's persisted.
    [HttpPost("submissions/{id}/plagiarism-check")]
    public async Task<IActionResult> RequestPlagiarismCheck(Guid id)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }

        var submission = await db.Submissions.Include(s => s.Assignment).FirstOrDefaultAsync(s => s.Id == id);
        if (submission is null)
        {
            return NotFound();
        }
        if (!await CanAccessAssignmentAsync(submission.Assignment, caller))
        {
            return Forbid();
        }

        var scanId = id.ToString("N");
        try
        {
            // No secret in the URL: it would leak into access/proxy logs. The shared secret
            // is carried in the scan's properties.developerPayload instead (set by
            // CopyleaksClient) and validated from the webhook body by WebhooksController.
            var webhookUrlTemplate =
                $"{Request.Scheme}://{Request.Host}/api/v1/webhooks/copyleaks/{scanId}/{{status}}";
            await copyleaks.SubmitScanAsync(scanId, submission.ContentUrl, webhookUrlTemplate);
        }
        catch (ExternalServiceNotConfiguredException)
        {
            return StatusCode(503, new
            {
                error = "service_not_configured",
                message = "Internet plagiarism checking is not configured for this deployment (missing Copyleaks credentials).",
            });
        }

        return Accepted(new PlagiarismCheckAcceptedDto(id, scanId, "pending"));
    }

    // Never shown to the submitting student (AIS-02 acceptance criterion) — enforced here
    // by the same teacher-or-Admin gate as the trigger endpoint above, not by hiding an
    // otherwise-reachable route.
    [HttpGet("submissions/{id}/plagiarism-report")]
    public async Task<IActionResult> PlagiarismReport(Guid id)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }

        var submission = await db.Submissions.Include(s => s.Assignment).FirstOrDefaultAsync(s => s.Id == id);
        if (submission is null)
        {
            return NotFound();
        }
        if (!await CanAccessAssignmentAsync(submission.Assignment, caller))
        {
            return Forbid();
        }

        var report = await db.PlagiarismReports.FirstOrDefaultAsync(r => r.SubmissionId == id);
        if (report is null)
        {
            return Ok(new PlagiarismReportStatusDto(id, "pending"));
        }

        return Ok(ToPlagiarismDto(report));
    }

    [HttpGet("submissions/{id}/ai-detection")]
    public IActionResult AiDetection(Guid id) => StatusCode(501, new { feature = "AIS-05", status = "not_implemented" });

    // AIS-03: compares this assignment's submissions against each other via the
    // self-hosted embedding-similarity model, flagging matches at >=90% (per
    // copy_check_flags' own documented threshold). Re-checking replaces any previous
    // flags for this assignment's submissions rather than accumulating stale ones.
    // Changed from the original stub's GET to a POST: this triggers a fresh analysis
    // and writes rows, it doesn't just fetch a cached resource.
    [HttpPost("assignments/{id}/copy-check")]
    public async Task<ActionResult<List<CopyCheckMatchDto>>> CopyCheck(Guid id)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }

        var assignment = await db.Assignments.FindAsync(id);
        if (assignment is null)
        {
            return NotFound();
        }
        if (!await CanAccessAssignmentAsync(assignment, caller))
        {
            return Forbid();
        }

        var submissions = await db.Submissions.Where(s => s.AssignmentId == id).ToListAsync();
        var submissionIds = submissions.Select(s => s.Id).ToHashSet();

        var existingFlags = await db.CopyCheckFlags
            .Where(f => submissionIds.Contains(f.SubmissionAId) || submissionIds.Contains(f.SubmissionBId))
            .ToListAsync();
        db.CopyCheckFlags.RemoveRange(existingFlags);

        if (submissions.Count < 2)
        {
            await db.SaveChangesAsync();
            return Ok(new List<CopyCheckMatchDto>());
        }

        var pairs = submissions.Select(s => (s.Id.ToString(), s.ContentUrl)).ToList();
        var matches = await aiServices.CheckSimilarityAsync(pairs, threshold: 0.90);

        var now = DateTime.UtcNow;
        var flags = matches.Select(m => new CopyCheckFlag
        {
            Id = Guid.NewGuid(),
            SubmissionAId = Guid.Parse(m.SubmissionAId),
            SubmissionBId = Guid.Parse(m.SubmissionBId),
            SimilarityScore = (decimal)m.SimilarityScore,
            FlaggedAt = now,
        }).ToList();
        db.CopyCheckFlags.AddRange(flags);
        await db.SaveChangesAsync();

        return Ok(flags.Select(f => new CopyCheckMatchDto(f.SubmissionAId, f.SubmissionBId, f.SimilarityScore)).ToList());
    }

    // AIS-04: advisory only, via the self-hosted keyword-rubric model — the caller
    // (teacher) supplies the rubric ad hoc since no Rubric table exists in the schema.
    // Changed from the original stub's parameterless GET to a POST carrying the rubric:
    // a rubric can't be threaded through query-string params sanely.
    [HttpPost("submissions/{id}/autograde-suggestion")]
    public async Task<ActionResult<AutogradeSuggestionDto>> AutogradeSuggestion(Guid id, RequestAutogradeSuggestion request)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }

        var submission = await db.Submissions.Include(s => s.Assignment).FirstOrDefaultAsync(s => s.Id == id);
        if (submission is null)
        {
            return NotFound();
        }
        if (!await CanAccessAssignmentAsync(submission.Assignment, caller))
        {
            return Forbid();
        }

        if (request.Rubric.Count == 0)
        {
            return BadRequest(new { error = "rubric_required", message = "At least one rubric criterion is required." });
        }
        var totalWeight = request.Rubric.Sum(c => c.Weight);
        if (Math.Abs(totalWeight - 1.0) > 0.01)
        {
            return BadRequest(new { error = "invalid_weights", message = $"Rubric weights must sum to 1.0 (got {totalWeight:F4})." });
        }

        var rubric = request.Rubric.Select(c => new RubricCriterion(c.Name, c.Keywords, c.Weight)).ToList();
        var result = await aiServices.SuggestAutogradeAsync(submission.ContentUrl, rubric, request.MaxScore);

        var suggestion = new Data.Entities.AutogradeSuggestion
        {
            Id = Guid.NewGuid(),
            SubmissionId = id,
            SuggestedGrade = (decimal)result.SuggestedGrade,
            ConfirmedByTeacher = false,
        };
        db.AutogradeSuggestions.Add(suggestion);
        await db.SaveChangesAsync();

        return Ok(new AutogradeSuggestionDto(
            suggestion.Id, id, suggestion.SuggestedGrade, result.MaxScore, result.Confidence, result.MatchedCriteria, result.Feedback));
    }

    // Bookkeeping distinct from actually publishing marks (TWA-16, POST /marks/internal)
    // — this only records that a teacher reviewed and accepted a specific suggestion,
    // matching the acceptance criterion that autograde is "never auto-published".
    [HttpPost("submissions/{id}/grade")]
    public async Task<ActionResult<ConfirmedGradeDto>> Grade(Guid id, ConfirmGradeRequest request)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }

        var suggestion = await db.AutogradeSuggestions
            .Include(s => s.Submission).ThenInclude(sub => sub.Assignment)
            .FirstOrDefaultAsync(s => s.Id == request.SuggestionId && s.SubmissionId == id);
        if (suggestion is null)
        {
            return NotFound();
        }
        if (!await CanAccessAssignmentAsync(suggestion.Submission.Assignment, caller))
        {
            return Forbid();
        }

        suggestion.ConfirmedByTeacher = true;
        suggestion.ConfirmedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Ok(new ConfirmedGradeDto(suggestion.Id, suggestion.SubmissionId, suggestion.SuggestedGrade, suggestion.ConfirmedByTeacher, suggestion.ConfirmedAt));
    }

    // #135: a student is "enrolled in this assignment's subject" if they're a member of
    // any section that subject is actually taught to — same TeacherSectionAssignments +
    // SectionEnrollments join MarksController.CreateInternal uses to scope teacher writes,
    // applied here to scope student submissions instead.
    private async Task<bool> IsEnrolledInAssignmentSubjectAsync(Guid studentId, Guid subjectId)
    {
        var taughtSectionIds = await db.TeacherSectionAssignments
            .Where(a => a.SubjectId == subjectId)
            .Select(a => a.SectionId)
            .ToListAsync();
        if (taughtSectionIds.Count == 0)
        {
            return false;
        }

        return await db.SectionEnrollments
            .AnyAsync(e => e.StudentId == studentId && taughtSectionIds.Contains(e.SectionId));
    }

    // #11: shared by every teacher-or-Admin gate in this controller (previously each
    // one let AdminTier bypass ownership unconditionally) — an Admin may still act on any
    // assignment/submission the actual teacher could, but only within the Admin's own
    // college, matching the cross-college IDOR fix CollegeScopeService applies elsewhere
    // (#126-#129).
    private async Task<bool> CanAccessAssignmentAsync(Assignment assignment, User caller)
    {
        if (caller.AccountType is not AccountType.AdminTier)
        {
            return assignment.TeacherId == caller.Id;
        }

        return await IsSubjectInCollegeAsync(assignment.SubjectId, caller.CollegeId);
    }

    private async Task<bool> IsSubjectInCollegeAsync(Guid subjectId, Guid collegeId) =>
        await db.Subjects.AnyAsync(s => s.Id == subjectId && s.Department.CollegeId == collegeId);

    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AssignmentDto ToDto(Assignment a) => new(
        a.Id, a.SubjectId, a.Title, a.Description, a.Type.ToString(),
        a.DueDate, a.SubmissionWindowStart, a.SubmissionWindowEnd, a.TypeSpecificSettings);

    private static SubmissionDto ToSubmissionDto(Submission s) => new(
        s.Id, s.AssignmentId, s.StudentId, s.ContentUrl, s.SubmittedAt, s.IsLate, s.IsAutosubmitted);

    private static PlagiarismReportDto ToPlagiarismDto(PlagiarismReport r) => new(
        r.Id, r.SubmissionId, r.SimilarityScore, r.CopyleaksScanId,
        string.IsNullOrEmpty(r.MatchedSources) ? [] : JsonSerializer.Deserialize<List<string>>(r.MatchedSources) ?? [],
        r.CheckedAt);

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private Task<User?> CurrentUserAsync() => db.Users.FindAsync(CurrentUserId()).AsTask();
}
