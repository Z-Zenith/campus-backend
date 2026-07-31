using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
        return new ReportsController(db, router, new AuthorizationService(db, NullLogger<AuthorizationService>.Instance))
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
}
