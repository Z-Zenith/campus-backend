using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

// TWA-11: a teacher submits a report about a section or student, routed to Admin.
// There's no dedicated permission code for this (see db/init/02_seed_roles_and_permissions.sql's
// anti-drift note — the permission catalog mirrors architecture doc Section 9 verbatim and
// isn't extended casually), so access is gated directly off role_bindings, mirroring how
// PermissionService.GetDepartmentScopeAsync checks the "hod" role code directly.
[ApiController]
[Route("api/v1/reports")]
[Authorize]
public class ReportsController(AppDbContext db, INotificationRouter notifications, ICollegeScopeService collegeScope) : ControllerBase
{
    private static readonly string[] TeacherRoleCodes = ["lecturer", "hod"];

    // TWA-11
    [HttpPost]
    public async Task<ActionResult<TeacherReportDto>> Create(CreateReportRequest request)
    {
        var userId = CurrentUserId();
        if (!await HasAnyRoleAsync(userId, TeacherRoleCodes))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { error = "content is required" });
        }
        if (request.SectionId is null && request.StudentId is null)
        {
            return BadRequest(new { error = "sectionId or studentId is required" });
        }

        // #24: neither StudentId nor SectionId was ever checked against the caller's own
        // college — a lecturer at College A could file a permanent disciplinary report
        // against a College B student/section, which then surfaces in that student's own
        // profile (UsersController.GetProfile).
        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
        if (request.StudentId is { } studentId)
        {
            var studentCollegeId = await db.Users
                .Where(u => u.Id == studentId)
                .Select(u => (Guid?)u.CollegeId)
                .FirstOrDefaultAsync();
            if (studentCollegeId is null)
            {
                return BadRequest(new { error = "unknown_student", message = "No student exists with that id." });
            }
            if (studentCollegeId != callerCollegeId)
            {
                return Forbid();
            }
        }
        if (request.SectionId is { } sectionId)
        {
            var sectionCollegeId = await db.Sections
                .Where(s => s.Id == sectionId)
                .Select(s => (Guid?)s.Department.CollegeId)
                .FirstOrDefaultAsync();
            if (sectionCollegeId is null)
            {
                return BadRequest(new { error = "unknown_section", message = "No section exists with that id." });
            }
            if (sectionCollegeId != callerCollegeId)
            {
                return Forbid();
            }
        }

        var report = new TeacherReport
        {
            Id = Guid.NewGuid(),
            TeacherId = userId,
            SectionId = request.SectionId,
            StudentId = request.StudentId,
            Content = request.Content,
            SubmittedAt = DateTime.UtcNow,
        };
        db.TeacherReports.Add(report);
        await db.SaveChangesAsync();

        var saved = await db.TeacherReports
            .Include(r => r.Teacher)
            .Include(r => r.Section)
            .Include(r => r.Student)
            .FirstAsync(r => r.Id == report.Id);

        // Notification Router (shared) — TWA-11's own AC is "Admin's inbox shows the report",
        // which List() below already satisfies by itself; routing a notification on top just
        // means Admin doesn't have to poll that inbox to find out a new report landed. Fanned
        // out to every Admin in the reporting teacher's own college (not institution-wide) —
        // see AdminRecipients for why role_bindings needs a join to scope that.
        var adminIds = await AdminRecipients.GetCollegeAdminIdsAsync(db, saved.Teacher.CollegeId);
        foreach (var adminId in adminIds)
        {
            await notifications.RouteAsync(adminId, NotificationType.Report, new
            {
                reportId = report.Id,
                teacherId = report.TeacherId,
                teacherName = saved.Teacher.FullName,
                sectionId = report.SectionId,
                sectionName = saved.Section?.Name,
                studentId = report.StudentId,
                studentName = saved.Student?.FullName,
                submittedAt = report.SubmittedAt,
            });
        }

        return Ok(ToDto(saved));
    }

    // TWA-11 — Admin inbox
    [HttpGet]
    public async Task<ActionResult<List<TeacherReportDto>>> List()
    {
        var userId = CurrentUserId();
        if (!await HasAnyRoleAsync(userId, ["admin"]))
        {
            return Forbid();
        }

        // #13: no Where clause at all previously — any "admin" role holder, at any college,
        // could read every teacher report platform-wide. Scoped to the reporting teacher's
        // own college, matching the scoping already applied to this same report's
        // notification fan-out in Create() above (AdminRecipients.GetCollegeAdminIdsAsync).
        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
        var reports = await db.TeacherReports
            .Include(r => r.Teacher)
            .Include(r => r.Section)
            .Include(r => r.Student)
            .Where(r => r.Teacher.CollegeId == callerCollegeId)
            .OrderByDescending(r => r.SubmittedAt)
            .ToListAsync();
        return Ok(reports.Select(ToDto).ToList());
    }

    private async Task<bool> HasAnyRoleAsync(Guid userId, IReadOnlyCollection<string> roleCodes) =>
        await db.RoleBindings.AnyAsync(b => b.UserId == userId && roleCodes.Contains(b.RoleCode));

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static TeacherReportDto ToDto(TeacherReport r) => new(
        r.Id, r.TeacherId, r.Teacher.FullName,
        r.SectionId, r.Section?.Name,
        r.StudentId, r.Student?.FullName,
        r.Content, r.SubmittedAt);
}
