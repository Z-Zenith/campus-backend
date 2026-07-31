using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// Academic Calendar work (queued Phase 8) - admin-facing exam scheduling, genuinely
// unscoped before this (no feature ID, no table). Gated create_timetable and scoped the
// same way as TimetableController's admin endpoints (department-scope HoD, or caller's
// college for a global holder) since this is the same kind of section-level scheduling
// authority, not a new permission tier.
[ApiController]
[Route("api/v1")]
[Authorize]
public class ExamSchedulesController(AppDbContext db, IPermissionService permissions, ICollegeScopeService collegeScope, IHolidayService holidays) : ControllerBase
{
    // Read-only section picker for the exam-schedule form above - see SectionDto's doc
    // comment for why this isn't full Section CRUD.
    [HttpGet("departments/{departmentId}/sections")]
    public async Task<ActionResult<List<SectionDto>>> ListSections(Guid departmentId)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_timetable"))
        {
            return Forbid();
        }

        var department = await db.Departments.FindAsync(departmentId);
        if (department is null)
        {
            return NotFound();
        }
        if (!await IsInScopeAsync(userId, departmentId, department.CollegeId))
        {
            return Forbid();
        }

        var sections = await db.Sections
            .Where(s => s.DepartmentId == departmentId)
            .OrderBy(s => s.Year).ThenBy(s => s.Name)
            .Select(s => new SectionDto(s.Id, s.DepartmentId, s.Year, s.Name))
            .ToListAsync();
        return Ok(sections);
    }

    [HttpGet("sections/{sectionId}/exam-schedules")]
    public async Task<ActionResult<List<ExamScheduleDto>>> List(Guid sectionId)
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

        var schedules = await db.ExamSchedules
            .Include(e => e.Subject)
            .Where(e => e.SectionId == sectionId)
            .OrderBy(e => e.ExamDate).ThenBy(e => e.StartTime)
            .ToListAsync();
        return Ok(schedules.Select(ToDto).ToList());
    }

    [HttpPost("sections/{sectionId}/exam-schedules")]
    public async Task<ActionResult<ExamScheduleDto>> Create(Guid sectionId, CreateExamScheduleRequest request)
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

        if (request.EndTime <= request.StartTime)
        {
            return BadRequest("EndTime must be after StartTime.");
        }
        if (await holidays.IsHolidayAsync(section.Department.CollegeId, request.ExamDate))
        {
            return BadRequest("ExamDate falls on a college holiday.");
        }

        if (await db.ExamSchedules.AnyAsync(e => e.SectionId == sectionId && e.SubjectId == request.SubjectId && e.ExamType == request.ExamType))
        {
            return Conflict("An exam schedule for this subject, section, and exam type already exists.");
        }

        var schedule = new ExamSchedule
        {
            Id = Guid.NewGuid(),
            SectionId = sectionId,
            SubjectId = request.SubjectId,
            ExamType = request.ExamType,
            ExamDate = request.ExamDate,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            Room = request.Room,
            CreatedBy = userId,
        };
        db.ExamSchedules.Add(schedule);
        await db.SaveChangesAsync();

        schedule.Subject = subject;
        return CreatedAtAction(nameof(List), new { sectionId }, ToDto(schedule));
    }

    [HttpPut("exam-schedules/{id}")]
    public async Task<ActionResult<ExamScheduleDto>> Update(Guid id, UpdateExamScheduleRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_timetable"))
        {
            return Forbid();
        }

        var schedule = await db.ExamSchedules
            .Include(e => e.Subject)
            .Include(e => e.Section).ThenInclude(s => s.Department)
            .FirstOrDefaultAsync(e => e.Id == id);
        if (schedule is null)
        {
            return NotFound();
        }
        if (!await IsInScopeAsync(userId, schedule.Section.DepartmentId, schedule.Section.Department.CollegeId))
        {
            return Forbid();
        }

        if (request.EndTime <= request.StartTime)
        {
            return BadRequest("EndTime must be after StartTime.");
        }
        if (await holidays.IsHolidayAsync(schedule.Section.Department.CollegeId, request.ExamDate))
        {
            return BadRequest("ExamDate falls on a college holiday.");
        }

        schedule.ExamDate = request.ExamDate;
        schedule.StartTime = request.StartTime;
        schedule.EndTime = request.EndTime;
        schedule.Room = request.Room;
        await db.SaveChangesAsync();

        return Ok(ToDto(schedule));
    }

    [HttpDelete("exam-schedules/{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "create_timetable"))
        {
            return Forbid();
        }

        var schedule = await db.ExamSchedules
            .Include(e => e.Section).ThenInclude(s => s.Department)
            .FirstOrDefaultAsync(e => e.Id == id);
        if (schedule is null)
        {
            return NotFound();
        }
        if (!await IsInScopeAsync(userId, schedule.Section.DepartmentId, schedule.Section.Department.CollegeId))
        {
            return Forbid();
        }

        db.ExamSchedules.Remove(schedule);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Same department-scope-or-caller's-college clamp as TimetableController's admin
    // endpoints (#129-class check).
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

    private static ExamScheduleDto ToDto(ExamSchedule e) => new(
        e.Id, e.SectionId, e.SubjectId, e.Subject.Code, e.Subject.Name, e.ExamType, e.ExamDate, e.StartTime, e.EndTime, e.Room);

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
}
