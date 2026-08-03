using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Tests.Controllers;

// TWA-11 + Notification Router (shared, #80) — submitting a report routes a Report
// notification to every Admin in the reporting teacher's own college. NotificationRouterTests
// (Services) covers RouteAsync itself; this covers that Create actually calls it, for the
// right recipients, scoped to the right college.
public class ReportsControllerTests
{
    private class RecordingNotificationRouter : INotificationRouter
    {
        public List<(Guid RecipientId, NotificationType Type)> Routed { get; } = new();

        public Task<Notification> RouteAsync(Guid recipientId, NotificationType type, object payload, CancellationToken cancellationToken = default)
        {
            Routed.Add((recipientId, type));
            return Task.FromResult(new Notification { Id = Guid.NewGuid(), RecipientId = recipientId, Type = type });
        }
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static User NewUser(Guid collegeId, AccountType accountType) => new()
    {
        Id = Guid.NewGuid(),
        CollegeId = collegeId,
        Identifier = $"user-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        FullName = "Test User",
        AccountType = accountType,
        IsActive = true,
    };

    private static ReportsController ControllerAs(AppDbContext db, User user, RecordingNotificationRouter router)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new ReportsController(db, router, new CollegeScopeService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private static async Task SeedAdminRoleAsync(AppDbContext db)
    {
        if (!await db.Roles.AnyAsync(r => r.Code == "admin"))
        {
            db.Roles.Add(new Role { Code = "admin" });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Create_NotifiesEveryAdminInTheTeachersCollege()
    {
        await using var db = NewDb();
        await SeedAdminRoleAsync(db);
        var collegeId = Guid.NewGuid();
        var teacher = NewUser(collegeId, AccountType.Teacher);
        var admin1 = NewUser(collegeId, AccountType.AdminTier);
        var admin2 = NewUser(collegeId, AccountType.AdminTier);
        var student = NewUser(collegeId, AccountType.Student);
        db.Users.AddRange(teacher, admin1, admin2, student);
        db.RoleBindings.AddRange(
            new RoleBinding { Id = Guid.NewGuid(), UserId = teacher.Id, RoleCode = "lecturer", GrantedAt = DateTime.UtcNow },
            new RoleBinding { Id = Guid.NewGuid(), UserId = admin1.Id, RoleCode = "admin", GrantedAt = DateTime.UtcNow },
            new RoleBinding { Id = Guid.NewGuid(), UserId = admin2.Id, RoleCode = "admin", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var router = new RecordingNotificationRouter();
        var controller = ControllerAs(db, teacher, router);

        var result = await controller.Create(new CreateReportRequest(null, student.Id, "Suspicious activity in class"));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(2, router.Routed.Count);
        Assert.All(router.Routed, r => Assert.Equal(NotificationType.Report, r.Type));
        Assert.Contains(router.Routed, r => r.RecipientId == admin1.Id);
        Assert.Contains(router.Routed, r => r.RecipientId == admin2.Id);
    }

    [Fact]
    public async Task Create_DoesNotNotifyAdminsInAnotherCollege()
    {
        await using var db = NewDb();
        await SeedAdminRoleAsync(db);
        var collegeId = Guid.NewGuid();
        var otherCollegeId = Guid.NewGuid();
        var teacher = NewUser(collegeId, AccountType.Teacher);
        var localAdmin = NewUser(collegeId, AccountType.AdminTier);
        var otherCollegeAdmin = NewUser(otherCollegeId, AccountType.AdminTier);
        var student = NewUser(collegeId, AccountType.Student);
        db.Users.AddRange(teacher, localAdmin, otherCollegeAdmin, student);
        db.RoleBindings.AddRange(
            new RoleBinding { Id = Guid.NewGuid(), UserId = teacher.Id, RoleCode = "lecturer", GrantedAt = DateTime.UtcNow },
            new RoleBinding { Id = Guid.NewGuid(), UserId = localAdmin.Id, RoleCode = "admin", GrantedAt = DateTime.UtcNow },
            new RoleBinding { Id = Guid.NewGuid(), UserId = otherCollegeAdmin.Id, RoleCode = "admin", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var router = new RecordingNotificationRouter();
        var controller = ControllerAs(db, teacher, router);

        await controller.Create(new CreateReportRequest(null, student.Id, "Report content"));

        var routed = Assert.Single(router.Routed);
        Assert.Equal(localAdmin.Id, routed.RecipientId);
    }

    [Fact]
    public async Task Create_ForbidsCallerWithoutTeacherRole()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var student = NewUser(collegeId, AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var router = new RecordingNotificationRouter();
        var controller = ControllerAs(db, student, router);

        var result = await controller.Create(new CreateReportRequest(null, null, "content"));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(router.Routed);
    }

    // #24: StudentId was never checked against the caller's own college — a lecturer at
    // College A could file a permanent disciplinary report against a College B student.
    [Fact]
    public async Task Issue24_Create_ForbidsCrossCollegeStudentTarget()
    {
        await using var db = NewDb();
        await SeedAdminRoleAsync(db);
        var collegeId = Guid.NewGuid();
        var teacher = NewUser(collegeId, AccountType.Teacher);
        var otherCollegeStudent = NewUser(Guid.NewGuid(), AccountType.Student);
        db.Users.AddRange(teacher, otherCollegeStudent);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = teacher.Id, RoleCode = "lecturer", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var router = new RecordingNotificationRouter();
        var controller = ControllerAs(db, teacher, router);

        var result = await controller.Create(new CreateReportRequest(null, otherCollegeStudent.Id, "Cross-college report"));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.TeacherReports.ToListAsync());
        Assert.Empty(router.Routed);
    }

    // #24: same scoping rule for SectionId.
    [Fact]
    public async Task Issue24_Create_ForbidsCrossCollegeSectionTarget()
    {
        await using var db = NewDb();
        await SeedAdminRoleAsync(db);
        var collegeId = Guid.NewGuid();
        var teacher = NewUser(collegeId, AccountType.Teacher);
        var otherDepartment = new Department { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "CS" };
        var otherSection = new Section { Id = Guid.NewGuid(), DepartmentId = otherDepartment.Id, Year = 1, Name = "A" };
        db.Users.Add(teacher);
        db.Departments.Add(otherDepartment);
        db.Sections.Add(otherSection);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = teacher.Id, RoleCode = "lecturer", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var router = new RecordingNotificationRouter();
        var controller = ControllerAs(db, teacher, router);

        var result = await controller.Create(new CreateReportRequest(otherSection.Id, null, "Cross-college report"));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.TeacherReports.ToListAsync());
    }

    // #13: List() previously had no Where clause at all — any "admin" role holder, at any
    // college, could read every teacher report platform-wide.
    [Fact]
    public async Task Issue13_List_OnlyReturnsReportsFromTheAdminsOwnCollege()
    {
        await using var db = NewDb();
        await SeedAdminRoleAsync(db);
        var collegeId = Guid.NewGuid();
        var otherCollegeId = Guid.NewGuid();
        var localAdmin = NewUser(collegeId, AccountType.AdminTier);
        var localTeacher = NewUser(collegeId, AccountType.Teacher);
        var localStudent = NewUser(collegeId, AccountType.Student);
        var otherTeacher = NewUser(otherCollegeId, AccountType.Teacher);
        var otherStudent = NewUser(otherCollegeId, AccountType.Student);
        db.Users.AddRange(localAdmin, localTeacher, localStudent, otherTeacher, otherStudent);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = localAdmin.Id, RoleCode = "admin", GrantedAt = DateTime.UtcNow });
        db.TeacherReports.AddRange(
            new TeacherReport { Id = Guid.NewGuid(), TeacherId = localTeacher.Id, StudentId = localStudent.Id, Content = "Local report", SubmittedAt = DateTime.UtcNow },
            new TeacherReport { Id = Guid.NewGuid(), TeacherId = otherTeacher.Id, StudentId = otherStudent.Id, Content = "Other college report", SubmittedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, localAdmin, new RecordingNotificationRouter());
        var result = await controller.List();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var reports = Assert.IsType<List<TeacherReportDto>>(ok.Value);
        var report = Assert.Single(reports);
        Assert.Equal("Local report", report.Content);
    }
}
