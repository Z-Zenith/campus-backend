using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using BackendApi.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Controllers;

// Also covers SDA-04 + Notification Router (shared): approving a whitelist request routes
// a WhitelistRequest notification back to the original requester. NotificationRouterTests
// (Services) covers RouteAsync itself; the tests here cover that ApproveWhitelistRequest
// actually calls it, for the right recipient, only on the request that's genuinely approved.
public class BrowsingControllerTests
{
    // No test in this file needs real SignalR delivery — NotificationRouterTests owns
    // that. This just records what ApproveWhitelistRequest routed.
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

    // Mirrors AuthorizationService's actual resolution (role_default_permissions), but as a
    // direct grant so tests don't need to seed roles/role-bindings just to exercise the
    // controller's permission check.
    private static PermissionGrant GrantViewBrowsingHistory(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        PermissionCode = "view_browsing_history",
        Granted = true,
        GrantedBy = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static BrowsingController ControllerAs(AppDbContext db, User user, FakeAiServicesClient? aiServices = null) =>
        ControllerAs(db, user, aiServices ?? new FakeAiServicesClient(), new RecordingNotificationRouter());

    private static BrowsingController ControllerAs(AppDbContext db, User user, RecordingNotificationRouter router) =>
        ControllerAs(db, user, new FakeAiServicesClient(), router);

    private static BrowsingController ControllerAs(AppDbContext db, User user, FakeAiServicesClient aiServices, INotificationRouter notifications)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new BrowsingController(db, aiServices, new AuthorizationService(db, NullLogger<AuthorizationService>.Instance), notifications)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    // SDA-03
    [Fact]
    public async Task Sda03_GetWhitelist_OnlyReturnsSitesForCallersCollege()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        db.WhitelistSites.AddRange(
            new WhitelistSite { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Url = "https://allowed.example", ApprovedAt = DateTime.UtcNow },
            new WhitelistSite { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Url = "https://otherclass.example", ApprovedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.GetWhitelist();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WhitelistResponse>(ok.Value);
        Assert.Single(response.Sites);
        Assert.Equal("https://allowed.example", response.Sites[0].Url);
    }

    // SDA-04
    [Fact]
    public async Task Sda04_RequestWhitelist_CreatesPendingRequest_ForValidUrl()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.RequestWhitelist(new CreateWhitelistRequestRequest("https://newsite.example"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<WhitelistRequestDto>(ok.Value);
        Assert.Equal("Pending", dto.Status);
        Assert.Single(await db.WhitelistRequests.ToListAsync());
    }

    // SDA-04
    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://wrong-scheme.example")]
    public async Task Sda04_RequestWhitelist_RejectsInvalidUrl(string invalidUrl)
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.RequestWhitelist(new CreateWhitelistRequestRequest(invalidUrl));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // SDA-04
    [Fact]
    public async Task Sda04_RequestWhitelist_ReturnsConflict_WhenUrlAlreadyWhitelisted()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        db.WhitelistSites.Add(new WhitelistSite { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Url = "https://already.example", ApprovedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.RequestWhitelist(new CreateWhitelistRequestRequest("https://already.example"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    // SDA-04: host casing and a bare trailing slash must not let the same site dodge the
    // already-whitelisted check by being tracked as a different URL string.
    [Theory]
    [InlineData("https://Already.Example")]
    [InlineData("https://already.example/")]
    public async Task Sda04_RequestWhitelist_ReturnsConflict_ForCaseOrTrailingSlashVariantOfWhitelistedUrl(string variantUrl)
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        db.WhitelistSites.Add(new WhitelistSite { Id = Guid.NewGuid(), CollegeId = student.CollegeId, Url = "https://already.example", ApprovedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.RequestWhitelist(new CreateWhitelistRequestRequest(variantUrl));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    // SDA-04
    [Fact]
    public async Task Sda04_RequestWhitelist_IsIdempotent_ReturnsExistingPendingRequestForSameCollegeAndUrl()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var first = await controller.RequestWhitelist(new CreateWhitelistRequestRequest("https://dup.example"));
        var second = await controller.RequestWhitelist(new CreateWhitelistRequestRequest("https://dup.example"));

        var firstDto = (WhitelistRequestDto)((OkObjectResult)first.Result!).Value!;
        var secondDto = (WhitelistRequestDto)((OkObjectResult)second.Result!).Value!;
        Assert.Equal(firstDto.Id, secondDto.Id);
        Assert.Single(await db.WhitelistRequests.ToListAsync());
    }

    // SDA-04
    [Fact]
    public async Task Sda04_ListPendingRequests_ForbidsStudents()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.ListPendingRequests();

        Assert.IsType<ForbidResult>(result.Result);
    }

    // SDA-04
    [Fact]
    public async Task Sda04_ListPendingRequests_OnlyReturnsPendingForReviewersCollege()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var sameCollegeStudent = NewUser(AccountType.Student, teacher.CollegeId);
        var otherCollegeStudent = NewUser(AccountType.Student);
        db.Users.AddRange(teacher, sameCollegeStudent, otherCollegeStudent);
        db.WhitelistRequests.AddRange(
            new WhitelistRequest { Id = Guid.NewGuid(), Url = "https://mine.example", RequestedBy = sameCollegeStudent.Id, Status = WhitelistRequestStatus.Pending },
            new WhitelistRequest { Id = Guid.NewGuid(), Url = "https://other-college.example", RequestedBy = otherCollegeStudent.Id, Status = WhitelistRequestStatus.Pending },
            new WhitelistRequest { Id = Guid.NewGuid(), Url = "https://already-approved.example", RequestedBy = sameCollegeStudent.Id, Status = WhitelistRequestStatus.Approved });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.ListPendingRequests();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WhitelistRequestDto>>(ok.Value);
        Assert.Single(list);
        Assert.Equal("https://mine.example", list[0].Url);
    }

    // SDA-04
    [Fact]
    public async Task Sda04_ApproveWhitelistRequest_ForbidsStudents()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var requester = NewUser(AccountType.Student);
        db.Users.AddRange(student, requester);
        var request = new WhitelistRequest { Id = Guid.NewGuid(), Url = "https://x.example", RequestedBy = requester.Id, Status = WhitelistRequestStatus.Pending };
        db.WhitelistRequests.Add(request);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.ApproveWhitelistRequest(request.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // SDA-04: this is the acceptance-critical path — approval must create an
    // institution-wide site, not something scoped to the requesting student's class.
    [Fact]
    public async Task Sda04_ApproveWhitelistRequest_CreatesSiteForRequestersCollege_AndMarksApproved()
    {
        await using var db = NewDb();
        var requester = NewUser(AccountType.Student);
        var teacher = NewUser(AccountType.Teacher, requester.CollegeId);
        db.Users.AddRange(teacher, requester);
        var request = new WhitelistRequest { Id = Guid.NewGuid(), Url = "https://approve-me.example", RequestedBy = requester.Id, Status = WhitelistRequestStatus.Pending };
        db.WhitelistRequests.Add(request);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.ApproveWhitelistRequest(request.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApproveWhitelistRequestResponse>(ok.Value);
        Assert.Equal("Approved", response.Status);

        var reloaded = await db.WhitelistRequests.FindAsync(request.Id);
        Assert.Equal(WhitelistRequestStatus.Approved, reloaded!.Status);
        Assert.Equal(teacher.Id, reloaded.ReviewedBy);

        var site = Assert.Single(await db.WhitelistSites.Where(s => s.CollegeId == requester.CollegeId).ToListAsync());
        Assert.Equal("https://approve-me.example", site.Url);
    }

    // SDA-04
    [Fact]
    public async Task Sda04_ApproveWhitelistRequest_ReturnsConflict_WhenAlreadyReviewed()
    {
        await using var db = NewDb();
        var requester = NewUser(AccountType.Student);
        var teacher = NewUser(AccountType.Teacher, requester.CollegeId);
        db.Users.AddRange(teacher, requester);
        var request = new WhitelistRequest { Id = Guid.NewGuid(), Url = "https://x.example", RequestedBy = requester.Id, Status = WhitelistRequestStatus.Approved, ReviewedBy = teacher.Id };
        db.WhitelistRequests.Add(request);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.ApproveWhitelistRequest(request.Id);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    // SDA-04: a reviewer must not be able to approve — or even detect the existence of —
    // a pending request submitted by a student in a different college.
    [Fact]
    public async Task Sda04_ApproveWhitelistRequest_ReturnsNotFound_ForRequestFromAnotherCollege()
    {
        await using var db = NewDb();
        var requester = NewUser(AccountType.Student);
        var otherCollegeTeacher = NewUser(AccountType.Teacher);
        db.Users.AddRange(requester, otherCollegeTeacher);
        var request = new WhitelistRequest { Id = Guid.NewGuid(), Url = "https://cross-college.example", RequestedBy = requester.Id, Status = WhitelistRequestStatus.Pending };
        db.WhitelistRequests.Add(request);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherCollegeTeacher);
        var result = await controller.ApproveWhitelistRequest(request.Id);

        Assert.IsType<NotFoundResult>(result.Result);
        var reloaded = await db.WhitelistRequests.FindAsync(request.Id);
        Assert.Equal(WhitelistRequestStatus.Pending, reloaded!.Status);
    }

    // SDA-04
    [Fact]
    public async Task Sda04_ApproveWhitelistRequest_ReturnsNotFound_ForUnknownId()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.ApproveWhitelistRequest(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // SDA-04: two pending requests for the same URL both get approved without violating
    // the (college_id, url) unique index on whitelist_sites.
    [Fact]
    public async Task Sda04_ApproveWhitelistRequest_DoesNotDuplicateSite_WhenAlreadyApprovedByAnotherRequest()
    {
        await using var db = NewDb();
        var requesterA = NewUser(AccountType.Student);
        var teacher = NewUser(AccountType.Teacher, requesterA.CollegeId);
        var requesterB = NewUser(AccountType.Student, requesterA.CollegeId);
        db.Users.AddRange(teacher, requesterA, requesterB);
        db.WhitelistSites.Add(new WhitelistSite { Id = Guid.NewGuid(), CollegeId = requesterA.CollegeId, Url = "https://shared.example", ApprovedAt = DateTime.UtcNow });
        var request = new WhitelistRequest { Id = Guid.NewGuid(), Url = "https://shared.example", RequestedBy = requesterB.Id, Status = WhitelistRequestStatus.Pending };
        db.WhitelistRequests.Add(request);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.ApproveWhitelistRequest(request.Id);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(await db.WhitelistSites.Where(s => s.CollegeId == requesterA.CollegeId).ToListAsync());
    }

    // SDA-03
    [Fact]
    public async Task Sda03_AddEngineeringDefaults_ForbidsStudents()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.AddEngineeringDefaults();

        Assert.IsType<ForbidResult>(result.Result);
    }

    // SDA-03
    [Fact]
    public async Task Sda03_AddEngineeringDefaults_AddsAllCuratedSitesForCallersCollege()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.AddEngineeringDefaults();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<AddDefaultSitesResponse>(ok.Value);
        Assert.Empty(response.AlreadyWhitelisted);
        Assert.NotEmpty(response.Added);
        Assert.Equal(response.Added.Count, await db.WhitelistSites.CountAsync(s => s.CollegeId == teacher.CollegeId));
        Assert.Contains(response.Added, site => site.Url == "https://github.com");
    }

    // SDA-03: calling it twice must not duplicate rows or violate the (college_id, url)
    // unique index — the second call reports everything as already whitelisted.
    [Fact]
    public async Task Sda03_AddEngineeringDefaults_IsIdempotent()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var first = await controller.AddEngineeringDefaults();
        var firstCount = ((AddDefaultSitesResponse)((OkObjectResult)first.Result!).Value!).Added.Count;

        var second = await controller.AddEngineeringDefaults();
        var secondResponse = (AddDefaultSitesResponse)((OkObjectResult)second.Result!).Value!;

        Assert.Empty(secondResponse.Added);
        Assert.Equal(firstCount, secondResponse.AlreadyWhitelisted.Count);
        Assert.Equal(firstCount, await db.WhitelistSites.CountAsync(s => s.CollegeId == teacher.CollegeId));
    }

    // SDA-03: college-scoped like every other whitelist_sites write — adding defaults for
    // one college must not create or count rows belonging to another.
    [Fact]
    public async Task Sda03_AddEngineeringDefaults_IsScopedToCallersCollege()
    {
        await using var db = NewDb();
        var teacherA = NewUser(AccountType.Teacher);
        var teacherB = NewUser(AccountType.Teacher);
        db.Users.AddRange(teacherA, teacherB);
        await db.SaveChangesAsync();

        await ControllerAs(db, teacherA).AddEngineeringDefaults();
        var resultB = await ControllerAs(db, teacherB).AddEngineeringDefaults();

        var responseB = (AddDefaultSitesResponse)((OkObjectResult)resultB.Result!).Value!;
        Assert.Empty(responseB.AlreadyWhitelisted);
        Assert.NotEmpty(responseB.Added);
        Assert.Equal(responseB.Added.Count, await db.WhitelistSites.CountAsync(s => s.CollegeId == teacherB.CollegeId));
    }

    // AIS-07: "never shown to the student" — enforced by requiring a teacher/admin caller.
    [Fact]
    public async Task Ais07_SuspiciousFlags_ForbidsStudents()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.SuspiciousFlags(classSessionId: null, assignmentId: Guid.NewGuid());

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Ais07_SuspiciousFlags_RequiresExactlyOneScopeParameter(bool giveClassSession, bool giveAssignment)
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.SuspiciousFlags(
            classSessionId: giveClassSession ? Guid.NewGuid() : null,
            assignmentId: giveAssignment ? Guid.NewGuid() : null);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Ais07_SuspiciousFlags_ForbidsATeacherWhoDoesNotOwnTheAssignment()
    {
        await using var db = NewDb();
        var otherTeacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = Guid.NewGuid(), Title = "A1", Type = AssignmentType.Code, DueDate = DateTime.UtcNow.AddDays(7), SubmissionWindowStart = DateTime.UtcNow, SubmissionWindowEnd = DateTime.UtcNow.AddDays(7) };
        db.Users.Add(otherTeacher);
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherTeacher);
        var result = await controller.SuspiciousFlags(classSessionId: null, assignmentId: assignment.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Ais07_SuspiciousFlags_PersistsFlagsFromAiServices_ForTheAssignmentsTeacher()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var student = NewUser(AccountType.Student);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Code, DueDate = DateTime.UtcNow.AddDays(7), SubmissionWindowStart = DateTime.UtcNow, SubmissionWindowEnd = DateTime.UtcNow.AddDays(7) };
        db.Users.AddRange(teacher, student);
        db.Assignments.Add(assignment);
        db.UsageTelemetries.Add(new UsageTelemetry
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            AssignmentId = assignment.Id,
            EventType = "paste",
            Metadata = "{}",
            RecordedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var fakeAi = new FakeAiServicesClient
        {
            SuspiciousFlagResults = [new(student.Id.ToString(), null, assignment.Id.ToString(), 0.85, ["Rapid paste after long idle"])],
        };
        var controller = ControllerAs(db, teacher, fakeAi);
        var result = await controller.SuspiciousFlags(classSessionId: null, assignmentId: assignment.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var flags = Assert.IsType<List<SuspiciousFlagReportDto>>(ok.Value);
        var flag = Assert.Single(flags);
        Assert.Equal(0.85m, flag.ConfidenceScore);
        Assert.Single(await db.SuspiciousFlags.ToListAsync());
    }

    [Fact]
    public async Task Ais07_SuspiciousFlags_ReCheckReplacesPreviousFlagsForTheSameAssignment()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var student = NewUser(AccountType.Student);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Code, DueDate = DateTime.UtcNow.AddDays(7), SubmissionWindowStart = DateTime.UtcNow, SubmissionWindowEnd = DateTime.UtcNow.AddDays(7) };
        db.Users.AddRange(teacher, student);
        db.Assignments.Add(assignment);
        db.SuspiciousFlags.Add(new SuspiciousFlag { Id = Guid.NewGuid(), StudentId = student.Id, AssignmentId = assignment.Id, ConfidenceScore = 0.99m, FlaggedAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();

        // No telemetry recorded this time, so the AI client won't even be called.
        var controller = ControllerAs(db, teacher, new FakeAiServicesClient());
        var result = await controller.SuspiciousFlags(classSessionId: null, assignmentId: assignment.Id);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(await db.SuspiciousFlags.ToListAsync());
    }

    // AIS-01: "a role without that permission cannot see the summary anywhere, including
    // in the student's own profile view" — no self-view exception, even for Admin viewing
    // their own (irrelevant) summary or a student viewing their own.
    [Fact]
    public async Task Ais01_BrowsingSummary_ForbidsCallersWithoutViewBrowsingHistoryPermission()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.BrowsingSummary(student.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Ais01_BrowsingSummary_ForbidsAStudentFromViewingTheirOwnSummary_WithoutThePermission()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        db.BrowsingHistories.Add(new BrowsingHistory { Id = Guid.NewGuid(), StudentId = student.Id, Url = "https://example.com", VisitedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.BrowsingSummary(student.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Ais01_BrowsingSummary_GeneratesAndPersistsASummary_ForAnAuthorizedCaller()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        // #126: caller and target must share a college — the college-scope guard (fix:
        // enforce college scope on browsing-summary) 404s a cross-college target before any
        // summary is generated. Previously these two got different random colleges from
        // NewUser and this test only passed because that scope check didn't exist yet.
        var student = NewUser(AccountType.Student, admin.CollegeId);
        db.Users.AddRange(admin, student);
        db.PermissionGrants.Add(GrantViewBrowsingHistory(admin.Id));
        db.BrowsingHistories.Add(new BrowsingHistory { Id = Guid.NewGuid(), StudentId = student.Id, Url = "https://example.com", VisitedAt = DateTime.UtcNow, DurationSeconds = 120 });
        await db.SaveChangesAsync();

        var fakeAi = new FakeAiServicesClient { BrowsingSummaryText = "Visited example.com once." };
        var controller = ControllerAs(db, admin, fakeAi);
        var result = await controller.BrowsingSummary(student.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BrowsingSummaryReportDto>(ok.Value);
        Assert.Equal("Visited example.com once.", dto.SummaryText);
        Assert.Single(await db.BrowsingHistorySummaries.Where(s => s.StudentId == student.Id).ToListAsync());
    }

    [Fact]
    public async Task Ais01_BrowsingSummary_ReturnsNotFound_ForUnknownStudent()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        db.PermissionGrants.Add(GrantViewBrowsingHistory(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.BrowsingSummary(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // #126 (fix: enforce college scope on browsing-summary) — a College-A caller holding
    // view_browsing_history must not generate/persist a summary for a College-B student;
    // the cross-college target 404s (existing collapse convention) and nothing is written.
    [Fact]
    public async Task Ais01_BrowsingSummary_ReturnsNotFound_ForStudentInAnotherCollege()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var otherCollegeStudent = NewUser(AccountType.Student);
        db.Users.AddRange(admin, otherCollegeStudent);
        db.PermissionGrants.Add(GrantViewBrowsingHistory(admin.Id));
        db.BrowsingHistories.Add(new BrowsingHistory { Id = Guid.NewGuid(), StudentId = otherCollegeStudent.Id, Url = "https://example.com", VisitedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.BrowsingSummary(otherCollegeStudent.Id);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(await db.BrowsingHistorySummaries.ToListAsync());
    }

    // AIS-01
    [Fact]
    public async Task Ais01_LogBrowsingVisit_ForbidsNonStudents()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.LogBrowsingVisit(new LogBrowsingVisitRequest("https://example.com", null));

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Ais01_LogBrowsingVisit_RejectsEmptyUrl()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.LogBrowsingVisit(new LogBrowsingVisitRequest("   ", null));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Ais01_LogBrowsingVisit_RecordsTheVisitForTheCaller()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.LogBrowsingVisit(new LogBrowsingVisitRequest("https://example.com", 45));

        Assert.IsType<NoContentResult>(result);
        var visit = Assert.Single(await db.BrowsingHistories.Where(v => v.StudentId == student.Id).ToListAsync());
        Assert.Equal("https://example.com", visit.Url);
        Assert.Equal(45, visit.DurationSeconds);
    }

    [Fact]
    public async Task ApproveWhitelistRequest_NotifiesTheOriginalRequester()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var requester = NewUser(AccountType.Student, collegeId);
        var reviewer = NewUser(AccountType.Teacher, collegeId);
        db.Users.AddRange(requester, reviewer);

        var request = new WhitelistRequest
        {
            Id = Guid.NewGuid(),
            Url = "https://example.com",
            RequestedBy = requester.Id,
            Status = WhitelistRequestStatus.Pending,
        };
        db.WhitelistRequests.Add(request);
        await db.SaveChangesAsync();

        var router = new RecordingNotificationRouter();
        var controller = ControllerAs(db, reviewer, router);

        var result = await controller.ApproveWhitelistRequest(request.Id);

        Assert.IsType<OkObjectResult>(result.Result);
        var routed = Assert.Single(router.Routed);
        Assert.Equal(requester.Id, routed.RecipientId);
        Assert.Equal(NotificationType.WhitelistRequest, routed.Type);
    }

    [Fact]
    public async Task ApproveWhitelistRequest_DoesNotNotify_WhenAlreadyReviewed()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var requester = NewUser(AccountType.Student, collegeId);
        var reviewer = NewUser(AccountType.Teacher, collegeId);
        db.Users.AddRange(requester, reviewer);

        var request = new WhitelistRequest
        {
            Id = Guid.NewGuid(),
            Url = "https://example.com",
            RequestedBy = requester.Id,
            Status = WhitelistRequestStatus.Approved,
        };
        db.WhitelistRequests.Add(request);
        await db.SaveChangesAsync();

        var router = new RecordingNotificationRouter();
        var controller = ControllerAs(db, reviewer, router);

        var result = await controller.ApproveWhitelistRequest(request.Id);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(router.Routed);
    }
}
