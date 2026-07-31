using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

[ApiController]
[Route("api/v1/marks")]
public class MarksController(AppDbContext db, IAppAuthorizationService permissions, ICollegeScopeService collegeScope) : ControllerBase
{
    private const string AddExternalMarksPermission = "add_external_marks";

    // TWA-16. Internal marks are direct-publish (no approval gate, unlike TWA-17/TWA-20) —
    // publishing just requires the teacher's own explicit action via the Publish flag.
    [HttpPost("internal")]
    [Authorize]
    public async Task<ActionResult<InternalMarkRecordDto>> CreateInternal(CreateInternalMarkRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "add_internal_marks"))
        {
            return Forbid();
        }

        if (request.Marks < 0)
        {
            return BadRequest(new { error = "invalid_marks", message = "Marks must not be negative." });
        }

        // Scope to the caller's own section/subject: the teacher must be assigned to teach
        // this subject to a section the student is actually enrolled in. Which section(s) the
        // student belongs to is a roster lookup, not an authorization check, so it stays a
        // direct query; the ownership question itself ("does this teacher teach this subject
        // in that section") goes through the shared relation engine.
        var studentSectionIds = await db.SectionEnrollments
            .Where(e => e.StudentId == request.StudentId)
            .Select(e => e.SectionId)
            .ToListAsync();

        var authorizedForStudent = false;
        foreach (var sectionId in studentSectionIds)
        {
            if (await permissions.CheckRelationAsync(userId, "teacher", "section", $"{sectionId}:{request.SubjectId}"))
            {
                authorizedForStudent = true;
                break;
            }
        }
        if (!authorizedForStudent)
        {
            return Forbid();
        }

        if (request.AssignmentId is { } assignmentId)
        {
            var assignment = await db.Assignments.FindAsync(assignmentId);
            if (assignment is null || assignment.SubjectId != request.SubjectId)
            {
                return BadRequest(new { error = "invalid_assignment", message = "Assignment does not belong to this subject." });
            }
        }

        // Upsert: re-submitting for the same student/subject/assignment updates the existing
        // row instead of creating a duplicate. An already-published mark's Published state is
        // never cleared by a Publish=false request — only an explicit publish action changes it.
        var mark = await db.InternalMarks.FirstOrDefaultAsync(m =>
            m.StudentId == request.StudentId &&
            m.SubjectId == request.SubjectId &&
            m.AssignmentId == request.AssignmentId);

        if (mark is null)
        {
            mark = new Data.Entities.InternalMark
            {
                Id = Guid.NewGuid(),
                StudentId = request.StudentId,
                SubjectId = request.SubjectId,
                AssignmentId = request.AssignmentId,
            };
            db.InternalMarks.Add(mark);
        }

        mark.Marks = request.Marks;
        if (request.Publish)
        {
            mark.Published = true;
            mark.PublishedAt = DateTime.UtcNow;
            mark.PublishedBy = userId;
        }

        await db.SaveChangesAsync();

        return Ok(new InternalMarkRecordDto(mark.Id, mark.StudentId, mark.SubjectId, mark.AssignmentId, mark.Marks, mark.Published, mark.PublishedAt));
    }

    // TWA-16 support endpoint — lets the marks-entry screen list the students the caller
    // may actually enter marks for, instead of requiring student ids to be typed blind.
    [HttpGet("internal/roster")]
    [Authorize]
    public async Task<ActionResult<List<InternalMarksRosterEntryDto>>> InternalRoster([FromQuery] Guid subjectId, [FromQuery] Guid? assignmentId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "add_internal_marks"))
        {
            return Forbid();
        }

        var teacherSectionIds = await db.TeacherSectionAssignments
            .Where(a => a.TeacherId == userId && a.SubjectId == subjectId)
            .Select(a => a.SectionId)
            .ToListAsync();
        if (teacherSectionIds.Count == 0)
        {
            return Forbid();
        }

        var students = await db.SectionEnrollments
            .Where(e => teacherSectionIds.Contains(e.SectionId))
            .Select(e => e.Student)
            .Distinct()
            .OrderBy(s => s.FullName)
            .ToListAsync();

        var existingMarks = await db.InternalMarks
            .Where(m => m.SubjectId == subjectId && m.AssignmentId == assignmentId)
            .ToListAsync();
        var marksByStudent = existingMarks.ToDictionary(m => m.StudentId);

        var roster = students.Select(s => marksByStudent.TryGetValue(s.Id, out var mark)
                ? new InternalMarksRosterEntryDto(s.Id, s.FullName, mark.Marks, mark.Published, mark.PublishedAt)
                : new InternalMarksRosterEntryDto(s.Id, s.FullName, null, false, null))
            .ToList();

        return Ok(roster);
    }

    // TWA-17. Gated by an active, non-expired add_external_marks PermissionGrant — this
    // permission has no role-default bundle (see db/init/02_seed_roles_and_permissions.sql),
    // so HasPermissionAsync effectively only returns true for a live, unexpired grant row.
    // Submissions land here unapproved; TWA-20 is the only path that flips Approved/Published,
    // so a submitted mark is never directly visible to the student/parent until then.
    [HttpPost("external")]
    [Authorize]
    public async Task<ActionResult<ExternalMarkSubmissionResponse>> CreateExternal(CreateExternalMarkRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, AddExternalMarksPermission))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Grade))
        {
            return BadRequest(new { error = "grade_required", message = "Grade must not be empty." });
        }

        var student = await db.Users.FindAsync(request.StudentId);
        if (student is null || student.AccountType != AccountType.Student)
        {
            return BadRequest(new { error = "unknown_student", message = "No student exists with that id." });
        }

        var subject = await db.Subjects.Include(s => s.Department).FirstOrDefaultAsync(s => s.Id == request.SubjectId);
        if (subject is null)
        {
            return BadRequest(new { error = "unknown_subject", message = "No subject exists with that id." });
        }

        // #129: add_external_marks has no DepartmentId/CollegeId column on PermissionGrant
        // at all (contradicts model.fga's department-scoped add_external_marks_grant), and
        // this endpoint previously only checked the student/subject existed, not that they
        // belong to the grant holder's own college. Clamp both to the caller's college.
        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
        if (student.CollegeId != callerCollegeId || subject.Department.CollegeId != callerCollegeId)
        {
            return Forbid();
        }

        var externalMark = new ExternalMark
        {
            Id = Guid.NewGuid(),
            StudentId = request.StudentId,
            SubjectId = request.SubjectId,
            Grade = request.Grade.Trim(),
            SubmittedBy = userId,
            SubmittedAt = DateTime.UtcNow,
            Approved = false,
            Published = false,
        };
        db.ExternalMarks.Add(externalMark);
        await db.SaveChangesAsync();

        return Ok(new ExternalMarkSubmissionResponse(
            externalMark.Id,
            externalMark.StudentId,
            externalMark.SubjectId,
            externalMark.Grade,
            "pending_approval",
            externalMark.SubmittedAt));
    }

    // TWA-17 — read-only check the teacher-web UI polls to decide whether the "submit
    // external marks" option should render at all. Reads the grant's own status (including
    // ExpiresAt, which a plain HasPermissionAsync bool can't supply) via the shared service
    // rather than depending on AWA-13's grant-management endpoints (owned/implemented
    // separately), matching the same "is there a live, unexpired grant" rule enforced above.
    [HttpGet("external/permission-status")]
    [Authorize]
    public async Task<ActionResult<ExternalMarksPermissionStatusResponse>> ExternalMarksPermissionStatus()
    {
        var userId = CurrentUserId();
        var (granted, expiresAt) = await permissions.GetPermissionGrantStatusAsync(userId, AddExternalMarksPermission);
        return Ok(new ExternalMarksPermissionStatusResponse(granted, expiresAt));
    }

    // TWA-20 — approval queue for holders of the approve_external_marks permission.
    // HoD grants are department-scoped (architecture doc Section 9), so a HoD only sees
    // pending marks for subjects in their own department; a global grant (e.g. Admin via
    // a PermissionGrant) sees everything still pending.
    [HttpGet("external/pending")]
    [Authorize]
    public async Task<ActionResult<List<PendingExternalMarkDto>>> PendingExternal()
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "approve_external_marks"))
        {
            return Forbid();
        }

        var departmentScope = await permissions.GetDepartmentScopeAsync(userId);

        var query = db.ExternalMarks.Where(m => !m.Approved);
        if (departmentScope is not null)
        {
            query = query.Where(m => m.Subject.DepartmentId == departmentScope);
        }
        else
        {
            // #126/#129: a global (non-department-scoped) approve_external_marks holder — the
            // Admin path — must only see pending marks for students in their own college, not
            // every college's queue. Mirrors CreateExternal's college clamp. Filtered on the
            // student's college so this stays consistent with ApproveExternal's own check
            // below (a mark listed here can actually be approved there).
            var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
            query = query.Where(m => m.Student.CollegeId == callerCollegeId);
        }

        var pending = await query
            .OrderBy(m => m.SubmittedAt)
            .Select(m => new PendingExternalMarkDto(
                m.Id,
                m.StudentId,
                m.Student.FullName,
                m.SubjectId,
                m.Subject.Name,
                m.Grade,
                m.SubmittedBy,
                m.SubmittedByNavigation.FullName,
                m.SubmittedAt))
            .ToListAsync();

        return Ok(pending);
    }

    // TWA-20 — marks stay invisible to the student (SDA-15 / PRT-02) until a holder of
    // approve_external_marks approves them here. External marks have no separate
    // direct-publish step like TWA-16's internal marks, so approval both flips `approved`
    // and `published` in one atomic update — that's the entire visibility gate for this flow.
    [HttpPost("external/{id}/approve")]
    [Authorize]
    public async Task<ActionResult<ApproveExternalMarkResponse>> ApproveExternal(Guid id)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "approve_external_marks"))
        {
            return Forbid();
        }

        var mark = await db.ExternalMarks.FindAsync(id);
        if (mark is null)
        {
            return NotFound();
        }

        var departmentScope = await permissions.GetDepartmentScopeAsync(userId);
        if (departmentScope is not null)
        {
            var subjectDepartmentId = await db.Subjects
                .Where(s => s.Id == mark.SubjectId)
                .Select(s => s.DepartmentId)
                .FirstOrDefaultAsync();
            if (subjectDepartmentId != departmentScope)
            {
                return Forbid();
            }
        }
        else
        {
            // #126/#129: a global (non-department-scoped) approve_external_marks holder — the
            // Admin path — must not approve/publish a mark for a student outside their own
            // college. Verified before the ExecuteUpdateAsync so a cross-college row is never
            // flipped to Approved/Published. Uses the 404-collapse convention (treated as not
            // found rather than Forbidden), matching the department path's own scope guard's
            // intent while not leaking that a row for another college exists.
            var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
            var studentCollegeId = await db.Users
                .Where(u => u.Id == mark.StudentId)
                .Select(u => (Guid?)u.CollegeId)
                .FirstOrDefaultAsync();
            if (studentCollegeId != callerCollegeId)
            {
                return NotFound();
            }
        }

        var approvedAt = DateTime.UtcNow;
        // Atomic conditional update closes the check-then-act race between concurrent
        // approve requests for the same row — only the request that actually flips
        // `approved` runs the state transition; a losing concurrent request sees 0 rows.
        var rowsUpdated = await db.ExternalMarks
            .Where(m => m.Id == id && !m.Approved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Approved, true)
                .SetProperty(m => m.ApprovedBy, userId)
                .SetProperty(m => m.ApprovedAt, approvedAt)
                .SetProperty(m => m.Published, true));

        if (rowsUpdated == 0)
        {
            return Conflict(new { error = "already_approved", message = "This external mark has already been approved." });
        }

        return Ok(new ApproveExternalMarkResponse(id, userId, approvedAt));
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    // SDA-15 — published marks only, mirrors PRT-02's filtering logic scoped to the logged-in student.
    [HttpGet("mine")]
    [Authorize]
    public async Task<ActionResult<MyMarksResponse>> Mine()
    {
        var studentId = CurrentUserId();

        var internalMarks = await PublishedMarksQueries.GetPublishedInternalMarksAsync(db, studentId);
        var externalMarks = await PublishedMarksQueries.GetPublishedExternalMarksAsync(db, studentId);

        return Ok(new MyMarksResponse(internalMarks, externalMarks));
    }

    // PRT-02 — attendance + published marks only, matching SDA-15's publish rule.
    [HttpGet("ward/{studentId}")]
    [Authorize]
    [ServiceFilter(typeof(WardAccessFilter))]
    public async Task<ActionResult<WardRecordResponse>> Ward(Guid studentId)
    {
        var student = await db.Users.FindAsync(studentId);
        if (student is null)
        {
            return NotFound();
        }

        var attendance = await db.AttendanceRecords
            .Where(a => a.StudentId == studentId)
            .OrderByDescending(a => a.ClassSession.SessionDate)
            .Select(a => new AttendanceRecordDto(
                a.ClassSession.SessionDate,
                a.ClassSession.TimetableSlot.SubjectId,
                a.ClassSession.TimetableSlot.Subject.Name,
                a.Status.ToString()))
            .ToListAsync();

        var internalMarks = await PublishedMarksQueries.GetPublishedInternalMarksAsync(db, studentId);
        var externalMarks = await PublishedMarksQueries.GetPublishedExternalMarksAsync(db, studentId);

        return Ok(new WardRecordResponse(student.Id, student.FullName, attendance, internalMarks, externalMarks));
    }
}
