using System.Security.Claims;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Services;

public class WardAccessFilterTests
{
    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IAppAuthorizationService NewAuthService(AppDbContext db) =>
        new AuthorizationService(db, NullLogger<AuthorizationService>.Instance);

    private static ClaimsPrincipal ParentPrincipal(Guid userId, Guid sessionId, Guid wardId) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("account_type", nameof(AccountType.Parent)),
            new Claim("ward_id", wardId.ToString()),
            new Claim("session_id", sessionId.ToString()),
        ], "TestAuth"));

    private static ActionExecutingContext NewContext(ClaimsPrincipal user, Guid studentIdRouteValue)
    {
        var httpContext = new DefaultHttpContext { User = user };
        var routeData = new RouteData();
        routeData.Values["studentId"] = studentIdRouteValue.ToString();
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(httpContext, routeData, new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: null!);
    }

    // #93: a caller who isn't authorized for the requested ward must get NotFound, not
    // Forbid — so "this student doesn't exist" and "it's not your ward" are indistinguishable
    // to an unauthorized caller probing ids, matching FeesController.Pay's convention.
    [Fact]
    public async Task ReturnsNotFound_NotForbid_WhenCallerIsNotAuthorizedForWard()
    {
        using var db = NewDb();
        var parentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var requestedStudentId = Guid.NewGuid();
        // Deliberately no ParentWards link and no matching session — caller is unauthorized.

        var filter = new WardAccessFilter(db, NewAuthService(db));
        var context = NewContext(ParentPrincipal(parentId, sessionId, requestedStudentId), requestedStudentId);

        var called = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            called = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.IsType<NotFoundResult>(context.Result);
        Assert.False(called);
    }

    [Fact]
    public async Task AllowsRequest_WhenCallerIsAuthorizedForWard()
    {
        using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        db.Users.Add(new User { Id = parentId, CollegeId = collegeId, Identifier = "parent-1", PasswordHash = "hash", FullName = "Parent", IsActive = true, AccountType = AccountType.Parent });
        db.Users.Add(new User { Id = studentId, CollegeId = collegeId, Identifier = "student-1", PasswordHash = "hash", FullName = "Student", IsActive = true, AccountType = AccountType.Student });
        db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parentId, StudentId = studentId });
        db.UserSessions.Add(new UserSession { Id = sessionId, UserId = parentId, IsActive = true });
        await db.SaveChangesAsync();

        var filter = new WardAccessFilter(db, NewAuthService(db));
        var context = NewContext(ParentPrincipal(parentId, sessionId, studentId), studentId);

        var called = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            called = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Null(context.Result);
        Assert.True(called);
    }
}
