using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BackendApi.Tests.Controllers;

// Community redesign: CommunityController is now scoped to staff-only groups (all that's
// left of the old flat "groups" concept once Clubs/ClassroomDiscussions moved to their own
// controllers - see ClubsControllerTests.cs / ClassroomDiscussionsControllerTests.cs) and
// Materials, which can attach to a subject and/or any one of the three community spaces.
public class CommunityControllerTests
{
    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IConfiguration ConfigWithAllowedHosts(params string[] hosts)
    {
        var data = hosts.Select((h, i) => new KeyValuePair<string, string?>($"MaterialStorage:AllowedHosts:{i}", h));
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static User NewUser(AccountType accountType, Guid? collegeId = null) => new()
    {
        Id = Guid.NewGuid(),
        CollegeId = collegeId ?? Guid.NewGuid(),
        Identifier = $"user-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        FullName = "Test User",
        AccountType = accountType,
        IsActive = true,
    };

    private static PermissionGrant Grant(Guid userId, string code) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        PermissionCode = code,
        Granted = true,
        GrantedBy = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static CommunityController ControllerAs(AppDbContext db, User user, IConfiguration? configuration = null) =>
        new(db, new PermissionService(db), configuration ?? new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth")),
                },
            },
        };

    [Fact]
    public async Task CreateStaffGroup_ForbidsUsersWithoutCreateGroupPermission()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, student).CreateStaffGroup(new CreateStaffGroupRequest("Staff Room"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreateStaffGroup_CreatesGroupAndAddsCreatorAsMember()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        db.PermissionGrants.Add(Grant(teacher.Id, "create_group"));
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, teacher).CreateStaffGroup(new CreateStaffGroupRequest("Staff Room"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StaffGroupDto>(ok.Value);
        Assert.Equal("Staff Room", dto.Name);
        Assert.True(await db.StaffGroupMembers.AnyAsync(m => m.StaffGroupId == dto.Id && m.UserId == teacher.Id));
    }

    [Fact]
    public async Task MyStaffGroups_NeverReturnsGroupsToAStudent()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var staffGroup = new StaffGroup { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Staff Only" };
        db.Users.Add(student);
        db.StaffGroups.Add(staffGroup);
        // Defense-in-depth: even if a membership row existed for the student by mistake.
        db.StaffGroupMembers.Add(new StaffGroupMember { Id = Guid.NewGuid(), StaffGroupId = staffGroup.Id, UserId = student.Id });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, student).MyStaffGroups();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<MyStaffGroupsResponse>(ok.Value);
        Assert.Empty(response.StaffGroups);
    }

    [Fact]
    public async Task AllStaffGroups_ScopesToCallersOwnCollege()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        db.PermissionGrants.Add(Grant(admin.Id, "view_all_groups"));
        db.StaffGroups.AddRange(
            new StaffGroup { Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Name = "Mine" },
            new StaffGroup { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "Other College" });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, admin).AllStaffGroups();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<MyStaffGroupsResponse>(ok.Value);
        Assert.Single(response.StaffGroups);
        Assert.Equal("Mine", response.StaffGroups[0].Name);
    }

    [Fact]
    public async Task RemoveMember_RemovesTheGivenMember()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var caller = NewUser(AccountType.Teacher, collegeId);
        var otherMember = NewUser(AccountType.Teacher, collegeId);
        var group = new StaffGroup { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Staff Room" };
        db.Users.AddRange(caller, otherMember);
        db.StaffGroups.Add(group);
        db.StaffGroupMembers.Add(new StaffGroupMember { Id = Guid.NewGuid(), StaffGroupId = group.Id, UserId = caller.Id, JoinedAt = DateTime.UtcNow });
        var membership = new StaffGroupMember { Id = Guid.NewGuid(), StaffGroupId = group.Id, UserId = otherMember.Id, JoinedAt = DateTime.UtcNow };
        db.StaffGroupMembers.Add(membership);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, caller).RemoveMember(group.Id, otherMember.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.DoesNotContain(db.StaffGroupMembers.Local, m => m.Id == membership.Id);
    }

    // #136: FileUrl previously only had to be an absolute http(s) URL — any external domain
    // (e.g. a phishing site) was accepted and later handed straight to Redirect().
    [Fact]
    public async Task UploadMaterial_RejectsUrl_WhenHostNotOnAllowlist()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, teacher, ConfigWithAllowedHosts("storage.campus.local"));

        var result = await controller.UploadMaterial(new CreateMaterialRequest("Notes", "https://evil-phish.example/x.pdf", null, null, null, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.Materials.ToListAsync());
    }

    [Fact]
    public async Task UploadMaterial_Succeeds_WhenHostIsOnAllowlist()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = teacher.CollegeId, Name = "CS" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro", TeacherId = teacher.Id };
        db.Users.Add(teacher);
        db.Departments.Add(department);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, teacher, ConfigWithAllowedHosts("storage.campus.local"));

        var result = await controller.UploadMaterial(new CreateMaterialRequest("Notes", "https://storage.campus.local/x.pdf", subject.Id, null, null, null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<MaterialDto>(ok.Value);
    }

    [Fact]
    public async Task UploadMaterial_RejectsUnknownClub()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, teacher, ConfigWithAllowedHosts("storage.campus.local"));

        var result = await controller.UploadMaterial(new CreateMaterialRequest("Notes", "https://storage.campus.local/x.pdf", null, Guid.NewGuid(), null, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task DownloadMaterial_Redirects_WhenFileHostIsOnAllowlist()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var material = new Material
        {
            Id = Guid.NewGuid(),
            Title = "Notes",
            FileUrl = "https://storage.campus.local/x.pdf",
            UploadedBy = teacher.Id,
            UploadedAt = DateTime.UtcNow,
        };
        db.Users.Add(teacher);
        db.Materials.Add(material);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, teacher, ConfigWithAllowedHosts("storage.campus.local"));

        var result = await controller.DownloadMaterial(material.Id);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal(material.FileUrl, redirect.Url);
    }

    // #136: defense in depth for a row whose FileUrl predates the allowlist (or reached the
    // table by some other path) — must never redirect to a disallowed host.
    [Fact]
    public async Task DownloadMaterial_RefusesToRedirect_WhenFileHostIsNotOnAllowlist()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var material = new Material
        {
            Id = Guid.NewGuid(),
            Title = "Notes",
            FileUrl = "https://evil-phish.example/x.pdf",
            UploadedBy = teacher.Id,
            UploadedAt = DateTime.UtcNow,
        };
        db.Users.Add(teacher);
        db.Materials.Add(material);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, teacher, ConfigWithAllowedHosts("storage.campus.local"));

        var result = await controller.DownloadMaterial(material.Id);

        Assert.IsNotType<RedirectResult>(result);
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, statusResult.StatusCode);
    }

    [Fact]
    public async Task DownloadMaterial_AllowsClubMemberToDownloadClubMaterial()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var student = NewUser(AccountType.Student, collegeId);
        var uploader = NewUser(AccountType.Teacher, collegeId);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        var material = new Material
        {
            Id = Guid.NewGuid(),
            Title = "Rules",
            FileUrl = "https://storage.campus.local/rules.pdf",
            ClubId = club.Id,
            UploadedBy = uploader.Id,
            UploadedAt = DateTime.UtcNow,
        };
        db.Users.AddRange(student, uploader);
        db.Clubs.Add(club);
        db.ClubMembers.Add(new ClubMember { Id = Guid.NewGuid(), ClubId = club.Id, UserId = student.Id, JoinedAt = DateTime.UtcNow });
        db.Materials.Add(material);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, student, ConfigWithAllowedHosts("storage.campus.local"));

        var result = await controller.DownloadMaterial(material.Id);

        Assert.IsType<RedirectResult>(result);
    }

    [Fact]
    public async Task DownloadMaterial_ForbidsNonMemberFromClubMaterial()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var outsider = NewUser(AccountType.Student, collegeId);
        var uploader = NewUser(AccountType.Teacher, collegeId);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        var material = new Material
        {
            Id = Guid.NewGuid(),
            Title = "Rules",
            FileUrl = "https://storage.campus.local/rules.pdf",
            ClubId = club.Id,
            UploadedBy = uploader.Id,
            UploadedAt = DateTime.UtcNow,
        };
        db.Users.AddRange(outsider, uploader);
        db.Clubs.Add(club);
        db.Materials.Add(material);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, outsider, ConfigWithAllowedHosts("storage.campus.local"));

        var result = await controller.DownloadMaterial(material.Id);

        Assert.IsType<ForbidResult>(result);
    }
}
