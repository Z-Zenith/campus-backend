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

public class CalendarControllerTests
{
    // Grants "create_event" unconditionally — no test in this file needs department-scoped
    // permission checks.
    private class AllowingPermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, string permissionCode) => Task.FromResult(true);
        public Task<Guid?> GetDepartmentScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
        public Task<List<string>> GetEffectivePermissionsAsync(Guid userId, IEnumerable<string> candidateCodes) => Task.FromResult(candidateCodes.ToList());
    }

    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static User NewUser(AccountType accountType) => new()
    {
        Id = Guid.NewGuid(),
        CollegeId = Guid.NewGuid(),
        Identifier = $"user-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        FullName = "Test User",
        AccountType = accountType,
        IsActive = true,
    };

    private static CalendarController ControllerAs(AppDbContext db, User user) => new(db, new AllowingPermissionService())
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

    // #159: CreateEvent had no validation at all that EndTime came after StartTime.
    [Fact]
    public async Task Issue159_CreateEvent_RejectsEndTimeAtOrBeforeStartTime()
    {
        await using var db = NewDb();
        var creator = NewUser(AccountType.AdminTier);
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, creator);
        var start = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var result = await controller.CreateEvent(new CreateEventRequest("Orientation", start, start, null, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.Events.ToListAsync());
    }

    [Fact]
    public async Task Issue159_CreateEvent_RejectsEndTimeBeforeStartTime()
    {
        await using var db = NewDb();
        var creator = NewUser(AccountType.AdminTier);
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, creator);
        var start = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var result = await controller.CreateEvent(new CreateEventRequest("Orientation", start, start.AddHours(-1), null, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.Events.ToListAsync());
    }

    [Fact]
    public async Task Issue159_CreateEvent_AllowsValidTimeRange()
    {
        await using var db = NewDb();
        var creator = NewUser(AccountType.AdminTier);
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, creator);
        var start = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var result = await controller.CreateEvent(new CreateEventRequest("Orientation", start, start.AddHours(1), null, null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<EventDto>(ok.Value);
        Assert.Single(await db.Events.ToListAsync());
    }

    // #159: an undated todo used to be mapped to DateTime.MinValue (0001-01-01), rendering
    // as a ~2000-years-overdue calendar item. It should be omitted from the dated calendar
    // instead.
    [Fact]
    public async Task Issue159_MyCalendar_OmitsUndatedTodos()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        db.Todos.Add(new Todo { Id = Guid.NewGuid(), StudentId = student.Id, Title = "No due date", DueDate = null });
        var dated = new Todo { Id = Guid.NewGuid(), StudentId = student.Id, Title = "Has a due date", DueDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.Todos.Add(dated);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.MyCalendar();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<MyCalendarResponse>(ok.Value);
        var todoItems = response.Items.Where(i => i.Kind == "todo").ToList();
        var item = Assert.Single(todoItems);
        Assert.Equal(dated.Id, item.Id);
        Assert.NotEqual(DateTime.MinValue, item.Start);
    }

    private static AppDbContext NewDb(string dbName) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options);

    private class FakePermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, string permissionCode) => Task.FromResult(false);
        public Task<Guid?> GetDepartmentScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
        public Task<List<string>> GetEffectivePermissionsAsync(Guid userId, IEnumerable<string> candidateCodes) => Task.FromResult(new List<string>());
    }

    // #94: simulates the exact race CalendarController.RegisterForEvent must survive — two
    // concurrent requests for the same (event, student) both pass the "does a registration
    // already exist" check before either commits, then one of them wins the unique-index
    // race. EF Core's in-memory provider doesn't enforce unique indexes (verified: a plain
    // duplicate insert does not throw), so the "other request" is simulated by overriding
    // SaveChangesAsync to insert the winning row via a second context sharing the same
    // in-memory database, then throwing DbUpdateException the way a real unique-constraint
    // violation would.
    private sealed class RaceSimulatingDbContext(DbContextOptions<AppDbContext> options, string dbName) : AppDbContext(options)
    {
        private bool _injected;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var added = ChangeTracker.Entries<EventRegistration>()
                .FirstOrDefault(e => e.State == EntityState.Added)?.Entity;

            if (!_injected && added is not null)
            {
                _injected = true;

                await using var winnerDb = NewDb(dbName);
                winnerDb.EventRegistrations.Add(new EventRegistration
                {
                    Id = Guid.NewGuid(),
                    EventId = added.EventId,
                    StudentId = added.StudentId,
                    RegisteredAt = DateTime.UtcNow,
                });
                await winnerDb.SaveChangesAsync(cancellationToken);

                throw new DbUpdateException("Simulated unique-constraint race on (event_id, student_id).");
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed record Fixture(Guid StudentId, Guid EventId);

    private static async Task<Fixture> SeedAsync(AppDbContext db)
    {
        var collegeId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        db.Departments.Add(new Department { Id = departmentId, CollegeId = collegeId, Name = "CS" });
        db.Sections.Add(new Section { Id = sectionId, DepartmentId = departmentId, Year = 1, Name = "A" });
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
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = sectionId, StudentId = studentId });
        db.Events.Add(new Event
        {
            Id = eventId,
            CollegeId = collegeId,
            Title = "Fest",
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            CreatedBy = studentId,
        });

        await db.SaveChangesAsync();
        return new Fixture(studentId, eventId);
    }

    private static CalendarController ControllerAs(AppDbContext db, Guid studentId) =>
        new(db, new FakePermissionService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, studentId.ToString())], "TestAuth")),
                },
            },
        };

    [Fact]
    public async Task RegisterForEvent_RecoversFromConcurrentDuplicateInsert()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var seedDb = NewDb(dbName);
        var fixture = await SeedAsync(seedDb);

        await using var raceDb = new RaceSimulatingDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, dbName);
        var controller = ControllerAs(raceDb, fixture.StudentId);

        var result = await controller.RegisterForEvent(fixture.EventId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);

        await using var verifyDb = NewDb(dbName);
        var registrations = await verifyDb.EventRegistrations
            .Where(r => r.EventId == fixture.EventId && r.StudentId == fixture.StudentId)
            .ToListAsync();
        // Exactly one registration survives — the "winner's" row — not a second row from
        // this request's own (failed) insert.
        Assert.Single(registrations);
    }

    [Fact]
    public async Task RegisterForEvent_ReturnsExistingRegistration_WhenAlreadyRegistered()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var db = NewDb(dbName);
        var fixture = await SeedAsync(db);
        db.EventRegistrations.Add(new EventRegistration { Id = Guid.NewGuid(), EventId = fixture.EventId, StudentId = fixture.StudentId });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, fixture.StudentId);
        var result = await controller.RegisterForEvent(fixture.EventId);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(await db.EventRegistrations.ToListAsync());
    }

    // SDA-14: personal to-do CRUD — student-owned, no permission check beyond "it's mine".
    [Fact]
    public async Task CreateTodo_ThenSetComplete_ThenDelete_RoundTrips()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var created = await controller.CreateTodo(new CreateTodoRequest("Finish lab report", DateTime.UtcNow.AddDays(2)));
        var todo = Assert.IsType<TodoDto>(Assert.IsType<OkObjectResult>(created.Result).Value);
        Assert.False(todo.Completed);

        var completed = await controller.SetTodoComplete(todo.Id, new SetTodoCompleteRequest(true));
        var updated = Assert.IsType<TodoDto>(Assert.IsType<OkObjectResult>(completed.Result).Value);
        Assert.True(updated.Completed);

        var deleted = await controller.DeleteTodo(todo.Id);
        Assert.IsType<NoContentResult>(deleted);
        Assert.Empty(await db.Todos.ToListAsync());
    }

    [Fact]
    public async Task SetTodoComplete_ReturnsNotFound_ForAnotherStudentsTodo()
    {
        await using var db = NewDb();
        var owner = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        db.Users.AddRange(owner, otherStudent);
        var todo = new Todo { Id = Guid.NewGuid(), StudentId = owner.Id, Title = "Owner's todo", Completed = false };
        db.Todos.Add(todo);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherStudent);
        var result = await controller.SetTodoComplete(todo.Id, new SetTodoCompleteRequest(true));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateCustomEntry_ThenDelete_RoundTrips()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var created = await controller.CreateCustomEntry(new CreateCustomCalendarEntryRequest("Study group", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3))));
        var entry = Assert.IsType<CustomCalendarEntryDto>(Assert.IsType<OkObjectResult>(created.Result).Value);

        var deleted = await controller.DeleteCustomEntry(entry.Id);
        Assert.IsType<NoContentResult>(deleted);
        Assert.Empty(await db.CustomCalendarEntries.ToListAsync());
    }
}
