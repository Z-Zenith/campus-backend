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
// isn't extended casually), so access is gated directly off role_bindings via
// IAppAuthorizationService.HasAnyRoleAsync, mirroring how AuthorizationService.GetDepartmentScopeAsync
// checks the "hod" role code directly.
[ApiController]
[Route("api/v1/reports")]
[Authorize]
public class ReportsController(AppDbContext db, INotificationRouter notifications, IAppAuthorizationService permissions) : ControllerBase
{
    private static readonly string[] TeacherRoleCodes = ["lecturer", "hod"];

    // TWA-11
    [HttpPost]
    public async Task<ActionResult<TeacherReportDto>> Create(CreateReportRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasAnyRoleAsync(userId, TeacherRoleCodes))
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
        if (!await permissions.HasAnyRoleAsync(userId, "admin"))
        {
            return Forbid();
        }

        var reports = await db.TeacherReports
            .Include(r => r.Teacher)
            .Include(r => r.Section)
            .Include(r => r.Student)
            .OrderByDescending(r => r.SubmittedAt)
            .ToListAsync();
        return Ok(reports.Select(ToDto).ToList());
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static TeacherReportDto ToDto(TeacherReport r) => new(
        r.Id, r.TeacherId, r.Teacher.FullName,
        r.SectionId, r.Section?.Name,
        r.StudentId, r.Student?.FullName,
        r.Content, r.SubmittedAt);
}
