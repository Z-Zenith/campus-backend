using System.Security.Claims;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Services;

public class ParentWardAccessTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    // Real AuthorizationService (not a fake) — these tests exercise genuine parent_wards
    // seed/revoke state, so the real CheckRelationAsync("parent","student",...) path is
    // what's actually under test here.
    private static IAppAuthorizationService NewAuthService(AppDbContext db) =>
        new AuthorizationService(db, NullLogger<AuthorizationService>.Instance);

    private static ClaimsPrincipal BuildPrincipal(Guid userId, Guid sessionId, Guid wardId, string accountType = "Parent")
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("account_type", accountType),
            new Claim("ward_id", wardId.ToString()),
            new Claim("session_id", sessionId.ToString()),
        ]);
        return new ClaimsPrincipal(identity);
    }

    private static async Task<(Guid parentId, Guid studentId, Guid sessionId)> SeedLinkedParentAsync(
        AppDbContext db, bool sessionActive = true, bool parentActive = true, bool keepWardLink = true)
    {
        var collegeId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        db.Users.Add(new User
        {
            Id = parentId,
            CollegeId = collegeId,
            Identifier = "parent-1",
            PasswordHash = "hash",
            FullName = "Parent One",
            IsActive = parentActive,
            AccountType = AccountType.Parent,
        });
        db.Users.Add(new User
        {
            Id = studentId,
            CollegeId = collegeId,
            Identifier = "student-1",
            PasswordHash = "hash",
            FullName = "Student One",
            IsActive = true,
            AccountType = AccountType.Student,
        });
        if (keepWardLink)
        {
            db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parentId, StudentId = studentId });
        }
        db.UserSessions.Add(new UserSession { Id = sessionId, UserId = parentId, IsActive = sessionActive });

        await db.SaveChangesAsync();
        return (parentId, studentId, sessionId);
    }

    [Fact]
    public async Task ReturnsParentId_WhenClaimsAndDbStateAreValid()
    {
        using var db = NewDb();
        var (parentId, studentId, sessionId) = await SeedLinkedParentAsync(db);
        var principal = BuildPrincipal(parentId, sessionId, studentId);

        var result = await ParentWardAccess.GetAuthorizedParentIdAsync(db, NewAuthService(db), principal, studentId);

        Assert.Equal(parentId, result);
    }

    [Fact]
    public async Task ReturnsNull_WhenAccountTypeIsNotParent()
    {
        using var db = NewDb();
        var (parentId, studentId, sessionId) = await SeedLinkedParentAsync(db);
        var principal = BuildPrincipal(parentId, sessionId, studentId, accountType: "Student");

        var result = await ParentWardAccess.GetAuthorizedParentIdAsync(db, NewAuthService(db), principal, studentId);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenRequestedStudentIdDoesNotMatchWardClaim()
    {
        using var db = NewDb();
        var (parentId, studentId, sessionId) = await SeedLinkedParentAsync(db);
        var principal = BuildPrincipal(parentId, sessionId, studentId);
        var otherStudentId = Guid.NewGuid();

        var result = await ParentWardAccess.GetAuthorizedParentIdAsync(db, NewAuthService(db), principal, otherStudentId);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenSessionIsInactive()
    {
        using var db = NewDb();
        var (parentId, studentId, sessionId) = await SeedLinkedParentAsync(db, sessionActive: false);
        var principal = BuildPrincipal(parentId, sessionId, studentId);

        var result = await ParentWardAccess.GetAuthorizedParentIdAsync(db, NewAuthService(db), principal, studentId);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenParentWardLinkWasRevoked()
    {
        using var db = NewDb();
        var (parentId, studentId, sessionId) = await SeedLinkedParentAsync(db, keepWardLink: false);
        var principal = BuildPrincipal(parentId, sessionId, studentId);

        var result = await ParentWardAccess.GetAuthorizedParentIdAsync(db, NewAuthService(db), principal, studentId);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReturnsNull_WhenParentAccountIsInactive()
    {
        using var db = NewDb();
        var (parentId, studentId, sessionId) = await SeedLinkedParentAsync(db, parentActive: false);
        var principal = BuildPrincipal(parentId, sessionId, studentId);

        var result = await ParentWardAccess.GetAuthorizedParentIdAsync(db, NewAuthService(db), principal, studentId);

        Assert.Null(result);
    }
}
