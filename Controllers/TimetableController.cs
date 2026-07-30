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
[Route("api/v1")]
[Authorize]
public class TimetableController(AppDbContext db, IPermissionService permissions, INotificationRouter notifications, ICollegeScopeService collegeScope, ILogger<TimetableController> logger) : ControllerBase
{
    // 5 weekdays x 6 one-hour periods starting 9am. MVP scheduling heuristic, not a
    // constraint solver — enough to satisfy AWA-01/AWA-02's stated acceptance criteria.
    private static readonly (int Day, TimeOnly Start, TimeOnly End)[] Grid =
        Enumerable.Range(1, 5)
            .SelectMany(day => Enumerable.Range(0, 6).Select(period =>
                (day, new TimeOnly(9 + period, 0), new TimeOnly(10 + period, 0))))
            .ToArray();

    // AWA-01, AWA-02
    [HttpPost("timetable/generate")]
    public async Task<ActionResult<List<TimetableSlotDto>>> Generate(GenerateTimetableRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_timetable"))
        {
            return Forbid();
        }

        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);

        // #129: a global (non-HoD) create_timetable holder previously fell back to an
        // attacker-supplied request.DepartmentId with no ownership check, and to *no* filter
        // at all (every section in every college) when that was also omitted. Both paths are
        // now clamped to the caller's own college.
        var departmentScope = await permissions.GetDepartmentScopeAsync(userId);
        if (departmentScope is null && request.DepartmentId is { } requestedDepartmentId)
        {
            var requestedDepartmentCollegeId = await db.Departments
                .Where(d => d.Id == requestedDepartmentId)
                .Select(d => (Guid?)d.CollegeId)
                .FirstOrDefaultAsync();
            if (requestedDepartmentCollegeId != callerCollegeId)
            {
                return Forbid();
            }
            departmentScope = requestedDepartmentId;
        }

        var assignmentsQuery = db.TeacherSectionAssignments
            .Include(a => a.Section)
            .Include(a => a.Subject)
            .Include(a => a.Teacher)
            .AsQueryable();
        if (departmentScope is not null)
        {
            assignmentsQuery = assignmentsQuery.Where(a => a.Section.DepartmentId == departmentScope);
        }
        else
        {
            assignmentsQuery = assignmentsQuery.Where(a => a.Section.Department.CollegeId == callerCollegeId);
        }
        var assignments = await assignmentsQuery.ToListAsync();

        var sectionIds = assignments.Select(a => a.SectionId).Distinct().ToList();
        var existingSlots = await db.TimetableSlots
            .Where(s => sectionIds.Contains(s.SectionId))
            .ToListAsync();

        var negativeFeedbackPairs = (await db.SectionFeedbacks
                .Where(f => sectionIds.Contains(f.SectionId) && f.Rating <= 2)
                .Select(f => new { f.TeacherId, f.SectionId })
                .ToListAsync())
            .Select(f => (f.TeacherId, f.SectionId))
            .ToHashSet();

        // Manually-edited slots persist through regeneration (AWA-03) — remove only the
        // auto-generated ones for sections in scope before recomputing.
        var toRemove = existingSlots.Where(s => !s.ManuallyEdited).ToList();
        db.TimetableSlots.RemoveRange(toRemove);

        // An assignment already satisfied by a manually-edited slot shouldn't get a second,
        // auto-generated slot alongside it. Keyed by (section, subject) rather than also
        // including teacher, because PatchSlot can reassign a manual slot's teacher — if the
        // key included the original teacher, that reassignment would make the original
        // assignment look uncovered and spawn a duplicate auto-generated slot for it.
        var manuallyCoveredAssignments = existingSlots
            .Where(s => s.ManuallyEdited)
            .Select(s => (s.SectionId, s.SubjectId))
            .ToHashSet();

        var occupiedCells = existingSlots
            .Where(s => s.ManuallyEdited)
            .Select(s => (s.SectionId, s.DayOfWeek, s.StartTime))
            .ToHashSet();

        // Teacher-conflict checks must not be limited to sectionIds in scope: a teacher may
        // also teach sections outside this department, and slots there aren't being touched
        // by this generation run (department-scoped regeneration otherwise risks double-
        // booking that teacher into an out-of-scope commitment).
        var relevantTeacherIds = assignments.Select(a => a.TeacherId).Distinct().ToList();
        var teacherBusy = (await db.TimetableSlots
                .Where(s => relevantTeacherIds.Contains(s.TeacherId) && (s.ManuallyEdited || !sectionIds.Contains(s.SectionId)))
                .Select(s => new { s.TeacherId, s.DayOfWeek, s.StartTime })
                .ToListAsync())
            .Select(s => (s.TeacherId, s.DayOfWeek, s.StartTime))
            .ToHashSet();

        // #159: the (Section, Subject) dedup key above is intentionally missing TeacherId
        // (see the comment on manuallyCoveredAssignments), which means a stale/duplicate
        // TeacherSectionAssignment row for the same (section, subject) can get silently
        // treated as "already covered" and never scheduled, with no error surfaced anywhere.
        // Without changing the dedup key itself, at least surface which assignments were
        // skipped for this reason so it's visible instead of silent.
        var skippedAsAlreadyCovered = new List<TeacherSectionAssignment>();

        var newSlots = new List<TimetableSlot>();
        foreach (var assignment in assignments)
        {
            if (negativeFeedbackPairs.Contains((assignment.TeacherId, assignment.SectionId)))
            {
                continue;
            }
            if (manuallyCoveredAssignments.Contains((assignment.SectionId, assignment.SubjectId)))
            {
                skippedAsAlreadyCovered.Add(assignment);
                continue;
            }

            var found = false;
            var cell = (Day: 0, Start: default(TimeOnly), End: default(TimeOnly));
            foreach (var candidate in Grid)
            {
                if (occupiedCells.Contains((assignment.SectionId, candidate.Day, candidate.Start)) ||
                    teacherBusy.Contains((assignment.TeacherId, candidate.Day, candidate.Start)))
                {
                    continue;
                }
                cell = candidate;
                found = true;
                break;
            }
            if (!found)
            {
                continue;
            }

            var slot = new TimetableSlot
            {
                Id = Guid.NewGuid(),
                SectionId = assignment.SectionId,
                SubjectId = assignment.SubjectId,
                TeacherId = assignment.TeacherId,
                DayOfWeek = cell.Day,
                StartTime = cell.Start,
                EndTime = cell.End,
                ManuallyEdited = false,
            };
            newSlots.Add(slot);
            occupiedCells.Add((slot.SectionId, slot.DayOfWeek, slot.StartTime));
            teacherBusy.Add((slot.TeacherId, slot.DayOfWeek, slot.StartTime));
        }

        if (skippedAsAlreadyCovered.Count > 0)
        {
            foreach (var skipped in skippedAsAlreadyCovered)
            {
                logger.LogWarning(
                    "TimetableController.Generate: TeacherSectionAssignment {AssignmentId} (Teacher {TeacherId}, Section {SectionId}, Subject {SubjectId}) was skipped as already covered by a manually-edited slot for that (Section, Subject) pair.",
                    skipped.Id, skipped.TeacherId, skipped.SectionId, skipped.SubjectId);
            }
        }

        db.TimetableSlots.AddRange(newSlots);
        await db.SaveChangesAsync();

        var result = await db.TimetableSlots
            .Where(s => sectionIds.Contains(s.SectionId))
            .Include(s => s.Section)
            .Include(s => s.Subject)
            .Include(s => s.Teacher)
            .OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartTime)
            .ToListAsync();
        return Ok(result.Select(ToDto).ToList());
    }

    // AWA-03
    [HttpPatch("timetable/slots/{id}")]
    public async Task<ActionResult<TimetableSlotDto>> PatchSlot(Guid id, PatchSlotRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_timetable"))
        {
            return Forbid();
        }

        var slot = await db.TimetableSlots
            .Include(s => s.Section).ThenInclude(s => s.Department)
            .Include(s => s.Subject)
            .Include(s => s.Teacher)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (slot is null)
        {
            return NotFound();
        }

        var departmentScope = await permissions.GetDepartmentScopeAsync(userId);
        if (departmentScope is not null)
        {
            if (slot.Section.DepartmentId != departmentScope)
            {
                return Forbid();
            }
        }
        else
        {
            // #129: a global (non-HoD) create_timetable holder's guard previously only ran
            // when departmentScope was non-null, so any such caller could patch any slot in
            // any college by guessing/enumerating slot ids. Clamp to the caller's own college.
            var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
            if (slot.Section.Department.CollegeId != callerCollegeId)
            {
                return Forbid();
            }
        }

        if (request.TeacherId is { } teacherId)
        {
            slot.TeacherId = teacherId;
            slot.Teacher = await db.Users.FindAsync(teacherId) ?? slot.Teacher;
        }
        if (request.DayOfWeek is { } dayOfWeek) slot.DayOfWeek = dayOfWeek;
        if (request.StartTime is { } startTime) slot.StartTime = startTime;
        if (request.EndTime is { } endTime) slot.EndTime = endTime;
        if (request.Room is not null) slot.Room = request.Room;
        slot.ManuallyEdited = true;

        await db.SaveChangesAsync();
        return Ok(ToDto(slot));
    }

    // Admin CRUD for the per-semester teacher/section rotation (teacher_section_assignments)
    // - previously had no production endpoint at all anywhere in this codebase (every
    // reference outside entity/DTO plumbing was test-seed-only). Distinct from
    // subjects/{id}/teachers (SubjectsController) which is the stable, decided-once roster;
    // Create below only allows assigning a teacher already on that roster.
    [HttpGet("timetable/sections/{sectionId}/teacher-assignments")]
    public async Task<ActionResult<List<TeacherSectionAssignmentDto>>> ListTeacherAssignments(Guid sectionId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_timetable"))
        {
            return Forbid();
        }

        var section = await db.Sections.Include(s => s.Department).FirstOrDefaultAsync(s => s.Id == sectionId);
        if (section is null)
        {
            return NotFound();
        }
        if (!await IsInScopeAsync(userId, section.DepartmentId, section.Department.CollegeId))
        {
            return Forbid();
        }

        var assignments = await db.TeacherSectionAssignments
            .Include(a => a.Subject)
            .Include(a => a.Teacher)
            .Where(a => a.SectionId == sectionId)
            .OrderBy(a => a.Subject.Code)
            .Select(a => new TeacherSectionAssignmentDto(a.Id, a.SectionId, a.SubjectId, a.Subject.Code, a.Subject.Name, a.TeacherId, a.Teacher.FullName))
            .ToListAsync();
        return Ok(assignments);
    }

    [HttpPost("timetable/sections/{sectionId}/teacher-assignments")]
    public async Task<ActionResult<TeacherSectionAssignmentDto>> CreateTeacherAssignment(Guid sectionId, CreateTeacherSectionAssignmentRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_timetable"))
        {
            return Forbid();
        }

        var section = await db.Sections.Include(s => s.Department).FirstOrDefaultAsync(s => s.Id == sectionId);
        if (section is null)
        {
            return NotFound();
        }
        if (!await IsInScopeAsync(userId, section.DepartmentId, section.Department.CollegeId))
        {
            return Forbid();
        }

        var subject = await db.Subjects.FirstOrDefaultAsync(s => s.Id == request.SubjectId);
        if (subject is null || subject.DepartmentId != section.DepartmentId)
        {
            return BadRequest("SubjectId must belong to the same department as the section.");
        }

        // Core of "remodel the arch this way": a teacher can only be assigned to teach a
        // subject in a section for this rotation if they're already on that subject's stable
        // roster (subjects/{id}/teachers) - the roster is decided once, this assignment is
        // what changes every semester.
        var onRoster = await db.SubjectTeachers.AnyAsync(st => st.SubjectId == request.SubjectId && st.TeacherId == request.TeacherId);
        if (!onRoster)
        {
            return BadRequest("This teacher is not on the subject's teacher roster. Add them to the roster first.");
        }

        if (await db.TeacherSectionAssignments.AnyAsync(a => a.SectionId == sectionId && a.SubjectId == request.SubjectId && a.TeacherId == request.TeacherId))
        {
            return Conflict("This teacher is already assigned to this subject for this section.");
        }

        var teacher = await db.Users.FindAsync(request.TeacherId);
        var assignment = new TeacherSectionAssignment { Id = Guid.NewGuid(), SectionId = sectionId, SubjectId = request.SubjectId, TeacherId = request.TeacherId };
        db.TeacherSectionAssignments.Add(assignment);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(ListTeacherAssignments), new { sectionId },
            new TeacherSectionAssignmentDto(assignment.Id, sectionId, subject.Id, subject.Code, subject.Name, request.TeacherId, teacher!.FullName));
    }

    [HttpDelete("timetable/sections/{sectionId}/teacher-assignments/{assignmentId}")]
    public async Task<IActionResult> DeleteTeacherAssignment(Guid sectionId, Guid assignmentId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_timetable"))
        {
            return Forbid();
        }

        var section = await db.Sections.Include(s => s.Department).FirstOrDefaultAsync(s => s.Id == sectionId);
        if (section is null)
        {
            return NotFound();
        }
        if (!await IsInScopeAsync(userId, section.DepartmentId, section.Department.CollegeId))
        {
            return Forbid();
        }

        var assignment = await db.TeacherSectionAssignments.FirstOrDefaultAsync(a => a.Id == assignmentId && a.SectionId == sectionId);
        if (assignment is null)
        {
            return NotFound();
        }

        db.TeacherSectionAssignments.Remove(assignment);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Same department-scope-or-caller's-college clamp as PatchSlot above (#129-class check) -
    // factored out here since three new endpoints need it identically.
    private async Task<bool> IsInScopeAsync(Guid userId, Guid targetDepartmentId, Guid targetCollegeId)
    {
        var departmentScope = await permissions.GetDepartmentScopeAsync(userId);
        if (departmentScope is not null)
        {
            return targetDepartmentId == departmentScope;
        }
        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
        return targetCollegeId == callerCollegeId;
    }

    // Phase 7 - admin-facing timetable browsing. timetable/mine below is teacher-personal-
    // schedule scoped (filters by TeacherId) and always returns empty for an Admin account -
    // there was no way for an admin to browse any section's timetable at all before this,
    // only ever re-view whatever Generate last returned in-memory (see TimetablePage's
    // paired frontend fix). Reuses the same IsInScopeAsync department-or-college clamp as
    // the teacher-assignment endpoints above.
    [HttpGet("timetable/sections/{sectionId}")]
    public async Task<ActionResult<List<TimetableSlotDto>>> SectionTimetable(Guid sectionId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_timetable"))
        {
            return Forbid();
        }

        var section = await db.Sections.Include(s => s.Department).FirstOrDefaultAsync(s => s.Id == sectionId);
        if (section is null)
        {
            return NotFound();
        }
        if (!await IsInScopeAsync(userId, section.DepartmentId, section.Department.CollegeId))
        {
            return Forbid();
        }

        var slots = await db.TimetableSlots
            .Where(s => s.SectionId == sectionId)
            .Include(s => s.Section)
            .Include(s => s.Subject)
            .Include(s => s.Teacher)
            .OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartTime)
            .ToListAsync();
        return Ok(slots.Select(ToDto).ToList());
    }

    // TWA-10
    [HttpGet("timetable/mine")]
    public async Task<ActionResult<List<TimetableSlotDto>>> Mine()
    {
        var userId = CurrentUserId();
        var slots = await db.TimetableSlots
            .Where(s => s.TeacherId == userId)
            .Include(s => s.Section)
            .Include(s => s.Subject)
            .Include(s => s.Teacher)
            .OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartTime)
            .ToListAsync();
        return Ok(slots.Select(ToDto).ToList());
    }

    // TWA-13
    [HttpPost("timetable/change-requests")]
    public async Task<ActionResult<ChangeRequestDto>> CreateChangeRequest(CreateChangeRequestRequest request)
    {
        var userId = CurrentUserId();
        var changeRequest = new TimetableChangeRequest
        {
            Id = Guid.NewGuid(),
            TeacherId = userId,
            Description = request.Description,
            Status = "pending",
        };
        db.TimetableChangeRequests.Add(changeRequest);
        await db.SaveChangesAsync();

        // Notification Router (shared) — TWA-13's AC is "Request shows as pending until Admin
        // approves/rejects it"; routing this means Admin finds out a request exists without
        // polling. Fanned out to every Admin in the requesting teacher's own college, same
        // scoping rule as TWA-11's report routing (see AdminRecipients).
        var teacher = await db.Users.FindAsync(userId);
        if (teacher is not null)
        {
            var adminIds = await AdminRecipients.GetCollegeAdminIdsAsync(db, teacher.CollegeId);
            foreach (var adminId in adminIds)
            {
                await notifications.RouteAsync(adminId, NotificationType.TimetableRequest, new
                {
                    changeRequestId = changeRequest.Id,
                    teacherId = userId,
                    teacherName = teacher.FullName,
                    description = changeRequest.Description,
                    requestedAt = changeRequest.RequestedAt,
                });
            }
        }

        return Ok(new ChangeRequestDto(changeRequest.Id, changeRequest.Description, changeRequest.Status, changeRequest.RequestedAt));
    }

    // TWA-08 — roster for the attendance-marking form; scoped the same way as marking
    // itself, so a teacher can only see the section they're assigned to teach this slot -
    // OR, per the class_teacher role, a caller with section-oversight scope for this
    // slot's section (they may not teach this particular period at all, but hold full
    // attendance/marks oversight for the section across every period).
    [HttpGet("timetable/slots/{id}/roster")]
    public async Task<ActionResult<List<RosterStudentDto>>> Roster(Guid id)
    {
        var userId = CurrentUserId();

        var slot = await db.TimetableSlots.FirstOrDefaultAsync(s => s.Id == id);
        if (slot is null)
        {
            return NotFound();
        }
        if (slot.TeacherId != userId && await permissions.GetSectionOversightScopeAsync(userId) != slot.SectionId)
        {
            return Forbid();
        }

        var students = await db.SectionEnrollments
            .Where(e => e.SectionId == slot.SectionId)
            .Include(e => e.Student)
            .OrderBy(e => e.Student.FullName)
            .Select(e => new RosterStudentDto(e.StudentId, e.Student.FullName))
            .ToListAsync();
        return Ok(students);
    }

    // TWA-12 — a teacher rates a section they've taught. Written in the exact shape
    // Generate() above reads for AWA-02 (feedback-based teacher exclusion): one row
    // per submission in section_feedback, keyed by (teacher_id, section_id, rating).
    // No dedicated permission code exists for this in the RBAC catalog (adding one
    // would be an authz-model change requiring the contract-change sign-off per
    // CLAUDE.md); instead this endpoint verifies the caller actually has a
    // TeacherSectionAssignment for the section, which both confirms they're a teacher
    // and that they taught that specific section.
    [HttpPost("timetable/sections/{sectionId}/feedback")]
    public async Task<ActionResult<SectionFeedbackDto>> SubmitSectionFeedback(Guid sectionId, SubmitSectionFeedbackRequest request)
    {
        var userId = CurrentUserId();

        if (request.Rating is < 1 or > 5)
        {
            return BadRequest(new { error = "rating must be between 1 and 5" });
        }

        var taughtSection = await db.TeacherSectionAssignments
            .AnyAsync(a => a.TeacherId == userId && a.SectionId == sectionId);
        if (!taughtSection)
        {
            return Forbid();
        }

        var section = await db.Sections.FindAsync(sectionId);
        if (section is null)
        {
            return NotFound();
        }

        var feedback = new SectionFeedback
        {
            Id = Guid.NewGuid(),
            TeacherId = userId,
            SectionId = sectionId,
            Rating = request.Rating,
            Comments = request.Comments,
            SubmittedAt = DateTime.UtcNow,
        };
        db.SectionFeedbacks.Add(feedback);
        await db.SaveChangesAsync();

        return Ok(new SectionFeedbackDto(feedback.Id, feedback.SectionId, section.Name, feedback.Rating, feedback.Comments, feedback.SubmittedAt));
    }

    // TWA-04: class performance dashboard for a section, reflecting attendance (TWA-08)
    // and internal marks (TWA-16) no older than the last write to either — both are read
    // live here, not cached, so there is no separate "last sync" to go stale. Same
    // TeacherSectionAssignment scoping as TWA-12: the caller must actually teach this
    // section for at least one subject - OR hold class_teacher section-oversight scope
    // for it, in which case every subject taught to the section (by any teacher) is
    // covered, not just the caller's own. Known, disclosed gap: still internal marks
    // only (TWA-16) - external marks (TWA-17/20) aren't in SectionPerformanceSummaryDto's
    // shape yet; adding those is a separate contract change, not folded in here.
    [HttpGet("timetable/sections/{sectionId}/performance-summary")]
    public async Task<ActionResult<SectionPerformanceSummaryDto>> GetSectionPerformanceSummary(Guid sectionId)
    {
        var userId = CurrentUserId();

        var hasSectionOversight = await permissions.GetSectionOversightScopeAsync(userId) == sectionId;
        var subjectIds = hasSectionOversight
            ? await db.TeacherSectionAssignments
                .Where(a => a.SectionId == sectionId)
                .Select(a => a.SubjectId)
                .Distinct()
                .ToListAsync()
            : await db.TeacherSectionAssignments
                .Where(a => a.TeacherId == userId && a.SectionId == sectionId)
                .Select(a => a.SubjectId)
                .ToListAsync();
        if (subjectIds.Count == 0)
        {
            return Forbid();
        }

        var section = await db.Sections.FindAsync(sectionId);
        if (section is null)
        {
            return NotFound();
        }

        var students = await db.SectionEnrollments
            .Where(e => e.SectionId == sectionId)
            .Select(e => e.Student)
            .OrderBy(s => s.FullName)
            .ToListAsync();

        var attendanceRecords = await db.AttendanceRecords
            .Where(r => r.ClassSession.TimetableSlot.SectionId == sectionId)
            .Select(r => new { r.StudentId, r.Status })
            .ToListAsync();

        var studentAttendance = students.Select(s =>
        {
            var records = attendanceRecords.Where(r => r.StudentId == s.Id).ToList();
            var percentage = records.Count == 0
                ? (decimal?)null
                : 100m * records.Count(r => r.Status == AttendanceStatus.Present) / records.Count;
            return new StudentAttendanceDto(s.Id, s.FullName, percentage);
        }).ToList();

        var overallAttendance = attendanceRecords.Count == 0
            ? (decimal?)null
            : 100m * attendanceRecords.Count(r => r.Status == AttendanceStatus.Present) / attendanceRecords.Count;

        var marksBySubject = new List<SubjectMarksSummaryDto>();
        foreach (var subjectId in subjectIds)
        {
            var subject = await db.Subjects.FindAsync(subjectId);
            if (subject is null)
            {
                continue;
            }
            var marks = await db.InternalMarks
                .Where(m => m.SubjectId == subjectId && students.Select(s => s.Id).Contains(m.StudentId))
                .Select(m => m.Marks)
                .ToListAsync();
            marksBySubject.Add(new SubjectMarksSummaryDto(
                subjectId, subject.Name,
                marks.Count == 0 ? null : marks.Average(),
                marks.Count));
        }

        return Ok(new SectionPerformanceSummaryDto(sectionId, section.Name, overallAttendance, studentAttendance, marksBySubject));
    }

    // SDA-12 — the Student Desktop App calls this whenever it loses effective focus or is
    // closed. The check for "is there actually a class session happening right now" lives
    // entirely server-side (ClassSessionLookup); the client doesn't need its own timetable
    // logic, it just always pings on exit/focus-loss and this is a no-op when there's no
    // active session. On a hit, this posts through the Notification Router (shared code —
    // see INotificationRouter) with the exit_ping notification type; the router owns
    // persistence + real-time delivery, this endpoint only decides *whether* and *who*.
    [HttpPost("class-sessions/exit-ping")]
    public async Task<ActionResult<ExitPingResponse>> ExitPing()
    {
        var studentId = CurrentUserId();
        var student = await db.Users.FindAsync(studentId);
        if (student is null || student.AccountType != AccountType.Student)
        {
            return Forbid();
        }

        var active = await ClassSessionLookup.FindOrStartActiveSessionAsync(db, studentId, DateTime.UtcNow);
        if (active is null)
        {
            return Ok(new ExitPingResponse(false, null, null));
        }

        await notifications.RouteAsync(active.TeacherId, NotificationType.ExitPing, new
        {
            studentId,
            studentName = student.FullName,
            classSessionId = active.ClassSessionId,
            sectionId = active.SectionId,
            sectionName = active.SectionName,
            subjectId = active.SubjectId,
            occurredAt = DateTime.UtcNow,
        });

        return Ok(new ExitPingResponse(true, active.ClassSessionId, active.TeacherId));
    }

    // TWA-08
    [HttpPost("attendance")]
    public async Task<ActionResult<MarkAttendanceResponse>> MarkAttendance(MarkAttendanceRequest request)
    {
        var userId = CurrentUserId();

        var caller = await db.Users.FindAsync(userId);
        if (caller is null || caller.AccountType != AccountType.Teacher)
        {
            return Forbid();
        }

        var slot = await db.TimetableSlots.FirstOrDefaultAsync(s => s.Id == request.TimetableSlotId);
        if (slot is null)
        {
            return NotFound();
        }

        // Scoped to the teacher's own assigned section: only the teacher timetabled for
        // this slot may mark attendance for its sessions - OR a class_teacher holding
        // section-oversight scope for it (full oversight includes acting on behalf of the
        // section, not just viewing - same reasoning as Roster above, which is this
        // endpoint's own pre-marking form).
        if (slot.TeacherId != userId && await permissions.GetSectionOversightScopeAsync(userId) != slot.SectionId)
        {
            return Forbid();
        }

        // #152: derive "today" from the college's local time zone, not raw UTC, so marking
        // attendance near local midnight doesn't roll the session date to the wrong day and
        // silently create a duplicate ClassSession for what the roster/UI treats as "today".
        var college = await db.Colleges.FindAsync(caller.CollegeId);
        var sessionDate = request.SessionDate ?? CollegeClock.LocalDate(college, DateTime.UtcNow);
        var session = await db.ClassSessions
            .FirstOrDefaultAsync(s => s.TimetableSlotId == slot.Id && s.SessionDate == sessionDate);
        if (session is null)
        {
            session = new ClassSession
            {
                Id = Guid.NewGuid(),
                TimetableSlotId = slot.Id,
                SessionDate = sessionDate,
                ActualTeacherId = userId,
            };
            db.ClassSessions.Add(session);
        }

        var sectionId = slot.SectionId;
        var enrolledStudentIds = await db.SectionEnrollments
            .Where(e => e.SectionId == sectionId)
            .Select(e => e.StudentId)
            .ToListAsync();
        if (enrolledStudentIds.Count == 0)
        {
            return BadRequest(new { error = "no_enrolled_students", message = "This section has no enrolled students." });
        }

        var entries = request.Entries ?? [];
        var providedIds = entries.Select(e => e.StudentId).ToList();
        if (providedIds.Count != providedIds.Distinct().Count())
        {
            return BadRequest(new { error = "duplicate_student", message = "Each student may only have one attendance entry." });
        }

        var enrolledSet = enrolledStudentIds.ToHashSet();
        var unknown = providedIds.Where(id => !enrolledSet.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            return BadRequest(new { error = "unknown_student", message = "One or more students are not enrolled in this section.", studentIds = unknown });
        }

        // AC: every enrolled student must have a status set after marking completes.
        var providedSet = providedIds.ToHashSet();
        var missing = enrolledStudentIds.Where(id => !providedSet.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            return BadRequest(new { error = "incomplete_attendance", message = "Every enrolled student must have an attendance status.", studentIds = missing });
        }

        var statusByStudent = new Dictionary<Guid, AttendanceStatus>();
        foreach (var entry in entries)
        {
            if (!Enum.TryParse<AttendanceStatus>(entry.Status, ignoreCase: true, out var status))
            {
                return BadRequest(new { error = "invalid_status", message = $"'{entry.Status}' is not a valid attendance status." });
            }
            statusByStudent[entry.StudentId] = status;
        }

        var existingRecords = await db.AttendanceRecords
            .Where(r => r.ClassSessionId == session.Id)
            .ToListAsync();
        var existingByStudent = existingRecords.ToDictionary(r => r.StudentId);

        var now = DateTime.UtcNow;
        foreach (var (studentId, status) in statusByStudent)
        {
            if (existingByStudent.TryGetValue(studentId, out var record))
            {
                record.Status = status;
                record.MarkedAt = now;
                record.MarkedBy = userId;
            }
            else
            {
                db.AttendanceRecords.Add(new AttendanceRecord
                {
                    Id = Guid.NewGuid(),
                    ClassSessionId = session.Id,
                    StudentId = studentId,
                    Status = status,
                    MarkedAt = now,
                    MarkedBy = userId,
                });
            }
        }

        await db.SaveChangesAsync();

        var studentNames = await db.Users
            .Where(u => enrolledSet.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var records = statusByStudent
            .Select(kv => new MarkedAttendanceDto(kv.Key, studentNames.GetValueOrDefault(kv.Key, ""), kv.Value.ToString()))
            .OrderBy(r => r.StudentName)
            .ToList();

        return Ok(new MarkAttendanceResponse(session.Id, session.SessionDate, sectionId, records));
    }

    // TWA-09: below-65% attendance alerts for the teacher's own sections.
    //
    // "Fires only the first time crossing below 65%" is implemented without any new
    // persistence or schema change (the notification_type enum has no matching value for
    // this, and adding one is a contract change requiring sign-off per CLAUDE.md): a
    // student is only included here if their MOST RECENT attendance record is the exact
    // one that dropped their cumulative percentage from >=65% to <65%. Once a later
    // session is marked, that record becomes the new "most recent" one — if the student
    // was already below 65% before it, the crossing condition no longer holds and the
    // alert stops appearing, even though the student remains below 65% overall. This
    // ties the alert to the marking event that caused the crossing rather than to an
    // ongoing state, which is what "fires once" requires.
    private const decimal AttendanceAlertThreshold = 65m;

    [HttpGet("attendance/alerts")]
    public async Task<ActionResult<List<AttendanceAlertDto>>> AttendanceAlerts()
    {
        var userId = CurrentUserId();

        var sectionIds = await db.TimetableSlots
            .Where(s => s.TeacherId == userId)
            .Select(s => s.SectionId)
            .Distinct()
            .ToListAsync();
        if (sectionIds.Count == 0)
        {
            return Ok(new List<AttendanceAlertDto>());
        }

        var sections = await db.Sections
            .Where(sec => sectionIds.Contains(sec.Id))
            .ToDictionaryAsync(sec => sec.Id, sec => sec.Name);

        var alerts = new List<AttendanceAlertDto>();

        foreach (var sectionId in sectionIds)
        {
            var studentIds = await db.SectionEnrollments
                .Where(e => e.SectionId == sectionId)
                .Select(e => e.StudentId)
                .ToListAsync();

            foreach (var studentId in studentIds)
            {
                var records = await db.AttendanceRecords
                    .Where(r => r.StudentId == studentId && r.ClassSession.TimetableSlot.SectionId == sectionId)
                    .OrderBy(r => r.ClassSession.SessionDate).ThenBy(r => r.MarkedAt)
                    .Select(r => r.Status)
                    .ToListAsync();

                if (records.Count == 0)
                {
                    continue;
                }

                var latest = records[^1];
                var withoutLatest = records.Take(records.Count - 1).ToList();

                var pctWith = PercentPresent(records);
                var pctWithout = withoutLatest.Count == 0 ? 100m : PercentPresent(withoutLatest);

                var justCrossed = pctWith < AttendanceAlertThreshold && pctWithout >= AttendanceAlertThreshold;
                if (!justCrossed)
                {
                    continue;
                }

                var student = await db.Users.FindAsync(studentId);
                alerts.Add(new AttendanceAlertDto(studentId, student?.FullName ?? "", sectionId, sections.GetValueOrDefault(sectionId, ""), Math.Round(pctWith, 1)));
            }
        }

        return Ok(alerts);
    }

    private static decimal PercentPresent(List<AttendanceStatus> records) =>
        records.Count == 0 ? 100m : 100m * records.Count(s => s == AttendanceStatus.Present) / records.Count;

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static TimetableSlotDto ToDto(TimetableSlot s) => new(
        s.Id, s.DayOfWeek, s.StartTime, s.EndTime,
        s.SectionId, s.Section.Name,
        s.SubjectId, s.Subject.Name,
        s.TeacherId, s.Teacher.FullName,
        s.Room, s.ManuallyEdited);
}
