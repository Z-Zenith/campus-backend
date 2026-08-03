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

public class RolesControllerTests
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

    private static RolesController ControllerAs(AppDbContext db, Guid userId) => new(db, new PermissionService(db), new CollegeScopeService(db))
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

    private static async Task SeedRolesAndPermissionsAsync(AppDbContext db)
    {
        var manageRolesPermission = new Permission { Code = "manage_roles_and_permissions", Description = "x" };
        var createTimetablePermission = new Permission { Code = "create_timetable", Description = "x" };
        var admin = new Role { Code = "admin" };
        admin.PermissionCodes.Add(manageRolesPermission);
        var lecturer = new Role { Code = "lecturer" };
        db.Roles.AddRange(admin, lecturer);
        db.Permissions.AddRange(manageRolesPermission, createTimetablePermission);
        await db.SaveChangesAsync();
    }

    // AWA-13
    [Fact]
    public async Task CreateRoleBinding_ForbidsCallerWithoutManagePermission()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var caller = NewUser();
        var target = NewUser();
        db.Users.AddRange(caller, target);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller.Id);
        var result = await controller.CreateRoleBinding(new CreateRoleBindingRequest(target.Id, "lecturer", ScopeKind.Global, null));

        Assert.IsType<ForbidResult>(result.Result);
    }

    // AWA-13
    [Fact]
    public async Task CreateRoleBinding_CreatesBindingForAdminCaller()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var admin = NewUser();
        var target = NewUser();
        target.CollegeId = admin.CollegeId;
        db.Users.AddRange(admin, target);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.CreateRoleBinding(new CreateRoleBindingRequest(target.Id, "lecturer", ScopeKind.Global, null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<RoleBindingDto>(ok.Value);
        Assert.Equal(target.Id, dto.UserId);
        Assert.Equal("lecturer", dto.RoleCode);
        Assert.Single(db.RoleBindings.Local, b => b.UserId == target.Id);
    }

    // #127 — cross-college privilege escalation: an admin at one college must not be able
    // to grant a role to a user at a different college.
    [Fact]
    public async Task CreateRoleBinding_ForbidsCrossCollegeTarget()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var admin = NewUser();
        var target = NewUser(); // different (random) CollegeId than admin, by construction
        db.Users.AddRange(admin, target);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.CreateRoleBinding(new CreateRoleBindingRequest(target.Id, "lecturer", ScopeKind.Global, null));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.DoesNotContain(db.RoleBindings.Local, b => b.UserId == target.Id);
    }

    // #19: DepartmentId was previously only checked for null-consistency with ScopeType,
    // never resolved to a college — a poisoned cross-college DepartmentId here grants the
    // bound user department-scoped authority (PermissionService.GetDepartmentScopeAsync)
    // over another college entirely (TimetableController.Generate/PatchSlot,
    // MarksController.PendingExternal/ApproveExternal all trust that scope with no college
    // check of their own).
    [Fact]
    public async Task Issue19_CreateRoleBinding_ForbidsDepartmentFromAnotherCollege()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var admin = NewUser();
        var target = NewUser();
        target.CollegeId = admin.CollegeId;
        var otherCollegeDepartment = new Department { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "CS" };
        db.Users.AddRange(admin, target);
        db.Departments.Add(otherCollegeDepartment);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.CreateRoleBinding(new CreateRoleBindingRequest(target.Id, "lecturer", ScopeKind.Department, otherCollegeDepartment.Id));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.DoesNotContain(db.RoleBindings.Local, b => b.UserId == target.Id && b.RoleCode == "lecturer");
    }

    // #19: a department in the caller's own college is still accepted.
    [Fact]
    public async Task Issue19_CreateRoleBinding_AllowsDepartmentFromCallersOwnCollege()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var admin = NewUser();
        var target = NewUser();
        target.CollegeId = admin.CollegeId;
        var department = new Department { Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Name = "CS" };
        db.Users.AddRange(admin, target);
        db.Departments.Add(department);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.CreateRoleBinding(new CreateRoleBindingRequest(target.Id, "lecturer", ScopeKind.Department, department.Id));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<RoleBindingDto>(ok.Value);
        Assert.Equal(department.Id, dto.DepartmentId);
    }

    // AWA-13
    [Fact]
    public async Task DeletePermissionGrant_RevokedOverrideStopsApplyingImmediately()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var admin = NewUser();
        var target = NewUser();
        target.CollegeId = admin.CollegeId;
        db.Users.AddRange(admin, target);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var permissionService = new PermissionService(db);
        var controller = ControllerAs(db, admin.Id);

        var grantResult = await controller.CreatePermissionGrant(new CreatePermissionGrantRequest(target.Id, "create_timetable", true, null));
        var ok = Assert.IsType<OkObjectResult>(grantResult.Result);
        var grantDto = Assert.IsType<PermissionGrantDto>(ok.Value);

        Assert.True(await permissionService.HasPermissionAsync(target.Id, "create_timetable"));

        var deleteResult = await controller.DeletePermissionGrant(grantDto.Id);
        Assert.IsType<NoContentResult>(deleteResult);

        // Live DB read, no session/cache to invalidate — reflects the AC that a revoke
        // applies immediately without requiring the affected user to re-login.
        Assert.False(await permissionService.HasPermissionAsync(target.Id, "create_timetable"));
    }

    // AWA-13
    [Fact]
    public async Task CreatePermissionGrant_RejectsUnknownPermissionCode()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var admin = NewUser();
        var target = NewUser();
        target.CollegeId = admin.CollegeId;
        db.Users.AddRange(admin, target);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.CreatePermissionGrant(new CreatePermissionGrantRequest(target.Id, "not_a_real_permission", true, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // #83 — regression coverage: every RolesController (AWA-13/14) endpoint must Forbid a
    // caller with no manage_roles_and_permissions / manage_departments grant, matching the
    // sibling FeesController/MarksController.Ward auth pattern. These endpoints already had
    // [Authorize] + permission checks wired (not the 501-stub-with-no-auth state #83 was
    // originally filed against), but lacked test coverage locking that guard in place.
    [Fact]
    public async Task ListRoleBindings_ForbidsCallerWithoutManagePermission()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var caller = NewUser();
        db.Users.Add(caller);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller.Id);
        var result = await controller.ListRoleBindings();

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task ListPermissionGrants_ForbidsCallerWithoutManagePermission()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var caller = NewUser();
        db.Users.Add(caller);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller.Id);
        var result = await controller.ListPermissionGrants();

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreateDepartment_ForbidsCallerWithoutManageDepartmentsPermission()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var caller = NewUser();
        var college = new College { Id = Guid.NewGuid(), Name = "Test College" };
        db.Users.Add(caller);
        db.Colleges.Add(college);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller.Id);
        var result = await controller.CreateDepartment(new CreateDepartmentRequest(college.Id, "Computer Science"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task AssignHod_ForbidsCallerWithoutManageDepartmentsPermission()
    {
        await using var db = NewDb();
        await SeedRolesAndPermissionsAsync(db);
        var caller = NewUser();
        var candidate = NewUser();
        var college = new College { Id = Guid.NewGuid(), Name = "Test College" };
        var department = new Department { Id = Guid.NewGuid(), CollegeId = college.Id, Name = "CS" };
        db.Users.AddRange(caller, candidate);
        db.Colleges.Add(college);
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller.Id);
        var result = await controller.AssignHod(department.Id, new AssignHodRequest(candidate.Id));

        Assert.IsType<ForbidResult>(result.Result);
    }
}
