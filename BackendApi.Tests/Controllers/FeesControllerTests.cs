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

public class FeesControllerTests
{
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

    private static async Task GrantManageFeesViaRoleAsync(AppDbContext db, Guid userId)
    {
        db.Roles.Add(new Role { Code = "finance" });
        db.Permissions.Add(new Permission { Code = "manage_fees", Description = "x" });
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = userId, RoleCode = "finance", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var role = await db.Roles.FindAsync("finance");
        var permission = await db.Permissions.FindAsync("manage_fees");
        role!.PermissionCodes.Add(permission!);
        await db.SaveChangesAsync();
    }

    private static PermissionGrant GrantManageFees(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        PermissionCode = "manage_fees",
        Granted = true,
        GrantedBy = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    // Empty by default — FeesController.SendReminders falls back to its own hardcoded
    // default (3 days) via IConfiguration.GetValue's fallback, same as production config
    // resolution when a key is absent.
    private static FeesController ControllerAs(AppDbContext db, User user, IConfiguration? configuration = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new FeesController(db, new PermissionService(db), configuration ?? new ConfigurationBuilder().Build(), new CollegeScopeService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private static ClaimsPrincipal ParentPrincipal(Guid userId, Guid sessionId, Guid wardId) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("account_type", nameof(AccountType.Parent)),
            new Claim("ward_id", wardId.ToString()),
            new Claim("session_id", sessionId.ToString()),
        ], "TestAuth"));

    private static FeesController ControllerAs(AppDbContext db, ClaimsPrincipal principal) =>
        new(db, new PermissionService(db), new ConfigurationBuilder().Build(), new CollegeScopeService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };

    private static async Task<(Guid parentId, Guid studentId, Guid sessionId, Guid feeId)> SeedAsync(AppDbContext db)
    {
        var collegeId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var feeId = Guid.NewGuid();

        db.Users.Add(new User { Id = parentId, CollegeId = collegeId, Identifier = "parent-1", PasswordHash = "hash", FullName = "Parent", IsActive = true, AccountType = AccountType.Parent });
        db.Users.Add(new User { Id = studentId, CollegeId = collegeId, Identifier = "student-1", PasswordHash = "hash", FullName = "Student", IsActive = true, AccountType = AccountType.Student });
        db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parentId, StudentId = studentId });
        db.UserSessions.Add(new UserSession { Id = sessionId, UserId = parentId, IsActive = true });
        db.FeeRecords.Add(new FeeRecord { Id = feeId, StudentId = studentId, Amount = 100, DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), Status = FeeStatus.Pending });

        await db.SaveChangesAsync();
        return (parentId, studentId, sessionId, feeId);
    }

    // #93: FeesController.Pay must return NotFound (not Forbid) when the fee simply doesn't
    // exist — this is the "existence" half of the standardized convention.
    [Fact]
    public async Task Pay_ReturnsNotFound_WhenFeeDoesNotExist()
    {
        await using var db = NewDb();
        var (parentId, studentId, sessionId, _) = await SeedAsync(db);
        var controller = ControllerAs(db, ParentPrincipal(parentId, sessionId, studentId));

        var result = await controller.Pay(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // #93: and also NotFound (not Forbid) when the fee exists but belongs to a different
    // ward than the caller's — the two cases must be indistinguishable to the caller.
    [Fact]
    public async Task Pay_ReturnsNotFound_WhenFeeBelongsToADifferentWard()
    {
        await using var db = NewDb();
        var (parentId, studentId, sessionId, feeId) = await SeedAsync(db);
        var otherWardId = Guid.NewGuid();
        var controller = ControllerAs(db, ParentPrincipal(parentId, sessionId, otherWardId));

        var result = await controller.Pay(feeId);

        Assert.IsType<NotFoundResult>(result.Result);
        var fee = await db.FeeRecords.FindAsync(feeId);
        Assert.Equal(FeeStatus.Pending, fee!.Status);
    }

    // Pay()'s success path uses ExecuteUpdateAsync, which the EF Core in-memory provider
    // doesn't support (pre-existing limitation, unrelated to #93) — so only the two
    // NotFound-before-that-point cases above are exercised here; Pay()'s happy path isn't
    // unit-testable against this provider.

    private static IConfiguration ReminderConfig(int daysBeforeDue) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([new("FeeReminder:DaysBeforeDue", daysBeforeDue.ToString())])
            .Build();

    [Fact]
    public async Task CreateLink_SucceedsForSameCollegeStudent()
    {
        await using var db = NewDb();
        var college = Guid.NewGuid();
        var caller = NewUser(AccountType.AdminTier, college);
        var student = NewUser(AccountType.Student, college);
        db.Users.AddRange(caller, student);
        await GrantManageFeesViaRoleAsync(db, caller.Id);

        var controller = ControllerAs(db, caller);
        var result = await controller.CreateLink(new CreateFeeLinkRequest(student.Id, 5000m, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // #129 — manage_fees is checked globally; without a CollegeId check, a caller at one
    // college could create a fee link (and payment obligation) against a student at a
    // different college.
    [Fact]
    public async Task CreateLink_ForbidsCrossCollegeStudent()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier, Guid.NewGuid());
        var student = NewUser(AccountType.Student, Guid.NewGuid()); // different college
        db.Users.AddRange(caller, student);
        await GrantManageFeesViaRoleAsync(db, caller.Id);

        var controller = ControllerAs(db, caller);
        var result = await controller.CreateLink(new CreateFeeLinkRequest(student.Id, 5000m, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.FeeRecords.ToListAsync());
    }

    // AWA-04
    [Fact]
    public async Task Awa04_CreateLink_ForbidsCallersWithoutManageFeesPermission()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var student = NewUser(AccountType.Student);
        db.Users.AddRange(teacher, student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.CreateLink(new CreateFeeLinkRequest(student.Id, 5000m, new DateOnly(2026, 8, 1)));

        Assert.IsType<ForbidResult>(result.Result);
    }

    // AWA-04
    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task Awa04_CreateLink_RejectsNonPositiveAmount(decimal amount)
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student);
        db.Users.AddRange(admin, student);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.CreateLink(new CreateFeeLinkRequest(student.Id, amount, new DateOnly(2026, 8, 1)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // AWA-04
    [Fact]
    public async Task Awa04_CreateLink_RejectsWhenTargetIsNotAStudentAccount()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var notAStudent = NewUser(AccountType.Teacher);
        db.Users.AddRange(admin, notAStudent);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.CreateLink(new CreateFeeLinkRequest(notAStudent.Id, 5000m, new DateOnly(2026, 8, 1)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // AWA-04
    [Fact]
    public async Task Awa04_CreateLink_RejectsUnknownStudentId()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.CreateLink(new CreateFeeLinkRequest(Guid.NewGuid(), 5000m, new DateOnly(2026, 8, 1)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // AWA-04
    [Fact]
    public async Task Awa04_CreateLink_RejectsAbsurdlyLargeAmount()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student);
        db.Users.AddRange(admin, student);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.CreateLink(new CreateFeeLinkRequest(student.Id, 50_000_000m, new DateOnly(2026, 8, 1)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // AWA-04
    [Theory]
    [InlineData(2020, 1, 1)] // clearly in the past
    public async Task Awa04_CreateLink_RejectsPastDueDate(int year, int month, int day)
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student, admin.CollegeId);
        db.Users.AddRange(admin, student);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.CreateLink(new CreateFeeLinkRequest(student.Id, 5000m, new DateOnly(year, month, day)));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // AWA-04
    [Fact]
    public async Task Awa04_CreateLink_RejectsDefaultDueDate()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student, admin.CollegeId);
        db.Users.AddRange(admin, student);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.CreateLink(new CreateFeeLinkRequest(student.Id, 5000m, default));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // AWA-04: acceptance-critical — the link must resolve to exactly the amount/period
    // it was generated for, which is why each call mints its own FeeRecord rather than
    // reusing/mutating an existing one.
    [Fact]
    public async Task Awa04_CreateLink_CreatesFeeRecordWithLinkBoundToExactAmountAndDueDate()
    {
        await using var db = NewDb();
        var finance = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student, finance.CollegeId);
        db.Users.AddRange(finance, student);
        db.PermissionGrants.Add(GrantManageFees(finance.Id));
        await db.SaveChangesAsync();

        var dueDate = new DateOnly(2026, 8, 1);
        var controller = ControllerAs(db, finance);
        var result = await controller.CreateLink(new CreateFeeLinkRequest(student.Id, 7500m, dueDate));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<FeeLinkResponse>(ok.Value);
        Assert.Equal(7500m, response.Amount);
        Assert.Equal(dueDate, response.DueDate);
        Assert.Equal("Pending", response.Status);
        Assert.Contains(response.FeeRecordId.ToString(), response.PaymentLink);

        var stored = await db.FeeRecords.FindAsync(response.FeeRecordId);
        Assert.NotNull(stored);
        Assert.Equal(student.Id, stored!.StudentId);
        Assert.Equal(7500m, stored.Amount);
        Assert.Equal(dueDate, stored.DueDate);
        Assert.Equal(response.PaymentLink, stored.PaymentLink);
    }

    // AWA-04
    [Fact]
    public async Task Awa04_CreateLink_TwoLinksForSameStudent_GetIndependentRecordsAndLinks()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student, admin.CollegeId);
        db.Users.AddRange(admin, student);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var first = await controller.CreateLink(new CreateFeeLinkRequest(student.Id, 1000m, new DateOnly(2026, 8, 1)));
        var second = await controller.CreateLink(new CreateFeeLinkRequest(student.Id, 2000m, new DateOnly(2026, 9, 1)));

        var firstResponse = (FeeLinkResponse)((OkObjectResult)first.Result!).Value!;
        var secondResponse = (FeeLinkResponse)((OkObjectResult)second.Result!).Value!;
        Assert.NotEqual(firstResponse.FeeRecordId, secondResponse.FeeRecordId);
        Assert.NotEqual(firstResponse.PaymentLink, secondResponse.PaymentLink);
        Assert.Equal(2, await db.FeeRecords.CountAsync(f => f.StudentId == student.Id));
    }

    // AWA-05
    [Fact]
    public async Task Awa05_SendReminders_ForbidsCallersWithoutManageFeesPermission()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.SendReminders();

        Assert.IsType<ForbidResult>(result.Result);
    }

    // AWA-05: acceptance-critical — "when a fee due date approaches, notify the parent".
    [Fact]
    public async Task Awa05_SendReminders_NotifiesParentOfFeeDueWithinWindow()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student);
        var parent = NewUser(AccountType.Parent);
        db.Users.AddRange(admin, student, parent);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parent.Id, StudentId = student.Id, CreatedAt = DateTime.UtcNow });
        db.FeeRecords.Add(new FeeRecord
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            Amount = 5000m,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)),
            Status = FeeStatus.Pending,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.SendReminders();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SendFeeRemindersResponse>(ok.Value);
        Assert.Equal(1, response.RemindersSent);
        var notification = Assert.Single(await db.Notifications.Where(n => n.RecipientId == parent.Id).ToListAsync());
        Assert.Equal(NotificationType.FeeReminder, notification.Type);
    }

    // AWA-05: a fee due well outside the reminder window shouldn't notify yet.
    [Fact]
    public async Task Awa05_SendReminders_DoesNotNotifyForFeeOutsideWindow()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student);
        var parent = NewUser(AccountType.Parent);
        db.Users.AddRange(admin, student, parent);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parent.Id, StudentId = student.Id, CreatedAt = DateTime.UtcNow });
        db.FeeRecords.Add(new FeeRecord
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            Amount = 5000m,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
            Status = FeeStatus.Pending,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.SendReminders();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SendFeeRemindersResponse>(ok.Value);
        Assert.Equal(0, response.RemindersSent);
        Assert.Empty(await db.Notifications.ToListAsync());
    }

    // AWA-05: an overdue (already past due) fee is outside the "approaching" window by
    // design — this locks in that scope decision rather than leaving it implicit.
    [Fact]
    public async Task Awa05_SendReminders_DoesNotNotifyForOverdueFee()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student);
        var parent = NewUser(AccountType.Parent);
        db.Users.AddRange(admin, student, parent);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parent.Id, StudentId = student.Id, CreatedAt = DateTime.UtcNow });
        db.FeeRecords.Add(new FeeRecord
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            Amount = 5000m,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            Status = FeeStatus.Pending,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.SendReminders();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SendFeeRemindersResponse>(ok.Value);
        Assert.Equal(0, response.RemindersSent);
    }

    // AWA-05
    [Fact]
    public async Task Awa05_SendReminders_DoesNotNotifyForAlreadyPaidFee()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student);
        var parent = NewUser(AccountType.Parent);
        db.Users.AddRange(admin, student, parent);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parent.Id, StudentId = student.Id, CreatedAt = DateTime.UtcNow });
        db.FeeRecords.Add(new FeeRecord
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            Amount = 5000m,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
            Status = FeeStatus.Paid,
            PaidAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.SendReminders();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<SendFeeRemindersResponse>(ok.Value);
        Assert.Equal(0, response.RemindersSent);
    }

    // AWA-05: acceptance-critical — "reminder fires at a configurable number of days
    // before the due date".
    [Fact]
    public async Task Awa05_SendReminders_RespectsConfigurableDaysBeforeDue()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student);
        var parent = NewUser(AccountType.Parent);
        db.Users.AddRange(admin, student, parent);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parent.Id, StudentId = student.Id, CreatedAt = DateTime.UtcNow });
        db.FeeRecords.Add(new FeeRecord
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            Amount = 5000m,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            Status = FeeStatus.Pending,
        });
        await db.SaveChangesAsync();

        // Default window (3 days) misses a fee due in 10 days...
        var defaultController = ControllerAs(db, admin);
        var defaultResult = await defaultController.SendReminders();
        var defaultResponse = Assert.IsType<SendFeeRemindersResponse>(Assert.IsType<OkObjectResult>(defaultResult.Result).Value);
        Assert.Equal(0, defaultResponse.RemindersSent);

        // ...but a wider configured window catches it.
        var widerController = ControllerAs(db, admin, ReminderConfig(daysBeforeDue: 14));
        var widerResult = await widerController.SendReminders();
        var widerResponse = Assert.IsType<SendFeeRemindersResponse>(Assert.IsType<OkObjectResult>(widerResult.Result).Value);
        Assert.Equal(1, widerResponse.RemindersSent);
    }

    // AWA-05: re-invoking (e.g. a daily cron calling this endpoint) must not spam the
    // same parent with duplicate reminders on the same day.
    [Fact]
    public async Task Awa05_SendReminders_IsIdempotentWithinTheSameDay()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student);
        var parent = NewUser(AccountType.Parent);
        db.Users.AddRange(admin, student, parent);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parent.Id, StudentId = student.Id, CreatedAt = DateTime.UtcNow });
        db.FeeRecords.Add(new FeeRecord
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            Amount = 5000m,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
            Status = FeeStatus.Pending,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var first = await controller.SendReminders();
        var second = await controller.SendReminders();

        Assert.Equal(1, ((SendFeeRemindersResponse)((OkObjectResult)first.Result!).Value!).RemindersSent);
        Assert.Equal(0, ((SendFeeRemindersResponse)((OkObjectResult)second.Result!).Value!).RemindersSent);
        Assert.Single(await db.Notifications.Where(n => n.RecipientId == parent.Id).ToListAsync());
    }

    // AWA-05: a parent with multiple wards (or one ward with multiple due fees) gets one
    // combined notification per invocation, not one per fee.
    [Fact]
    public async Task Awa05_SendReminders_CombinesMultipleDueFeesForSameParentIntoOneNotification()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student);
        var parent = NewUser(AccountType.Parent);
        db.Users.AddRange(admin, student, parent);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parent.Id, StudentId = student.Id, CreatedAt = DateTime.UtcNow });
        db.FeeRecords.AddRange(
            new FeeRecord { Id = Guid.NewGuid(), StudentId = student.Id, Amount = 1000m, DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), Status = FeeStatus.Pending },
            new FeeRecord { Id = Guid.NewGuid(), StudentId = student.Id, Amount = 2000m, DueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), Status = FeeStatus.Pending });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.SendReminders();

        var response = Assert.IsType<SendFeeRemindersResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, response.RemindersSent);
        Assert.Single(await db.Notifications.Where(n => n.RecipientId == parent.Id).ToListAsync());
    }

    // Admin-facing list — there was no way to see existing fee links at all before this.
    [Fact]
    public async Task List_ForbidsCallerWithoutManageFeesPermission()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        db.Users.Add(caller);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.List();

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task List_ReturnsOnlyCallersCollegeFeeRecords()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var ownStudent = NewUser(AccountType.Student, admin.CollegeId);
        var otherCollegeStudent = NewUser(AccountType.Student);
        db.Users.AddRange(admin, ownStudent, otherCollegeStudent);
        db.PermissionGrants.Add(GrantManageFees(admin.Id));
        db.FeeRecords.AddRange(
            new FeeRecord { Id = Guid.NewGuid(), StudentId = ownStudent.Id, Amount = 1000m, DueDate = DateOnly.FromDateTime(DateTime.UtcNow), Status = FeeStatus.Pending },
            new FeeRecord { Id = Guid.NewGuid(), StudentId = otherCollegeStudent.Id, Amount = 2000m, DueDate = DateOnly.FromDateTime(DateTime.UtcNow), Status = FeeStatus.Pending });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.List();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var fees = Assert.IsType<List<FeeRecordDto>>(ok.Value);
        Assert.Single(fees);
        Assert.Equal(ownStudent.Id, fees[0].StudentId);
        Assert.Equal(ownStudent.FullName, fees[0].StudentFullName);
    }
}
