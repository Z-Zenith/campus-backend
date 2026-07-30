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

public class ClubsControllerTests
{
    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

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

    private static ICollegeScopeService CollegeScope(AppDbContext db) => new CollegeScopeService(db);

    private static ClubsController ControllerAs(AppDbContext db, User user) =>
        new(db, new PermissionService(db), CollegeScope(db))
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
    public async Task CreateClub_ForbidsCallerWithoutCreateClubsPermission()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, student).CreateClub(new CreateClubRequest("Chess Club", null, null, null));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreateClub_CreatesClubWithFacultyLeadAndStudentIncharge()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var facultyLead = NewUser(AccountType.Teacher, teacher.CollegeId);
        var studentIncharge = NewUser(AccountType.Student, teacher.CollegeId);
        db.Users.AddRange(teacher, facultyLead, studentIncharge);
        db.PermissionGrants.Add(Grant(teacher.Id, "create_clubs"));
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, teacher).CreateClub(
            new CreateClubRequest("Chess Club", "For chess enthusiasts", facultyLead.Id, studentIncharge.Id));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ClubDto>(ok.Value);
        Assert.Equal("Chess Club", dto.Name);
        Assert.Equal(facultyLead.Id, dto.FacultyLeadUserId);
        Assert.Equal(studentIncharge.Id, dto.StudentInchargeUserId);
        // Creator + student incharge both auto-added as members.
        Assert.Equal(2, await db.ClubMembers.CountAsync(m => m.ClubId == dto.Id));
    }

    [Fact]
    public async Task CreateClub_RejectsFacultyLeadWhoIsAStudent()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var notATeacher = NewUser(AccountType.Student, teacher.CollegeId);
        db.Users.AddRange(teacher, notATeacher);
        db.PermissionGrants.Add(Grant(teacher.Id, "create_clubs"));
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, teacher).CreateClub(new CreateClubRequest("Chess Club", null, notATeacher.Id, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateClub_RejectsStudentInchargeWhoIsATeacher()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var notAStudent = NewUser(AccountType.Teacher, teacher.CollegeId);
        db.Users.AddRange(teacher, notAStudent);
        db.PermissionGrants.Add(Grant(teacher.Id, "create_clubs"));
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, teacher).CreateClub(new CreateClubRequest("Chess Club", null, null, notAStudent.Id));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task JoinClub_AddsCallerAsMember()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var student = NewUser(AccountType.Student, collegeId);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        db.Users.Add(student);
        db.Clubs.Add(club);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, student).JoinClub(club.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<ClubMemberDto>(ok.Value);
        Assert.True(await db.ClubMembers.AnyAsync(m => m.ClubId == club.Id && m.UserId == student.Id));
    }

    [Fact]
    public async Task JoinClub_RejectsDuplicateJoin()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var student = NewUser(AccountType.Student, collegeId);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        db.Users.Add(student);
        db.Clubs.Add(club);
        db.ClubMembers.Add(new ClubMember { Id = Guid.NewGuid(), ClubId = club.Id, UserId = student.Id, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, student).JoinClub(club.Id);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task JoinClub_ForbidsCrossCollegeClub()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        db.Users.Add(student);
        db.Clubs.Add(club);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, student).JoinClub(club.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task LeaveClub_RemovesMembership()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var student = NewUser(AccountType.Student, collegeId);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        db.Users.Add(student);
        db.Clubs.Add(club);
        var membership = new ClubMember { Id = Guid.NewGuid(), ClubId = club.Id, UserId = student.Id, JoinedAt = DateTime.UtcNow };
        db.ClubMembers.Add(membership);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, student).LeaveClub(club.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.DoesNotContain(db.ClubMembers.Local, m => m.Id == membership.Id);
    }

    // The oversight-vs-management fix this redesign targets: an admin holding view_all_clubs
    // can manage a club's membership without being a member themselves - the real-world
    // "Branch Administrator" pattern (structural delegation, not membership-gated).
    [Fact]
    public async Task AddMember_AllowsCallerWithViewAllClubsPermission_EvenIfNotAClubMember()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var admin = NewUser(AccountType.AdminTier, collegeId);
        var newMember = NewUser(AccountType.Student, collegeId);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(admin, newMember);
        db.Clubs.Add(club);
        db.PermissionGrants.Add(Grant(admin.Id, "view_all_clubs"));
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, admin).AddMember(club.Id, new AddClubMemberRequest(newMember.Id));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(await db.ClubMembers.AnyAsync(m => m.ClubId == club.Id && m.UserId == newMember.Id));
    }

    [Fact]
    public async Task AddMember_ForbidsNonMemberWithoutOversightPermission()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var outsider = NewUser(AccountType.Student, collegeId);
        var newMember = NewUser(AccountType.Student, collegeId);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(outsider, newMember);
        db.Clubs.Add(club);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, outsider).AddMember(club.Id, new AddClubMemberRequest(newMember.Id));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpdateClub_AllowsOversightHolderToChangeLeadership_WithoutBeingAMember()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var admin = NewUser(AccountType.AdminTier, collegeId);
        var newFacultyLead = NewUser(AccountType.Teacher, collegeId);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(admin, newFacultyLead);
        db.Clubs.Add(club);
        db.PermissionGrants.Add(Grant(admin.Id, "view_all_clubs"));
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, admin).UpdateClub(
            club.Id, new UpdateClubRequest("Chess Club", null, newFacultyLead.Id, null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ClubDto>(ok.Value);
        Assert.Equal(newFacultyLead.Id, dto.FacultyLeadUserId);
    }

    [Fact]
    public async Task UpdateHomeSite_AllowsStudentIncharge()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var studentIncharge = NewUser(AccountType.Student, collegeId);
        var club = new Club
        {
            Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club",
            StudentInchargeUserId = studentIncharge.Id, CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(studentIncharge);
        db.Clubs.Add(club);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, studentIncharge).UpdateHomeSite(
            club.Id, new UpdateClubHomeSiteRequest("<h1>Welcome to Chess Club</h1>"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ClubDto>(ok.Value);
        Assert.Equal("<h1>Welcome to Chess Club</h1>", dto.HomeSiteHtml);
    }

    [Fact]
    public async Task UpdateHomeSite_ForbidsRegularMember()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var member = NewUser(AccountType.Student, collegeId);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        db.Users.Add(member);
        db.Clubs.Add(club);
        db.ClubMembers.Add(new ClubMember { Id = Guid.NewGuid(), ClubId = club.Id, UserId = member.Id, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, member).UpdateHomeSite(club.Id, new UpdateClubHomeSiteRequest("<script>alert(1)</script>"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreatePost_ForbidsNonMember()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var outsider = NewUser(AccountType.Student, collegeId);
        var club = new Club { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "Chess Club", CreatedAt = DateTime.UtcNow };
        db.Users.Add(outsider);
        db.Clubs.Add(club);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, outsider).CreatePost(club.Id, new CreateClubPostRequest("Hello"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task ListClubs_ScopesToCallersOwnCollege()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        db.Clubs.AddRange(
            new Club { Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Name = "Mine", CreatedAt = DateTime.UtcNow },
            new Club { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "Other College", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, admin).ListClubs();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var clubs = Assert.IsType<List<ClubDto>>(ok.Value);
        Assert.Single(clubs);
        Assert.Equal("Mine", clubs[0].Name);
    }
}
