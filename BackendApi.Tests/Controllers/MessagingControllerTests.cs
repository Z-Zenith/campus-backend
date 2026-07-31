using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Tests.Controllers;

public class MessagingControllerTests
{
    private static AppDbContext NewDb(string dbName) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options);

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

    private static MessagingController ControllerAs(AppDbContext db, User user) => ControllerAs(db, user.Id);

    private static MessagingController ControllerAs(AppDbContext db, Guid userId) => new(db)
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

    private static Notification NewNotification(Guid recipientId, DateTime createdAt, DateTime? readAt = null) => new()
    {
        Id = Guid.NewGuid(),
        RecipientId = recipientId,
        Type = NotificationType.ExitPing,
        Payload = "{\"studentId\":\"" + Guid.NewGuid() + "\"}",
        CreatedAt = createdAt,
        ReadAt = readAt,
    };

    // #159: simulates the exact race CreateThread must survive — two concurrent requests
    // for the same (student, teacher) pair both pass the "does a thread already exist"
    // check before either commits, then one wins the message_threads unique-index race. EF
    // Core's in-memory provider doesn't enforce unique indexes, so the "other request" is
    // simulated by overriding SaveChangesAsync to insert the winning row via a second
    // context sharing the same in-memory database, then throwing DbUpdateException the way
    // a real unique-constraint violation would. Mirrors #94's RaceSimulatingDbContext
    // pattern in CalendarControllerTests.
    private sealed class RaceSimulatingDbContext(DbContextOptions<AppDbContext> options, string dbName) : AppDbContext(options)
    {
        private bool _injected;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var added = ChangeTracker.Entries<MessageThread>()
                .FirstOrDefault(e => e.State == EntityState.Added)?.Entity;

            if (!_injected && added is not null)
            {
                _injected = true;

                await using var winnerDb = NewDb(dbName);
                winnerDb.MessageThreads.Add(new MessageThread
                {
                    Id = Guid.NewGuid(),
                    StudentId = added.StudentId,
                    TeacherId = added.TeacherId,
                    CreatedAt = DateTime.UtcNow,
                });
                await winnerDb.SaveChangesAsync(cancellationToken);

                throw new DbUpdateException("Simulated unique-constraint race on (student_id, teacher_id).");
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Issue159_CreateThread_RecoversFromConcurrentDuplicateInsert()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var seedDb = NewDb(dbName);
        var student = NewUser(AccountType.Student);
        var teacher = NewUser(AccountType.Teacher);
        seedDb.Users.AddRange(student, teacher);
        await seedDb.SaveChangesAsync();

        await using var raceDb = new RaceSimulatingDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, dbName);
        var controller = ControllerAs(raceDb, student);

        var result = await controller.CreateThread(new CreateThreadRequest(student.Id, teacher.Id));

        // Not a 500 — the concurrent-insert race is recovered from with the thread the
        // other request actually persisted, same as BrowsingController.ApproveWhitelistRequest.
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MessageThreadResponse>(ok.Value);
        Assert.Equal(student.Id, dto.StudentId);
        Assert.Equal(teacher.Id, dto.TeacherId);

        await using var verifyDb = NewDb(dbName);
        var threads = await verifyDb.MessageThreads
            .Where(t => t.StudentId == student.Id && t.TeacherId == teacher.Id)
            .ToListAsync();
        // Exactly one thread survives — the winner's row — not a second row from this
        // request's own failed insert.
        Assert.Single(threads);
    }

    [Fact]
    public async Task Issue159_CreateThread_ReturnsExistingThread_WhenAlreadyCreated()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = NewDb(dbName);
        var student = NewUser(AccountType.Student);
        var teacher = NewUser(AccountType.Teacher);
        db.Users.AddRange(student, teacher);
        db.MessageThreads.Add(new MessageThread { Id = Guid.NewGuid(), StudentId = student.Id, TeacherId = teacher.Id, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.CreateThread(new CreateThreadRequest(student.Id, teacher.Id));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(await db.MessageThreads.ToListAsync());
    }

    // #11: AdminTier previously bypassed the participant check unconditionally — an
    // Admin at a different college than the student/teacher must not be able to open a
    // thread between them.
    [Fact]
    public async Task CreateThread_ForbidsAdmin_WhenStudentIsAtADifferentCollege()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var collegeId = Guid.NewGuid();
        var otherCollegeId = Guid.NewGuid();
        var student = NewUser(AccountType.Student, collegeId);
        var teacher = NewUser(AccountType.Teacher, collegeId);
        var admin = NewUser(AccountType.AdminTier, otherCollegeId);
        db.Users.AddRange(student, teacher, admin);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.CreateThread(new CreateThreadRequest(student.Id, teacher.Id));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.MessageThreads.ToListAsync());
    }

    // #11: same college — an Admin should still be able to open a thread for a
    // student/teacher pair within their own institution.
    [Fact]
    public async Task CreateThread_AllowsAdmin_WhenSameCollegeAsBothParticipants()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var collegeId = Guid.NewGuid();
        var student = NewUser(AccountType.Student, collegeId);
        var teacher = NewUser(AccountType.Teacher, collegeId);
        var admin = NewUser(AccountType.AdminTier, collegeId);
        db.Users.AddRange(student, teacher, admin);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.CreateThread(new CreateThreadRequest(student.Id, teacher.Id));

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // #11: same fix on the read side — an Admin bypassing the participant check in
    // ListMessages must still be limited to threads within their own college.
    [Fact]
    public async Task ListMessages_ForbidsAdmin_WhenThreadIsAtADifferentCollege()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var collegeId = Guid.NewGuid();
        var otherCollegeId = Guid.NewGuid();
        var student = NewUser(AccountType.Student, collegeId);
        var teacher = NewUser(AccountType.Teacher, collegeId);
        var admin = NewUser(AccountType.AdminTier, otherCollegeId);
        db.Users.AddRange(student, teacher, admin);
        var thread = new MessageThread { Id = Guid.NewGuid(), StudentId = student.Id, TeacherId = teacher.Id, CreatedAt = DateTime.UtcNow };
        db.MessageThreads.Add(thread);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.ListMessages(thread.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // #11: a thread's student and teacher aren't guaranteed to share a college — a
    // non-admin caller can self-initiate a thread with a counterpart at a different college
    // (CreateThread only rejects a cross-college pairing when the caller is AdminTier).
    // Checking only the student's college here would wrongly admit an Admin who happens to
    // share the student's college but not the teacher's.
    [Fact]
    public async Task ListMessages_ForbidsAdmin_WhenOnlyStudentSharesCallersCollege()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var collegeId = Guid.NewGuid();
        var otherCollegeId = Guid.NewGuid();
        var student = NewUser(AccountType.Student, collegeId);
        var teacher = NewUser(AccountType.Teacher, otherCollegeId);
        var admin = NewUser(AccountType.AdminTier, collegeId);
        db.Users.AddRange(student, teacher, admin);
        var thread = new MessageThread { Id = Guid.NewGuid(), StudentId = student.Id, TeacherId = teacher.Id, CreatedAt = DateTime.UtcNow };
        db.MessageThreads.Add(thread);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.ListMessages(thread.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // #159: ListMessages should paginate rather than always returning every message ever
    // sent in a thread.
    [Fact]
    public async Task Issue159_ListMessages_PaginatesInSentAtOrder()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var student = NewUser(AccountType.Student);
        var teacher = NewUser(AccountType.Teacher);
        db.Users.AddRange(student, teacher);
        var thread = new MessageThread { Id = Guid.NewGuid(), StudentId = student.Id, TeacherId = teacher.Id, CreatedAt = DateTime.UtcNow };
        db.MessageThreads.Add(thread);
        var baseTime = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            db.Messages.Add(new Message
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                SenderId = student.Id,
                Content = $"msg-{i}",
                SentAt = baseTime.AddMinutes(i),
            });
        }
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.ListMessages(thread.Id, page: 2, pageSize: 2);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var messages = Assert.IsType<List<MessageResponse>>(ok.Value);
        Assert.Equal(2, messages.Count);
        Assert.Equal("msg-2", messages[0].Content);
        Assert.Equal("msg-3", messages[1].Content);
    }

    // #159: ListThreads must not require .Include(t => t.Messages) (loading every message
    // in every thread) to determine each thread's last message, and must support pagination.
    [Fact]
    public async Task Issue159_ListThreads_ReturnsLastMessage_AndOrdersMostRecentFirst()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var student = NewUser(AccountType.Student);
        var teacherA = NewUser(AccountType.Teacher);
        var teacherB = NewUser(AccountType.Teacher);
        db.Users.AddRange(student, teacherA, teacherB);

        var threadA = new MessageThread { Id = Guid.NewGuid(), StudentId = student.Id, TeacherId = teacherA.Id, CreatedAt = DateTime.UtcNow.AddHours(-2) };
        var threadB = new MessageThread { Id = Guid.NewGuid(), StudentId = student.Id, TeacherId = teacherB.Id, CreatedAt = DateTime.UtcNow.AddHours(-1) };
        db.MessageThreads.AddRange(threadA, threadB);

        db.Messages.AddRange(
            new Message { Id = Guid.NewGuid(), ThreadId = threadA.Id, SenderId = student.Id, Content = "A-older", SentAt = DateTime.UtcNow.AddMinutes(-30) },
            new Message { Id = Guid.NewGuid(), ThreadId = threadA.Id, SenderId = student.Id, Content = "A-newer", SentAt = DateTime.UtcNow.AddMinutes(-5) });
        // threadB has no messages yet — falls back to CreatedAt for ordering.
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.ListThreads();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summaries = Assert.IsType<List<ThreadSummaryResponse>>(ok.Value);
        Assert.Equal(2, summaries.Count);
        // threadA's last message (5 min ago) is more recent than threadB's CreatedAt (1h ago).
        Assert.Equal(threadA.Id, summaries[0].Id);
        Assert.Equal("A-newer", summaries[0].LastMessage?.Content);
        Assert.Equal(threadB.Id, summaries[1].Id);
        Assert.Null(summaries[1].LastMessage);
    }

    [Fact]
    public async Task Issue159_ListThreads_SupportsPagination()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        for (var i = 0; i < 3; i++)
        {
            var teacher = NewUser(AccountType.Teacher);
            db.Users.Add(teacher);
            db.MessageThreads.Add(new MessageThread { Id = Guid.NewGuid(), StudentId = student.Id, TeacherId = teacher.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-i) });
        }
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var page1 = await controller.ListThreads(page: 1, pageSize: 2);
        var page2 = await controller.ListThreads(page: 2, pageSize: 2);

        var ok1 = Assert.IsType<OkObjectResult>(page1.Result);
        var ok2 = Assert.IsType<OkObjectResult>(page2.Result);
        Assert.Equal(2, Assert.IsType<List<ThreadSummaryResponse>>(ok1.Value).Count);
        Assert.Single(Assert.IsType<List<ThreadSummaryResponse>>(ok2.Value));
    }

    // Notification Router (shared) — GET /notifications and the mark-as-read endpoint.
    // NotificationRouterTests (Services) covers RouteAsync's persistence + SignalR push;
    // these tests cover the read side: a caller only ever sees/marks their own rows.
    [Fact]
    public async Task ListNotifications_ReturnsOnlyTheCallersNotifications_MostRecentFirst()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var caller = NewUser(AccountType.Student);
        var other = NewUser(AccountType.Student);
        db.Users.AddRange(caller, other);

        var older = NewNotification(caller.Id, DateTime.UtcNow.AddMinutes(-10));
        var newer = NewNotification(caller.Id, DateTime.UtcNow);
        var othersNotification = NewNotification(other.Id, DateTime.UtcNow);
        db.Notifications.AddRange(older, newer, othersNotification);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller.Id);
        var result = await controller.ListNotifications();

        var page = Assert.IsType<OkObjectResult>(result.Result).Value as Contracts.NotificationsPageResponse;
        Assert.NotNull(page);
        Assert.Equal(2, page!.TotalCount);
        Assert.Equal(2, page.Notifications.Count);
        Assert.Equal(newer.Id, page.Notifications[0].Id);
        Assert.Equal(older.Id, page.Notifications[1].Id);
        Assert.DoesNotContain(page.Notifications, n => n.Id == othersNotification.Id);
    }

    [Fact]
    public async Task ListNotifications_Paginates()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var caller = NewUser(AccountType.Student);
        db.Users.Add(caller);

        for (var i = 0; i < 5; i++)
        {
            db.Notifications.Add(NewNotification(caller.Id, DateTime.UtcNow.AddMinutes(-i)));
        }
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller.Id);
        var firstPage = await controller.ListNotifications(page: 1, pageSize: 2);
        var firstPageBody = Assert.IsType<OkObjectResult>(firstPage.Result).Value as Contracts.NotificationsPageResponse;
        Assert.NotNull(firstPageBody);
        Assert.Equal(5, firstPageBody!.TotalCount);
        Assert.Equal(2, firstPageBody.Notifications.Count);

        var secondPage = await controller.ListNotifications(page: 2, pageSize: 2);
        var secondPageBody = Assert.IsType<OkObjectResult>(secondPage.Result).Value as Contracts.NotificationsPageResponse;
        Assert.NotNull(secondPageBody);
        Assert.Equal(2, secondPageBody!.Notifications.Count);
        Assert.DoesNotContain(secondPageBody.Notifications, n => firstPageBody.Notifications.Select(x => x.Id).Contains(n.Id));
    }

    [Fact]
    public async Task ListNotifications_ReportsIsReadFromReadAt()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var caller = NewUser(AccountType.Student);
        db.Users.Add(caller);
        var read = NewNotification(caller.Id, DateTime.UtcNow, readAt: DateTime.UtcNow);
        var unread = NewNotification(caller.Id, DateTime.UtcNow.AddMinutes(-1));
        db.Notifications.AddRange(read, unread);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller.Id);
        var result = await controller.ListNotifications();
        var page = Assert.IsType<OkObjectResult>(result.Result).Value as Contracts.NotificationsPageResponse;

        Assert.True(page!.Notifications.Single(n => n.Id == read.Id).IsRead);
        Assert.False(page.Notifications.Single(n => n.Id == unread.Id).IsRead);
    }

    [Fact]
    public async Task MarkNotificationRead_MarksTheCallersOwnNotification_AndIsIdempotent()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var caller = NewUser(AccountType.Student);
        db.Users.Add(caller);
        var notification = NewNotification(caller.Id, DateTime.UtcNow);
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller.Id);
        var firstResult = await controller.MarkNotificationRead(notification.Id);
        var firstBody = Assert.IsType<OkObjectResult>(firstResult.Result).Value as Contracts.MarkNotificationReadResponse;
        Assert.NotNull(firstBody);

        var secondResult = await controller.MarkNotificationRead(notification.Id);
        var secondBody = Assert.IsType<OkObjectResult>(secondResult.Result).Value as Contracts.MarkNotificationReadResponse;

        // Idempotent: the second call doesn't overwrite the original ReadAt.
        Assert.Equal(firstBody!.ReadAt, secondBody!.ReadAt);
    }

    [Fact]
    public async Task MarkNotificationRead_ReturnsNotFound_ForAnotherUsersNotification()
    {
        await using var db = NewDb(Guid.NewGuid().ToString());
        var owner = NewUser(AccountType.Student);
        var intruder = NewUser(AccountType.Student);
        db.Users.AddRange(owner, intruder);
        var notification = NewNotification(owner.Id, DateTime.UtcNow);
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, intruder.Id);
        var result = await controller.MarkNotificationRead(notification.Id);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Null((await db.Notifications.FindAsync(notification.Id))!.ReadAt);
    }
}
