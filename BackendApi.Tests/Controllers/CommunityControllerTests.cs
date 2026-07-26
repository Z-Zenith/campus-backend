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

public class CommunityControllerTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static IConfiguration ConfigWithAllowedHosts(params string[] hosts)
    {
        var data = hosts.Select((h, i) => new KeyValuePair<string, string?>($"MaterialStorage:AllowedHosts:{i}", h));
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static async Task<User> SeedTeacherAsync(AppDbContext db)
    {
        var teacher = new User
        {
            Id = Guid.NewGuid(),
            CollegeId = Guid.NewGuid(),
            Identifier = "teacher-1",
            PasswordHash = "hash",
            FullName = "Teacher",
            IsActive = true,
            AccountType = AccountType.Teacher,
        };
        db.Users.Add(teacher);
        await db.SaveChangesAsync();
        return teacher;
    }

    // #136: FileUrl previously only had to be an absolute http(s) URL — any external domain
    // (e.g. a phishing site) was accepted and later handed straight to Redirect().
    [Fact]
    public async Task UploadMaterial_RejectsUrl_WhenHostNotOnAllowlist()
    {
        await using var db = NewDb();
        var teacher = await SeedTeacherAsync(db);
        var controller = ControllerAs(db, teacher, ConfigWithAllowedHosts("storage.campus.local"));

        var result = await controller.UploadMaterial(new CreateMaterialRequest("Notes", "https://evil-phish.example/x.pdf", null, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.Materials.ToListAsync());
    }

    [Fact]
    public async Task UploadMaterial_Succeeds_WhenHostIsOnAllowlist()
    {
        await using var db = NewDb();
        var teacher = await SeedTeacherAsync(db);
        var subjectId = Guid.NewGuid();
        db.Departments.Add(new Department { Id = Guid.NewGuid(), CollegeId = teacher.CollegeId, Name = "CS" });
        db.Subjects.Add(new Subject { Id = subjectId, DepartmentId = db.Departments.Local.First().Id, Code = "CS101", Name = "Intro", TeacherId = teacher.Id });
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, teacher, ConfigWithAllowedHosts("storage.campus.local"));

        var result = await controller.UploadMaterial(new CreateMaterialRequest("Notes", "https://storage.campus.local/x.pdf", subjectId, null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<MaterialDto>(ok.Value);
    }

    [Fact]
    public async Task DownloadMaterial_Redirects_WhenFileHostIsOnAllowlist()
    {
        await using var db = NewDb();
        var teacher = await SeedTeacherAsync(db);
        var material = new Material
        {
            Id = Guid.NewGuid(),
            Title = "Notes",
            FileUrl = "https://storage.campus.local/x.pdf",
            UploadedBy = teacher.Id,
            UploadedAt = DateTime.UtcNow,
        };
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
        var teacher = await SeedTeacherAsync(db);
        var material = new Material
        {
            Id = Guid.NewGuid(),
            Title = "Notes",
            FileUrl = "https://evil-phish.example/x.pdf",
            UploadedBy = teacher.Id,
            UploadedAt = DateTime.UtcNow,
        };
        db.Materials.Add(material);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, teacher, ConfigWithAllowedHosts("storage.campus.local"));

        var result = await controller.DownloadMaterial(material.Id);

        Assert.IsNotType<RedirectResult>(result);
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, statusResult.StatusCode);
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

    // Mirrors PermissionService's actual resolution (role_default_permissions), but as a
    // direct grant so tests don't need to seed roles/role-bindings just to exercise the
    // controller's permission check.
    private static PermissionGrant GrantCreateGroup(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        PermissionCode = "create_group",
        Granted = true,
        GrantedBy = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static PermissionGrant GrantViewAllGroups(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        PermissionCode = "view_all_groups",
        Granted = true,
        GrantedBy = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static CommunityController ControllerAs(AppDbContext db, User user, IConfiguration? configuration = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new CommunityController(db, new PermissionService(db), configuration ?? new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    // TWA-05, AWA-12
    [Fact]
    public async Task Twa05_CreateGroup_ForbidsUsersWithoutCreateGroupPermission()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.CreateGroup(new CreateGroupRequest("Chess Club", GroupType.Club, null));

        Assert.IsType<ForbidResult>(result.Result);
    }

    // TWA-05
    [Fact]
    public async Task Twa05_CreateGroup_RejectsClassType_ReservedForApi02AutoProvisioning()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        db.PermissionGrants.Add(GrantCreateGroup(teacher.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.CreateGroup(new CreateGroupRequest("Not a real class group", GroupType.Class, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // TWA-05, AWA-12
    [Fact]
    public async Task Twa05_CreateGroup_CreatesGroupAndAddsCreatorAsMember()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        db.PermissionGrants.Add(GrantCreateGroup(teacher.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.CreateGroup(new CreateGroupRequest("Staff Room", GroupType.TeacherOnly, null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<GroupDto>(ok.Value);
        Assert.Equal("Staff Room", dto.Name);
        Assert.True(await db.GroupMembers.AnyAsync(m => m.GroupId == dto.Id && m.UserId == teacher.Id));
    }

    // TWA-05
    [Fact]
    public async Task Twa05_CreateGroup_RejectsUnknownSectionId()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        db.PermissionGrants.Add(GrantCreateGroup(teacher.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.CreateGroup(new CreateGroupRequest("Ghost Section Group", GroupType.SubjectSection, Guid.NewGuid()));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // SDA-16
    [Fact]
    public async Task Sda16_MyGroups_OnlyReturnsGroupsCallerIsAMemberOf()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        db.Users.AddRange(student, otherStudent);
        var myGroup = new Group { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Mine", Type = GroupType.Club };
        var otherGroup = new Group { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Not Mine", Type = GroupType.Club };
        db.Groups.AddRange(myGroup, otherGroup);
        db.GroupMembers.AddRange(
            new GroupMember { Id = Guid.NewGuid(), GroupId = myGroup.Id, UserId = student.Id },
            new GroupMember { Id = Guid.NewGuid(), GroupId = otherGroup.Id, UserId = otherStudent.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.MyGroups();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<MyGroupsResponse>(ok.Value);
        Assert.Single(response.Groups);
        Assert.Equal("Mine", response.Groups[0].Name);
    }

    // SDA-16: acceptance-critical — "a teacher-only group is not visible to any student".
    [Fact]
    public async Task Sda16_MyGroups_ExcludesTeacherOnlyGroups_ForStudentAccounts_EvenIfSomehowAMember()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        var teacherOnlyGroup = new Group { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Staff Only", Type = GroupType.TeacherOnly };
        db.Groups.Add(teacherOnlyGroup);
        db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = teacherOnlyGroup.Id, UserId = student.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.MyGroups();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<MyGroupsResponse>(ok.Value);
        Assert.Empty(response.Groups);
    }

    // SDA-16
    [Fact]
    public async Task Sda16_CreatePost_ForbidsNonMembers()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        var group = new Group { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Club", Type = GroupType.Club };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.CreatePost(group.Id, new CreatePostRequest("hello"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    // SDA-16
    [Fact]
    public async Task Sda16_CreatePost_RejectsEmptyContent()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        var group = new Group { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Club", Type = GroupType.Club };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = student.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.CreatePost(group.Id, new CreatePostRequest("   "));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // SDA-16
    [Fact]
    public async Task Sda16_CreatePost_CreatesPost_ForGroupMember()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        var group = new Group { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Club", Type = GroupType.Club };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = student.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.CreatePost(group.Id, new CreatePostRequest("First post!"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<GroupPostDto>(ok.Value);
        Assert.Equal("First post!", dto.Content);
        Assert.Equal(student.Id, dto.AuthorId);
        Assert.Single(await db.GroupPosts.Where(p => p.GroupId == group.Id).ToListAsync());
    }

    // AWA-06: "no group is excluded from Admin's view regardless of who created it".
    [Fact]
    public async Task Awa06_AllGroups_ForbidsCallersWithoutViewAllGroupsPermission()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.AllGroups();

        Assert.IsType<ForbidResult>(result.Result);
    }

    // AWA-06 within the caller's college: every group in the college is returned regardless
    // of who created it or whether the caller is a member. Updated from the prior version
    // that seeded both groups in *different random colleges* (neither the admin's) and
    // asserted both came back — that encoded the #126 cross-college IDOR this fix closes.
    [Fact]
    public async Task Awa06_AllGroups_ReturnsEveryGroupInCallersCollege_RegardlessOfMembership()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        db.PermissionGrants.Add(GrantViewAllGroups(admin.Id));
        db.Groups.AddRange(
            new Group { Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Name = "Group A", Type = GroupType.Club, CreatedBy = Guid.NewGuid() },
            new Group { Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Name = "Group B", Type = GroupType.SubjectSection, CreatedBy = Guid.NewGuid() });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.AllGroups();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<MyGroupsResponse>(ok.Value);
        Assert.Equal(2, response.Groups.Count);
    }

    // #126 (fix: enforce college scope on groups) — a view_all_groups holder must only see
    // their own college's groups, never another college's.
    [Fact]
    public async Task Awa06_AllGroups_ExcludesGroupsFromOtherColleges()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        db.PermissionGrants.Add(GrantViewAllGroups(admin.Id));
        db.Groups.AddRange(
            new Group { Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Name = "Mine", Type = GroupType.Club, CreatedBy = Guid.NewGuid() },
            new Group { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "Other College", Type = GroupType.Club, CreatedBy = Guid.NewGuid() });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.AllGroups();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<MyGroupsResponse>(ok.Value);
        var group = Assert.Single(response.Groups);
        Assert.Equal("Mine", group.Name);
    }

    // TWA-05, SDA-16: "view and post in groups they belong to" — the view half.
    [Fact]
    public async Task Twa05Sda16_ListPosts_ForbidsNonMembers()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        var group = new Group { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Club", Type = GroupType.Club };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.ListPosts(group.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Twa05Sda16_ListPosts_ReturnsGroupPostsNewestFirst_ForMembers()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        var group = new Group { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Club", Type = GroupType.Club };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = student.Id });
        db.GroupPosts.AddRange(
            new GroupPost { Id = Guid.NewGuid(), GroupId = group.Id, AuthorId = student.Id, Content = "Older", CreatedAt = DateTime.UtcNow.AddMinutes(-5) },
            new GroupPost { Id = Guid.NewGuid(), GroupId = group.Id, AuthorId = student.Id, Content = "Newer", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.ListPosts(group.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var posts = Assert.IsType<List<GroupPostDto>>(ok.Value);
        Assert.Equal(2, posts.Count);
        Assert.Equal("Newer", posts[0].Content);
        Assert.Equal("Older", posts[1].Content);
    }

    // SDA-16: material shared in a group must surface in that group's Materials list
    // without a separate upload step, i.e. reading the same rows TWA-06 writes.
    [Fact]
    public async Task Sda16_ListGroupMaterials_ForbidsNonMembers()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        var group = new Group { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Club", Type = GroupType.Club };
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.ListGroupMaterials(group.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Sda16_ListGroupMaterials_ReturnsMaterialsSharedInTheGroup()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var student = NewUser(AccountType.Student);
        db.Users.AddRange(teacher, student);
        var group = new Group { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Name = "Club", Type = GroupType.Club };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = student.Id });
        db.Materials.Add(new Material { Id = Guid.NewGuid(), GroupId = group.Id, Title = "Slides", FileUrl = "https://example.com/slides.pdf", UploadedBy = teacher.Id, UploadedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.ListGroupMaterials(group.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var materials = Assert.IsType<List<MaterialDto>>(ok.Value);
        var entry = Assert.Single(materials);
        Assert.Equal("Slides", entry.Title);
    }

    // API-02: "one class group created per class, every semester... no manual step required."
    [Fact]
    public async Task Api02_ProvisionClassGroups_ForbidsNonAdmins()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.ProvisionClassGroups();

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Api02_ProvisionClassGroups_CreatesOneGroupPerSectionAndEnrollsStudents()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var admin = NewUser(AccountType.AdminTier, collegeId);
        var student = NewUser(AccountType.Student, collegeId);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 3, Name = "3rd Year CSE - A" };
        db.Users.AddRange(admin, student);
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = section.Id, StudentId = student.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.ProvisionClassGroups();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProvisionClassGroupsResponse>(ok.Value);
        Assert.Equal(1, response.GroupsCreated);
        Assert.Equal(1, response.MembershipsAdded);

        var group = Assert.Single(await db.Groups.Where(g => g.SectionId == section.Id && g.Type == GroupType.Class).ToListAsync());
        Assert.Equal("3rd Year CSE - A", group.Name);
        Assert.True(await db.GroupMembers.AnyAsync(m => m.GroupId == group.Id && m.UserId == student.Id));
    }

    [Fact]
    public async Task Api02_ProvisionClassGroups_IsIdempotent_SkipsExistingGroupsAndAvoidsDuplicateMemberships()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var admin = NewUser(AccountType.AdminTier, collegeId);
        var student = NewUser(AccountType.Student, collegeId);
        var newStudent = NewUser(AccountType.Student, collegeId);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 3, Name = "3rd Year CSE - A" };
        var existingGroup = new Group { Id = Guid.NewGuid(), CollegeId = collegeId, Name = section.Name, Type = GroupType.Class, SectionId = section.Id, CreatedBy = admin.Id };
        db.Users.AddRange(admin, student, newStudent);
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Groups.Add(existingGroup);
        db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = existingGroup.Id, UserId = student.Id });
        db.SectionEnrollments.AddRange(
            new SectionEnrollment { Id = Guid.NewGuid(), SectionId = section.Id, StudentId = student.Id },
            new SectionEnrollment { Id = Guid.NewGuid(), SectionId = section.Id, StudentId = newStudent.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.ProvisionClassGroups();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProvisionClassGroupsResponse>(ok.Value);
        Assert.Equal(0, response.GroupsCreated);
        Assert.Equal(1, response.MembershipsAdded);
        Assert.Single(await db.Groups.Where(g => g.SectionId == section.Id).ToListAsync());
        Assert.Equal(2, await db.GroupMembers.CountAsync(m => m.GroupId == existingGroup.Id));
    }

    // #114 review: an Admin at one college must not provision or re-sync class groups
    // for another college's sections.
    [Fact]
    public async Task Api02_ProvisionClassGroups_DoesNotTouchOtherColleges()
    {
        await using var db = NewDb();
        var adminCollegeId = Guid.NewGuid();
        var otherCollegeId = Guid.NewGuid();
        var admin = NewUser(AccountType.AdminTier, adminCollegeId);
        var otherStudent = NewUser(AccountType.Student, otherCollegeId);
        var otherDepartment = new Department { Id = Guid.NewGuid(), CollegeId = otherCollegeId, Name = "CS" };
        var otherSection = new Section { Id = Guid.NewGuid(), DepartmentId = otherDepartment.Id, Year = 1, Name = "1st Year CSE - A" };
        db.Users.AddRange(admin, otherStudent);
        db.Departments.Add(otherDepartment);
        db.Sections.Add(otherSection);
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = otherSection.Id, StudentId = otherStudent.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.ProvisionClassGroups();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProvisionClassGroupsResponse>(ok.Value);
        Assert.Equal(0, response.GroupsCreated);
        Assert.Equal(0, response.MembershipsAdded);
        Assert.False(await db.Groups.AnyAsync(g => g.SectionId == otherSection.Id));
    }
}
