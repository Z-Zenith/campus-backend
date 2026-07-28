using System.Security.Claims;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Tests.Controllers;

public class MeControllerTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        CollegeId = Guid.NewGuid(),
        Identifier = $"user-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        FullName = "Test User",
        AccountType = AccountType.Teacher,
        IsActive = true,
    };

    private static MeController ControllerAs(AppDbContext db, Guid userId) => new(new PermissionService(db))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth")),
            },
        },
    };

    [Fact]
    public async Task GetCapabilities_ReturnsEmptyList_ForCallerWithNoAdminPermissions()
    {
        await using var db = NewDb();
        var teacher = NewUser();
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher.Id);
        var result = await controller.GetCapabilities();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<Contracts.MeCapabilitiesResponse>(ok.Value);
        Assert.Empty(response.Permissions);
    }

    // Only the codes the caller actually holds should come back — not the full catalog,
    // and not codes outside AdminCapabilityPermissions.Codes even if granted.
    [Fact]
    public async Task GetCapabilities_ReturnsOnlyHeldCodes_FromTheAdminCapabilitySet()
    {
        await using var db = NewDb();
        var admin = NewUser();
        db.Users.Add(admin);
        db.PermissionGrants.AddRange(
            new PermissionGrant { Id = Guid.NewGuid(), UserId = admin.Id, PermissionCode = "manage_accounts", Granted = true, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow },
            new PermissionGrant { Id = Guid.NewGuid(), UserId = admin.Id, PermissionCode = "reset_password", Granted = true, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow },
            // A permission outside the admin-capability set entirely — must not leak into the response.
            new PermissionGrant { Id = Guid.NewGuid(), UserId = admin.Id, PermissionCode = "add_internal_marks", Granted = true, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.GetCapabilities();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<Contracts.MeCapabilitiesResponse>(ok.Value);
        Assert.Equal(["manage_accounts", "reset_password"], response.Permissions.OrderBy(p => p));
    }

    // AWA-13's acceptance criterion ("revoked/expired override stops applying without
    // logout") is what makes this endpoint safe to poll live rather than cache for a
    // session — a revoked grant must disappear from the very next call.
    [Fact]
    public async Task GetCapabilities_ExcludesExpiredPermissionGrant()
    {
        await using var db = NewDb();
        var admin = NewUser();
        db.Users.Add(admin);
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(),
            UserId = admin.Id,
            PermissionCode = "manage_fees",
            Granted = true,
            GrantedBy = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.GetCapabilities();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<Contracts.MeCapabilitiesResponse>(ok.Value);
        Assert.Empty(response.Permissions);
    }
}
