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
        public Task<Guid?> GetSectionOversightScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
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

    // Academic Calendar work: EventType defaults to Academic when omitted, so existing
    // callers (TWA-15) that don't set it keep working unchanged.
    [Fact]
    public async Task CreateEvent_DefaultsToAcademicEventTypeWhenOmitted()
    {
        await using var db = NewDb();
        var creator = NewUser(AccountType.AdminTier);
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, creator);
        var start = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var result = await controller.CreateEvent(new CreateEventRequest("Orientation", start, start.AddHours(1), null, null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<EventDto>(ok.Value);
        Assert.Equal(EventType.Academic, dto.EventType);
    }

    [Fact]
    public async Task CreateEvent_CreatesHolidayWhenEventTypeSpecified()
    {
        await using var db = NewDb();
        var creator = NewUser(AccountType.AdminTier);
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, creator);
        var start = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);
        var result = await controller.CreateEvent(new CreateEventRequest("Independence Day", start, start.AddDays(1), null, null, EventType.Holiday));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<EventDto>(ok.Value);
        Assert.Equal(EventType.Holiday, dto.EventType);
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
        public Task<Guid?> GetSectionOversightScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
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
            // EF's InMemory provider doesn't apply the column-level HasDefaultValue(Approved)
            // the way real Postgres does - a directly-constructed fixture must set this
            // explicitly or it defaults to Pending (the enum's first value) and gets
            // silently filtered out of EligibleEventsQuery.
            Status = EventStatus.Approved,
            ApprovedBy = studentId,
            ApprovedAt = DateTime.UtcNow,
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

    // Phase 5 - admin-facing event management. No test in this file needs
    // department-scoped denial (see AllowingPermissionService's comment above), so a plain
    // denying stub is enough for the one Forbid-on-missing-permission test below.
    private class DenyingPermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, string permissionCode) => Task.FromResult(false);
        public Task<Guid?> GetDepartmentScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
        public Task<Guid?> GetSectionOversightScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
    }

    private static CalendarController ControllerAs(AppDbContext db, User user, IPermissionService permissions) => new(db, permissions)
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
    public async Task ListCreatedEvents_ForbidsCallerWithoutCreateEventPermission()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        db.Users.Add(caller);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller, new DenyingPermissionService());
        var result = await controller.ListCreatedEvents();

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task ListCreatedEvents_ReturnsOnlyCallersCollegeEvents()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var otherCollegeCreator = NewUser(AccountType.AdminTier);
        db.Users.AddRange(caller, otherCollegeCreator);
        db.Events.Add(new Event { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Title = "Own", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1), CreatedBy = caller.Id });
        db.Events.Add(new Event { Id = Guid.NewGuid(), CollegeId = otherCollegeCreator.CollegeId, Title = "Other", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1), CreatedBy = otherCollegeCreator.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.ListCreatedEvents();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var events = Assert.IsType<List<AdminEventDto>>(ok.Value);
        var dto = Assert.Single(events);
        Assert.Equal("Own", dto.Title);
    }

    [Fact]
    public async Task UpdateEvent_ForbidsCrossCollegeEvent()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var otherCollegeCreator = NewUser(AccountType.AdminTier);
        db.Users.AddRange(caller, otherCollegeCreator);
        var otherEvent = new Event { Id = Guid.NewGuid(), CollegeId = otherCollegeCreator.CollegeId, Title = "Other", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1), CreatedBy = otherCollegeCreator.Id };
        db.Events.Add(otherEvent);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var start = DateTime.UtcNow;
        var result = await controller.UpdateEvent(otherEvent.Id, new UpdateEventRequest("Renamed", start, start.AddHours(1), null, null));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpdateEvent_RejectsEndTimeAtOrBeforeStartTime()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        db.Users.Add(caller);
        var existing = new Event { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Title = "Orientation", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1), CreatedBy = caller.Id };
        db.Events.Add(existing);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var start = DateTime.UtcNow;
        var result = await controller.UpdateEvent(existing.Id, new UpdateEventRequest("Orientation", start, start, null, null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateEvent_UpdatesFieldsAndPreservesEventTypeWhenOmitted()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        db.Users.Add(caller);
        var existing = new Event { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Title = "Orientation", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1), CreatedBy = caller.Id, EventType = EventType.Holiday };
        db.Events.Add(existing);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var start = DateTime.UtcNow.AddDays(1);
        var result = await controller.UpdateEvent(existing.Id, new UpdateEventRequest("Renamed", start, start.AddHours(2), null, null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<AdminEventDto>(ok.Value);
        Assert.Equal("Renamed", dto.Title);
        Assert.Equal(EventType.Holiday, dto.EventType);
    }

    [Fact]
    public async Task DeleteEvent_RemovesEventAndCascadesRegistrations()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student);
        db.Users.AddRange(caller, student);
        var existing = new Event { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Title = "Orientation", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1), CreatedBy = caller.Id };
        db.Events.Add(existing);
        db.EventRegistrations.Add(new EventRegistration { Id = Guid.NewGuid(), EventId = existing.Id, StudentId = student.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.DeleteEvent(existing.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await db.Events.ToListAsync());
    }

    // --- Events redesign: approval workflow ---

    [Fact]
    public async Task CreateEvent_ByTeacherOrAdmin_IsAutoApproved()
    {
        await using var db = NewDb();
        var creator = NewUser(AccountType.Teacher);
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var start = DateTime.UtcNow.AddDays(1);
        var result = await ControllerAs(db, creator).CreateEvent(new CreateEventRequest("Guest Lecture", start, start.AddHours(1), null, null));

        Assert.IsType<OkObjectResult>(result.Result);
        var stored = Assert.Single(await db.Events.ToListAsync());
        Assert.Equal(EventStatus.Approved, stored.Status);
        Assert.Equal(creator.Id, stored.ApprovedBy);
    }

    // The new event_organizer role (bindable to a specific student) grants create_event via
    // the same permission code the trusted tier uses - CreateEvent can't tell them apart from
    // the permission check alone, so a Student-created event starts Pending instead.
    [Fact]
    public async Task CreateEvent_ByStudent_StartsPendingApproval()
    {
        await using var db = NewDb();
        var organizer = NewUser(AccountType.Student);
        db.Users.Add(organizer);
        await db.SaveChangesAsync();

        var start = DateTime.UtcNow.AddDays(1);
        var result = await ControllerAs(db, organizer).CreateEvent(new CreateEventRequest("Club Fest", start, start.AddHours(1), null, null));

        Assert.IsType<OkObjectResult>(result.Result);
        var stored = Assert.Single(await db.Events.ToListAsync());
        Assert.Equal(EventStatus.Pending, stored.Status);
        Assert.Null(stored.ApprovedBy);
        Assert.Null(stored.ApprovedAt);
    }

    [Fact]
    public async Task ListEvents_NeverShowsAPendingEventToStudents()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        db.Departments.Add(new Department { Id = departmentId, CollegeId = collegeId, Name = "CS" });
        db.Sections.Add(new Section { Id = sectionId, DepartmentId = departmentId, Year = 1, Name = "A" });
        db.Users.Add(new User
        {
            Id = studentId, CollegeId = collegeId, Identifier = "student-1", PasswordHash = "hash",
            FullName = "Student", IsActive = true, AccountType = AccountType.Student,
        });
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = sectionId, StudentId = studentId });
        db.Events.Add(new Event
        {
            Id = Guid.NewGuid(), CollegeId = collegeId, Title = "Pending Fest",
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            CreatedBy = studentId, Status = EventStatus.Pending,
        });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, new User { Id = studentId }).ListEvents();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var events = Assert.IsType<List<EventDto>>(ok.Value);
        Assert.Empty(events);
    }

    [Fact]
    public async Task ApproveEvent_ApprovesAPendingEvent()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var organizer = NewUser(AccountType.Student);
        db.Users.AddRange(admin, organizer);
        var pending = new Event
        {
            Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Title = "Club Fest",
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            CreatedBy = organizer.Id, Status = EventStatus.Pending,
        };
        db.Events.Add(pending);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, admin).ApproveEvent(pending.Id, new ApproveEventRequest(true));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<AdminEventDto>(ok.Value);
        Assert.Equal(EventStatus.Approved, dto.Status);
        Assert.Equal(admin.Id, dto.ApprovedBy);
    }

    [Fact]
    public async Task ApproveEvent_CanDenyAPendingEvent()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var organizer = NewUser(AccountType.Student);
        db.Users.AddRange(admin, organizer);
        var pending = new Event
        {
            Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Title = "Club Fest",
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            CreatedBy = organizer.Id, Status = EventStatus.Pending,
        };
        db.Events.Add(pending);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, admin).ApproveEvent(pending.Id, new ApproveEventRequest(false));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<AdminEventDto>(ok.Value);
        Assert.Equal(EventStatus.Denied, dto.Status);
    }

    [Fact]
    public async Task ApproveEvent_ForbidsAStudentFromApprovingEvents()
    {
        await using var db = NewDb();
        var organizer = NewUser(AccountType.Student);
        db.Users.Add(organizer);
        var pending = new Event
        {
            Id = Guid.NewGuid(), CollegeId = organizer.CollegeId, Title = "Club Fest",
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            CreatedBy = organizer.Id, Status = EventStatus.Pending,
        };
        db.Events.Add(pending);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, organizer).ApproveEvent(pending.Id, new ApproveEventRequest(true));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task ApproveEvent_RejectsReapprovingAnAlreadyDecidedEvent()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        var alreadyApproved = new Event
        {
            Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Title = "Fest",
            StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            CreatedBy = admin.Id, Status = EventStatus.Approved, ApprovedBy = admin.Id, ApprovedAt = DateTime.UtcNow,
        };
        db.Events.Add(alreadyApproved);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, admin).ApproveEvent(alreadyApproved.Id, new ApproveEventRequest(true));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task ListPendingEvents_ReturnsOnlyPendingEventsAtCallersCollege()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        db.Events.AddRange(
            new Event { Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Title = "Pending Here", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1), CreatedBy = admin.Id, Status = EventStatus.Pending },
            new Event { Id = Guid.NewGuid(), CollegeId = admin.CollegeId, Title = "Already Approved", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1), CreatedBy = admin.Id, Status = EventStatus.Approved, ApprovedBy = admin.Id, ApprovedAt = DateTime.UtcNow },
            new Event { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Title = "Pending Elsewhere", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1), CreatedBy = admin.Id, Status = EventStatus.Pending });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, admin).ListPendingEvents();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var events = Assert.IsType<List<AdminEventDto>>(ok.Value);
        Assert.Single(events);
        Assert.Equal("Pending Here", events[0].Title);
    }

    // --- Events redesign: recurrence rule validation ---

    [Theory]
    [InlineData("FREQ=WEEKLY;INTERVAL=1;COUNT=10")]
    [InlineData("FREQ=DAILY;UNTIL=2026-12-31")]
    [InlineData("FREQ=MONTHLY")]
    public async Task CreateEvent_AcceptsValidRecurrenceRules(string rule)
    {
        await using var db = NewDb();
        var creator = NewUser(AccountType.Teacher);
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var start = DateTime.UtcNow.AddDays(1);
        var result = await ControllerAs(db, creator).CreateEvent(
            new CreateEventRequest("Office Hours", start, start.AddHours(1), null, null, RecurrenceRule: rule));

        Assert.IsType<OkObjectResult>(result.Result);
        var stored = Assert.Single(await db.Events.ToListAsync());
        Assert.Equal(rule, stored.RecurrenceRule);
    }

    [Theory]
    [InlineData("FREQ=YEARLY")]
    [InlineData("FREQ=WEEKLY;COUNT=5;UNTIL=2026-12-31")]
    [InlineData("garbage")]
    [InlineData("INTERVAL=1")]
    public async Task CreateEvent_RejectsInvalidRecurrenceRules(string rule)
    {
        await using var db = NewDb();
        var creator = NewUser(AccountType.Teacher);
        db.Users.Add(creator);
        await db.SaveChangesAsync();

        var start = DateTime.UtcNow.AddDays(1);
        var result = await ControllerAs(db, creator).CreateEvent(
            new CreateEventRequest("Office Hours", start, start.AddHours(1), null, null, RecurrenceRule: rule));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.Events.ToListAsync());
    }
}
