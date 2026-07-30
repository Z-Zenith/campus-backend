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
[Route("api/v1/subjects")]
[Authorize]
public class SubjectsController(AppDbContext db, IPermissionService permissions, ICollegeScopeService collegeScope) : ControllerBase
{
    // No dedicated feature ID or permission code existed for subject management before this
    // (only SDA-18's student-facing Mine() below) — reusing manage_departments rather than
    // adding a new permission code, since a subject always belongs to a department and the
    // same admin who manages departments is the natural owner. Gated on it the same way
    // RolesController gates department create/list/edit.

    // Admin-facing list (distinct from Mine() below, which is student-facing and scoped to
    // the caller's own enrolled sections). College-scoped via the department join, optional
    // departmentId filter for a department-scoped view (e.g. from SubjectsPage after picking
    // a department).
    [HttpGet]
    public async Task<ActionResult<List<SubjectDto>>> List(Guid? departmentId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
        var query = db.Subjects
            .Include(s => s.Department)
            .Include(s => s.Teacher) // the subject coordinator - see SubjectDto.CoordinatorId's doc comment
            .Where(s => s.Department.CollegeId == callerCollegeId);
        if (departmentId is { } deptId)
        {
            query = query.Where(s => s.DepartmentId == deptId);
        }

        var subjects = await query.OrderBy(s => s.Department.Name).ThenBy(s => s.Code).ToListAsync();
        return Ok(subjects.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<SubjectDto>> Create(CreateSubjectRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Code and name are required.");
        }

        var department = await db.Departments.FindAsync(request.DepartmentId);
        if (department is null)
        {
            return BadRequest("Unknown department.");
        }
        // #126/#127-class check: a manage_departments holder must not be able to create a
        // subject inside another college's department.
        if (!await collegeScope.IsSameCollegeAsync(userId, department.CollegeId))
        {
            return Forbid();
        }

        if (request.CoordinatorId is { } coordinatorId)
        {
            var coordinatorError = await ValidateCoordinatorAsync(coordinatorId, department.CollegeId);
            if (coordinatorError is not null)
            {
                return coordinatorError;
            }
        }

        if (await db.Subjects.AnyAsync(s => s.DepartmentId == request.DepartmentId && s.Code == request.Code))
        {
            return Conflict("A subject with this code already exists in this department.");
        }

        var subject = new Subject
        {
            Id = Guid.NewGuid(),
            DepartmentId = request.DepartmentId,
            Code = request.Code,
            Name = request.Name,
            TeacherId = request.CoordinatorId,
        };
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        subject.Department = department;
        if (request.CoordinatorId is not null)
        {
            subject.Teacher = await db.Users.FindAsync(request.CoordinatorId);
        }
        return CreatedAtAction(nameof(List), null, ToDto(subject));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<SubjectDto>> Update(Guid id, UpdateSubjectRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Code and name are required.");
        }

        var subject = await db.Subjects.Include(s => s.Department).Include(s => s.Teacher).FirstOrDefaultAsync(s => s.Id == id);
        if (subject is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, subject.Department.CollegeId))
        {
            return Forbid();
        }

        if (request.CoordinatorId is { } coordinatorId)
        {
            var coordinatorError = await ValidateCoordinatorAsync(coordinatorId, subject.Department.CollegeId);
            if (coordinatorError is not null)
            {
                return coordinatorError;
            }
        }

        if (await db.Subjects.AnyAsync(s => s.Id != id && s.DepartmentId == subject.DepartmentId && s.Code == request.Code))
        {
            return Conflict("A subject with this code already exists in this department.");
        }

        subject.Code = request.Code;
        subject.Name = request.Name;
        subject.TeacherId = request.CoordinatorId;
        await db.SaveChangesAsync();

        subject.Teacher = request.CoordinatorId is not null ? await db.Users.FindAsync(request.CoordinatorId) : null;
        return Ok(ToDto(subject));
    }

    // Deliberately checks every dependent table itself rather than relying on the DB to
    // reject the delete: most subject_id foreign keys in the schema are ON DELETE CASCADE
    // (teacher_section_assignments, internal_marks, external_marks, assignments) — letting
    // SaveChangesAsync run unchecked would silently cascade-wipe historical marks and
    // assignments instead of failing safely. Only timetable_slots is ON DELETE RESTRICT, so
    // that's the one case a raw delete would actually catch on its own.
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var subject = await db.Subjects.Include(s => s.Department).FirstOrDefaultAsync(s => s.Id == id);
        if (subject is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, subject.Department.CollegeId))
        {
            return Forbid();
        }

        var inUse = await db.TeacherSectionAssignments.AnyAsync(a => a.SubjectId == id)
            || await db.InternalMarks.AnyAsync(m => m.SubjectId == id)
            || await db.ExternalMarks.AnyAsync(m => m.SubjectId == id)
            || await db.Assignments.AnyAsync(a => a.SubjectId == id)
            || await db.TimetableSlots.AnyAsync(t => t.SubjectId == id);
        if (inUse)
        {
            return Conflict("This subject is in use (assignments, marks, or a timetable) and cannot be deleted.");
        }

        db.Subjects.Remove(subject);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Stable teacher roster (subject_teachers) - "who teaches this subject," decided once
    // and rarely changed. See TimetableController's teacher-section-assignment endpoints for
    // the per-semester rotation that draws from this roster.
    [HttpGet("{id}/teachers")]
    public async Task<ActionResult<List<SubjectTeacherDto>>> ListTeachers(Guid id)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var subject = await db.Subjects.Include(s => s.Department).FirstOrDefaultAsync(s => s.Id == id);
        if (subject is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, subject.Department.CollegeId))
        {
            return Forbid();
        }

        var roster = await db.SubjectTeachers
            .Include(st => st.Teacher)
            .Where(st => st.SubjectId == id)
            .OrderBy(st => st.Teacher.FullName)
            .Select(st => new SubjectTeacherDto(st.Id, st.SubjectId, st.TeacherId, st.Teacher.FullName))
            .ToListAsync();
        return Ok(roster);
    }

    [HttpPost("{id}/teachers")]
    public async Task<ActionResult<SubjectTeacherDto>> AddTeacher(Guid id, AddSubjectTeacherRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var subject = await db.Subjects.Include(s => s.Department).FirstOrDefaultAsync(s => s.Id == id);
        if (subject is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, subject.Department.CollegeId))
        {
            return Forbid();
        }

        var teacher = await db.Users.FindAsync(request.TeacherId);
        if (teacher is null || teacher.AccountType != AccountType.Teacher)
        {
            return BadRequest("TeacherId must belong to an existing Teacher account.");
        }
        if (teacher.CollegeId != subject.Department.CollegeId)
        {
            return BadRequest("The teacher must belong to the subject's college.");
        }
        if (await db.SubjectTeachers.AnyAsync(st => st.SubjectId == id && st.TeacherId == request.TeacherId))
        {
            return Conflict("This teacher is already on the subject's roster.");
        }

        var entry = new SubjectTeacher { Id = Guid.NewGuid(), SubjectId = id, TeacherId = request.TeacherId };
        db.SubjectTeachers.Add(entry);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(ListTeachers), new { id }, new SubjectTeacherDto(entry.Id, id, teacher.Id, teacher.FullName));
    }

    // Blocked if the teacher currently holds a live TeacherSectionAssignment for this subject
    // (same "check dependents before removing" pattern as Subject.Delete above) - a section's
    // current-semester assignment must be removed first, rather than silently orphaning it.
    [HttpDelete("{id}/teachers/{teacherId}")]
    public async Task<IActionResult> RemoveTeacher(Guid id, Guid teacherId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var subject = await db.Subjects.Include(s => s.Department).FirstOrDefaultAsync(s => s.Id == id);
        if (subject is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, subject.Department.CollegeId))
        {
            return Forbid();
        }

        var entry = await db.SubjectTeachers.FirstOrDefaultAsync(st => st.SubjectId == id && st.TeacherId == teacherId);
        if (entry is null)
        {
            return NotFound();
        }

        var hasActiveAssignment = await db.TeacherSectionAssignments
            .AnyAsync(a => a.SubjectId == id && a.TeacherId == teacherId);
        if (hasActiveAssignment)
        {
            return Conflict("This teacher currently has a section assignment for this subject and cannot be removed from the roster until that's reassigned.");
        }

        db.SubjectTeachers.Remove(entry);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<ObjectResult?> ValidateCoordinatorAsync(Guid coordinatorId, Guid departmentCollegeId)
    {
        var coordinator = await db.Users.FindAsync(coordinatorId);
        if (coordinator is null || coordinator.AccountType != AccountType.Teacher)
        {
            return BadRequest("CoordinatorId must belong to an existing Teacher account.");
        }
        if (coordinator.CollegeId != departmentCollegeId)
        {
            return BadRequest("The coordinator must belong to the department's college.");
        }
        return null;
    }

    private static SubjectDto ToDto(Subject s) => new(
        s.Id, s.DepartmentId, s.Department.Name, s.Code, s.Name, s.TeacherId, s.Teacher?.FullName);
    // ^ Subject.TeacherId/Teacher map to CoordinatorId/CoordinatorName - see SubjectDto's doc comment.


    // SDA-18: course + teacher info for every subject taught to a section the caller is
    // enrolled in. There's no independent section-curriculum table in this schema — a
    // subject only "belongs" to a section via a TeacherSectionAssignment row, so that's
    // also this endpoint's definition of "enrolled subject" (a subject a section is meant
    // to take but hasn't yet been staffed with a teacher has no representation here; fixing
    // that would need a schema change, out of SDA-18's scope).
    //
    // Teacher preference: Subject.TeacherId is treated as the canonical assigned teacher
    // elsewhere (AssignmentsController.cs gates assignment-creation on it and attributes
    // assignments to it), so this endpoint prefers the same field for consistency — a
    // student shouldn't be told a different teacher here than the one assignments for the
    // same subject come from. It falls back to the TeacherSectionAssignment row's own
    // TeacherId only when Subject.TeacherId is null, since that field is nullable but the
    // acceptance criterion requires a non-empty teacher-info entry for every result here.
    [HttpGet("mine")]
    public async Task<ActionResult<List<MySubjectDto>>> Mine()
    {
        var studentId = CurrentUserId();

        var sectionIds = await db.SectionEnrollments
            .Where(e => e.StudentId == studentId)
            .Select(e => e.SectionId)
            .ToListAsync();

        var assignments = await db.TeacherSectionAssignments
            .Where(a => sectionIds.Contains(a.SectionId))
            .Select(a => new
            {
                a.SubjectId,
                a.Subject.Code,
                a.Subject.Name,
                SubjectTeacherId = a.Subject.TeacherId,
                SubjectTeacherName = a.Subject.Teacher != null ? a.Subject.Teacher.FullName : null,
                AssignmentTeacherId = a.TeacherId,
                AssignmentTeacherName = a.Teacher.FullName,
            })
            .ToListAsync();

        // #159: Distinct() used to run on this anonymous projection, which still carries
        // AssignmentTeacherId/AssignmentTeacherName per TeacherSectionAssignment row — two
        // sections that both teach the same subject via different assignment-level teachers
        // produced two "distinct" rows that only collapse to the same teacher after the
        // SubjectTeacherId ?? AssignmentTeacherId fallback below. Apply Distinct() to the
        // final MySubjectDto shape instead (it's a record, so this is structural equality):
        // that collapses true duplicates — same subject, same final teacher after the
        // fallback — while still keeping legitimate co-teaching entries (same subject,
        // different final teacher, e.g. Subject.TeacherId unset with two
        // TeacherSectionAssignment rows for different teachers) separate.
        var subjects = assignments
            .Select(a => new MySubjectDto(
                a.SubjectId,
                a.Code,
                a.Name,
                a.SubjectTeacherId ?? a.AssignmentTeacherId,
                a.SubjectTeacherName ?? a.AssignmentTeacherName))
            .Distinct()
            .ToList();

        return Ok(subjects);
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
