using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests;

public class TimetableControllerTests
{
    // No test in this file needs a real permission lookup — TWA-12's endpoint doesn't
    // consult IPermissionService at all (see the controller's comment on why), but the
    // controller still requires one in its constructor for the other actions.
    private class FakePermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, string permissionCode) => Task.FromResult(false);
        public Task<Guid?> GetDepartmentScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
    }

    // #159: Generate() gates on "create_timetable" — a variant of the fake above that
    // grants it, for tests that need to actually exercise Generate().
    private class AllowingPermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, string permissionCode) => Task.FromResult(permissionCode == "create_timetable");
        public Task<Guid?> GetDepartmentScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
    }

    // #159: records LogWarning calls so tests can assert Generate() surfaces skipped
    // "already covered" assignments instead of silently dropping them.
    private class RecordingLogger : ILogger<TimetableController>
    {
        public List<string> Warnings { get; } = [];

        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // SDA-12: no test in this file exercises ExitPing's notification-routing behavior
    // directly (that's NotificationRouterTests' job) — the controller just needs some
    // INotificationRouter to construct.
    private class FakeNotificationRouter : INotificationRouter
    {
        public Task<Notification> RouteAsync(Guid recipientId, NotificationType type, object payload, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Notification { Id = Guid.NewGuid(), RecipientId = recipientId, Type = type });
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static User NewUser(AccountType accountType) => new()
    {
        Id = Guid.NewGuid(),
        CollegeId = Guid.NewGuid(),
        Identifier = $"user-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        FullName = "Test Teacher",
        AccountType = accountType,
        IsActive = true,
    };

    private static Department NewDepartment() => new() { Id = Guid.NewGuid(), Name = "CS", CollegeId = Guid.NewGuid() };

    private static Section NewSection(Guid departmentId) => new() { Id = Guid.NewGuid(), DepartmentId = departmentId, Year = 1, Name = "CS-A" };

    private static TimetableController ControllerAs(AppDbContext db, User user, IPermissionService? permissions = null, ILogger<TimetableController>? logger = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new TimetableController(db, permissions ?? new FakePermissionService(), new FakeNotificationRouter(), new CollegeScopeService(db), logger ?? NullLogger<TimetableController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private record Fixture(TimetableSlot Slot, List<User> Students);

    // TWA-08: sets up one teacher-owned slot for a section with `studentCount` enrolled students.
    private static async Task<Fixture> SeedSectionAsync(AppDbContext db, User teacher, int studentCount)
    {
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = Guid.NewGuid(), Year = 1, Name = "CS-A" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = section.DepartmentId, Code = "CS101", Name = "Intro to CS" };
        var slot = new TimetableSlot
        {
            Id = Guid.NewGuid(),
            SectionId = section.Id,
            SubjectId = subject.Id,
            TeacherId = teacher.Id,
            DayOfWeek = 1,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
        };
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        db.TimetableSlots.Add(slot);
        db.Users.Add(teacher);

        var students = new List<User>();
        for (var i = 0; i < studentCount; i++)
        {
            var student = NewUser(AccountType.Student);
            students.Add(student);
            db.Users.Add(student);
            db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = section.Id, StudentId = student.Id });
        }

        await db.SaveChangesAsync();
        return new Fixture(slot, students);
    }

    // TWA-12
    [Fact]
    public async Task Twa12_SubmitSectionFeedback_RejectsRatingOutOfRange()
    {
        await using var db = NewDb();
        var department = NewDepartment();
        var teacher = NewUser(AccountType.Teacher);
        var section = NewSection(department.Id);
        db.Departments.Add(department);
        db.Users.Add(teacher);
        db.Sections.Add(section);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.SubmitSectionFeedback(section.Id, new SubmitSectionFeedbackRequest(6, "too high"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // TWA-12
    [Fact]
    public async Task Twa12_SubmitSectionFeedback_ForbidsTeacherWhoDidNotTeachTheSection()
    {
        await using var db = NewDb();
        var department = NewDepartment();
        var teacher = NewUser(AccountType.Teacher);
        var section = NewSection(department.Id);
        db.Departments.Add(department);
        db.Users.Add(teacher);
        db.Sections.Add(section);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.SubmitSectionFeedback(section.Id, new SubmitSectionFeedbackRequest(3, null));

        Assert.IsType<ForbidResult>(result.Result);
    }

    // TWA-12
    [Fact]
    public async Task Twa12_SubmitSectionFeedback_ReturnsNotFound_ForUnknownSection()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.SubmitSectionFeedback(Guid.NewGuid(), new SubmitSectionFeedbackRequest(3, null));

        Assert.IsType<ForbidResult>(result.Result);
    }

    // TWA-12: acceptance-critical — the row this writes must be exactly the shape
    // Generate() reads for AWA-02 (teacher_id, section_id, rating <= 2 excludes them).
    [Fact]
    public async Task Twa12_SubmitSectionFeedback_WritesRowInShapeGenerateReadsForAwa02()
    {
        await using var db = NewDb();
        var department = NewDepartment();
        var teacher = NewUser(AccountType.Teacher);
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro", TeacherId = teacher.Id };
        var section = NewSection(department.Id);
        db.Departments.Add(department);
        db.Users.Add(teacher);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = section.Id, SubjectId = subject.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.SubmitSectionFeedback(section.Id, new SubmitSectionFeedbackRequest(1, "struggled with this group"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SectionFeedbackDto>(ok.Value);
        Assert.Equal(section.Id, dto.SectionId);
        Assert.Equal(1, dto.Rating);

        var stored = Assert.Single(await db.SectionFeedbacks.ToListAsync());
        Assert.Equal(teacher.Id, stored.TeacherId);
        Assert.Equal(section.Id, stored.SectionId);
        Assert.Equal(1, stored.Rating);

        // Mirror Generate()'s exact query shape for the negative-feedback exclusion set.
        var negativeFeedbackPairs = await db.SectionFeedbacks
            .Where(f => f.SectionId == section.Id && f.Rating <= 2)
            .Select(f => new { f.TeacherId, f.SectionId })
            .ToListAsync();
        Assert.Contains(negativeFeedbackPairs, p => p.TeacherId == teacher.Id && p.SectionId == section.Id);
    }

    // TWA-08
    [Fact]
    public async Task Twa08_MarkAttendance_ForbidsCallersWhoAreNotTeachers()
    {
        await using var db = NewDb();
        var notTeacher = NewUser(AccountType.AdminTier);
        db.Users.Add(notTeacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, notTeacher);
        var result = await controller.MarkAttendance(new MarkAttendanceRequest(Guid.NewGuid(), null, []));

        Assert.IsType<ForbidResult>(result.Result);
    }

    // TWA-08 — scoped to the teacher's own assigned section only.
    [Fact]
    public async Task Twa08_MarkAttendance_ForbidsTeacherNotAssignedToSlot()
    {
        await using var db = NewDb();
        var owningTeacher = NewUser(AccountType.Teacher);
        var otherTeacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, owningTeacher, studentCount: 2);
        db.Users.Add(otherTeacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherTeacher);
        var request = new MarkAttendanceRequest(fixture.Slot.Id, new DateOnly(2026, 7, 6),
            fixture.Students.Select(s => new AttendanceEntryRequest(s.Id, "Present")).ToList());
        var result = await controller.MarkAttendance(request);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Twa08_MarkAttendance_NotFoundForUnknownSlot()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.MarkAttendance(new MarkAttendanceRequest(Guid.NewGuid(), null, []));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // TWA-08 AC: every enrolled student must have a status set after marking completes —
    // an incomplete submission must be rejected outright, not partially saved.
    [Fact]
    public async Task Twa08_MarkAttendance_RejectsIncompleteRoster()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 3);

        var controller = ControllerAs(db, teacher);
        var request = new MarkAttendanceRequest(fixture.Slot.Id, new DateOnly(2026, 7, 6),
            fixture.Students.Take(2).Select(s => new AttendanceEntryRequest(s.Id, "Present")).ToList());
        var result = await controller.MarkAttendance(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, await db.AttendanceRecords.CountAsync());
        Assert.Contains("incomplete_attendance", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task Twa08_MarkAttendance_RejectsUnknownStudent()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 1);
        var outsider = NewUser(AccountType.Student);
        db.Users.Add(outsider);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var entries = fixture.Students.Select(s => new AttendanceEntryRequest(s.Id, "Present"))
            .Append(new AttendanceEntryRequest(outsider.Id, "Present"))
            .ToList();
        var result = await controller.MarkAttendance(new MarkAttendanceRequest(fixture.Slot.Id, new DateOnly(2026, 7, 6), entries));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("unknown_student", badRequest.Value!.ToString());
    }

    [Fact]
    public async Task Twa08_MarkAttendance_RejectsInvalidStatusValue()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 1);

        var controller = ControllerAs(db, teacher);
        var request = new MarkAttendanceRequest(fixture.Slot.Id, new DateOnly(2026, 7, 6),
            [new AttendanceEntryRequest(fixture.Students[0].Id, "on_fire")]);
        var result = await controller.MarkAttendance(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("invalid_status", badRequest.Value!.ToString());
    }

    // TWA-08 AC: every enrolled student must have a status set after marking completes.
    [Fact]
    public async Task Twa08_MarkAttendance_CompleteRosterCreatesRecordForEveryEnrolledStudent()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 3);

        var controller = ControllerAs(db, teacher);
        var entries = new List<AttendanceEntryRequest>
        {
            new(fixture.Students[0].Id, "Present"),
            new(fixture.Students[1].Id, "Absent"),
            new(fixture.Students[2].Id, "Late"),
        };
        var result = await controller.MarkAttendance(new MarkAttendanceRequest(fixture.Slot.Id, new DateOnly(2026, 7, 6), entries));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<MarkAttendanceResponse>(ok.Value);
        Assert.Equal(3, response.Records.Count);
        Assert.Equal(fixture.Slot.SectionId, response.SectionId);

        var session = await db.ClassSessions.SingleAsync(s => s.TimetableSlotId == fixture.Slot.Id);
        Assert.Equal(new DateOnly(2026, 7, 6), session.SessionDate);

        var records = await db.AttendanceRecords.Where(r => r.ClassSessionId == session.Id).ToListAsync();
        Assert.Equal(3, records.Count);
        foreach (var student in fixture.Students)
        {
            Assert.Contains(records, r => r.StudentId == student.Id && r.MarkedBy == teacher.Id);
        }
    }

    // #152 — when SessionDate is omitted, the session date must be derived from the
    // teacher's college's local time zone (via CollegeClock), not raw UTC, so marking
    // attendance near local midnight doesn't roll over to the wrong calendar day.
    [Fact]
    public async Task Twa08_MarkAttendance_DefaultsSessionDateFromCollegeTimeZone_NotRawUtc()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var college = new College { Id = teacher.CollegeId, Name = "Kiritimati Campus", TimeZone = "Pacific/Kiritimati" };
        db.Colleges.Add(college);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 1);

        var controller = ControllerAs(db, teacher);
        var entries = new List<AttendanceEntryRequest> { new(fixture.Students[0].Id, "Present") };
        var result = await controller.MarkAttendance(new MarkAttendanceRequest(fixture.Slot.Id, null, entries));

        Assert.IsType<OkObjectResult>(result.Result);
        var session = await db.ClassSessions.SingleAsync(s => s.TimetableSlotId == fixture.Slot.Id);
        var expectedDate = CollegeClock.LocalDate(college, DateTime.UtcNow);
        Assert.Equal(expectedDate, session.SessionDate);
    }

    // TWA-08
    [Fact]
    public async Task Twa08_Roster_ReturnsEnrolledStudentsForOwnSlot()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 2);

        var controller = ControllerAs(db, teacher);
        var result = await controller.Roster(fixture.Slot.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var roster = Assert.IsType<List<RosterStudentDto>>(ok.Value);
        Assert.Equal(2, roster.Count);
        Assert.All(fixture.Students, s => Assert.Contains(roster, r => r.StudentId == s.Id));
    }

    // TWA-08
    [Fact]
    public async Task Twa08_Roster_ForbidsTeacherNotAssignedToSlot()
    {
        await using var db = NewDb();
        var owningTeacher = NewUser(AccountType.Teacher);
        var otherTeacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, owningTeacher, studentCount: 1);
        db.Users.Add(otherTeacher);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherTeacher);
        var result = await controller.Roster(fixture.Slot.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // TWA-08 — re-marking the same session (e.g. correcting a mistake) updates in place
    // rather than duplicating rows, respecting the DB's unique (session, student) constraint.
    [Fact]
    public async Task Twa08_MarkAttendance_ReMarkingSameSessionUpdatesExistingRecordsInPlace()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 2);
        var date = new DateOnly(2026, 7, 6);

        var controller = ControllerAs(db, teacher);
        await controller.MarkAttendance(new MarkAttendanceRequest(fixture.Slot.Id, date,
        [
            new AttendanceEntryRequest(fixture.Students[0].Id, "Present"),
            new AttendanceEntryRequest(fixture.Students[1].Id, "Present"),
        ]));

        await controller.MarkAttendance(new MarkAttendanceRequest(fixture.Slot.Id, date,
        [
            new AttendanceEntryRequest(fixture.Students[0].Id, "Absent"),
            new AttendanceEntryRequest(fixture.Students[1].Id, "Present"),
        ]));

        Assert.Equal(2, await db.AttendanceRecords.CountAsync());
        var updated = await db.AttendanceRecords.SingleAsync(r => r.StudentId == fixture.Students[0].Id);
        Assert.Equal(AttendanceStatus.Absent, updated.Status);
    }

    // TWA-09 helper: marks one more session for the slot's section, with a fixed status
    // for every already-enrolled student in `fixture`, on a distinct sequential date so
    // ordering by SessionDate is deterministic.
    private static async Task MarkSessionAsync(TimetableController controller, Fixture fixture, DateOnly date, params (User student, string status)[] entries)
    {
        var byId = entries.ToDictionary(e => e.student.Id, e => e.status);
        var all = fixture.Students.Select(s => new AttendanceEntryRequest(s.Id, byId.GetValueOrDefault(s.Id, "Present"))).ToList();
        var result = await controller.MarkAttendance(new MarkAttendanceRequest(fixture.Slot.Id, date, all));
        Assert.IsType<OkObjectResult>(result.Result);
    }

    // TWA-09 AC: alert fires the first time a student's cumulative attendance crosses
    // below 65%, referencing the student and their current percentage.
    [Fact]
    public async Task Twa09_AttendanceAlerts_FiresWhenLatestSessionCrossesBelow65Percent()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 1);
        var student = fixture.Students[0];
        var controller = ControllerAs(db, teacher);

        // 1 present, 1 present: 100% — nowhere near the threshold yet.
        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 1), (student, "Present"));
        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 2), (student, "Present"));
        // Two absences bring cumulative to 2/4 = 50%, crossing below 65% on this call.
        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 3), (student, "Absent"));
        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 4), (student, "Absent"));

        var result = await controller.AttendanceAlerts();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var alerts = Assert.IsType<List<AttendanceAlertDto>>(ok.Value);

        var alert = Assert.Single(alerts);
        Assert.Equal(student.Id, alert.StudentId);
        Assert.Equal(fixture.Slot.SectionId, alert.SectionId);
        Assert.Equal(50m, alert.AttendancePercentage);
    }

    // TWA-09 AC: must not fire on every subsequent poll while still below 65% — only at
    // the exact session that caused the crossing.
    [Fact]
    public async Task Twa09_AttendanceAlerts_DoesNotFireAgainOnceAlreadyBelowThreshold()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 1);
        var student = fixture.Students[0];
        var controller = ControllerAs(db, teacher);

        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 1), (student, "Present"));
        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 2), (student, "Absent"));
        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 3), (student, "Absent"));
        // Crossed below 65% as of 7/3 (1/3 = 33%). One more absence keeps it below —
        // this should NOT still be reported as a fresh alert.
        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 4), (student, "Absent"));

        var result = await controller.AttendanceAlerts();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var alerts = Assert.IsType<List<AttendanceAlertDto>>(ok.Value);

        Assert.Empty(alerts);
    }

    [Fact]
    public async Task Twa09_AttendanceAlerts_DoesNotFireWhileAboveThreshold()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 1);
        var student = fixture.Students[0];
        var controller = ControllerAs(db, teacher);

        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 1), (student, "Present"));
        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 2), (student, "Present"));
        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 3), (student, "Absent"));

        var result = await controller.AttendanceAlerts();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var alerts = Assert.IsType<List<AttendanceAlertDto>>(ok.Value);

        Assert.Empty(alerts);
    }

    [Fact]
    public async Task Twa09_AttendanceAlerts_ScopedToCallingTeachersOwnSections()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var otherTeacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 1);
        db.Users.Add(otherTeacher);
        await db.SaveChangesAsync();
        var controller = ControllerAs(db, teacher);
        await MarkSessionAsync(controller, fixture, new DateOnly(2026, 7, 1), (fixture.Students[0], "Absent"));

        var otherController = ControllerAs(db, otherTeacher);
        var result = await otherController.AttendanceAlerts();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(Assert.IsType<List<AttendanceAlertDto>>(ok.Value));
    }

    // TWA-04
    [Fact]
    public async Task Twa04_PerformanceSummary_ForbidsTeacherNotAssignedToSection()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 1);

        var controller = ControllerAs(db, teacher);
        var result = await controller.GetSectionPerformanceSummary(fixture.Slot.SectionId);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Twa04_PerformanceSummary_ComputesOverallAndPerStudentAttendance()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 2);
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = fixture.Slot.SectionId, SubjectId = fixture.Slot.SubjectId });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        await controller.MarkAttendance(new MarkAttendanceRequest(fixture.Slot.Id, new DateOnly(2026, 7, 6),
        [
            new AttendanceEntryRequest(fixture.Students[0].Id, "Present"),
            new AttendanceEntryRequest(fixture.Students[1].Id, "Absent"),
        ]));

        var result = await controller.GetSectionPerformanceSummary(fixture.Slot.SectionId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<SectionPerformanceSummaryDto>(ok.Value);
        Assert.Equal(50m, summary.OverallAttendancePercentage);
        Assert.Equal(2, summary.StudentAttendance.Count);
        Assert.Contains(summary.StudentAttendance, s => s.StudentId == fixture.Students[0].Id && s.AttendancePercentage == 100m);
        Assert.Contains(summary.StudentAttendance, s => s.StudentId == fixture.Students[1].Id && s.AttendancePercentage == 0m);
    }

    [Fact]
    public async Task Twa04_PerformanceSummary_ComputesAverageMarksPerTaughtSubject()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 2);
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = fixture.Slot.SectionId, SubjectId = fixture.Slot.SubjectId });
        db.InternalMarks.Add(new InternalMark { Id = Guid.NewGuid(), StudentId = fixture.Students[0].Id, SubjectId = fixture.Slot.SubjectId, Marks = 80 });
        db.InternalMarks.Add(new InternalMark { Id = Guid.NewGuid(), StudentId = fixture.Students[1].Id, SubjectId = fixture.Slot.SubjectId, Marks = 60 });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.GetSectionPerformanceSummary(fixture.Slot.SectionId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<SectionPerformanceSummaryDto>(ok.Value);
        var subjectSummary = Assert.Single(summary.MarksBySubject);
        Assert.Equal(fixture.Slot.SubjectId, subjectSummary.SubjectId);
        Assert.Equal(70m, subjectSummary.AverageMarks);
        Assert.Equal(2, subjectSummary.StudentsGraded);
    }

    [Fact]
    public async Task Twa04_PerformanceSummary_NullAttendanceAndMarks_WhenNoDataYet()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var fixture = await SeedSectionAsync(db, teacher, studentCount: 1);
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = fixture.Slot.SectionId, SubjectId = fixture.Slot.SubjectId });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.GetSectionPerformanceSummary(fixture.Slot.SectionId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<SectionPerformanceSummaryDto>(ok.Value);
        Assert.Null(summary.OverallAttendancePercentage);
        Assert.Null(summary.MarksBySubject.Single().AverageMarks);
    }

    // #159: a stale/duplicate TeacherSectionAssignment row for the same (Section, Subject)
    // pair already covered by a manually-edited slot used to be dropped with no error
    // surfaced anywhere. Generate() must now log which assignment(s) were skipped as
    // "already covered", without changing the (Section, Subject) dedup key itself.
    [Fact]
    public async Task Issue159_Generate_LogsAssignmentsSkippedAsAlreadyCovered()
    {
        await using var db = NewDb();
        var coveringTeacher = NewUser(AccountType.Teacher);
        var duplicateTeacher = NewUser(AccountType.Teacher);
        // #129: Generate() (global, non-HoD caller) now rejects a requested DepartmentId
        // belonging to a different college than the caller's own - match them here since this
        // test is about the #159 skip-logging behavior, not #129's cross-college guard.
        var department = new Department { Id = Guid.NewGuid(), CollegeId = coveringTeacher.CollegeId, Name = "CS" };
        var section = NewSection(department.Id);
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS201", Name = "Data Structures" };
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        db.Users.AddRange(coveringTeacher, duplicateTeacher);

        // A manually-edited slot already covers (section, subject) for coveringTeacher.
        db.TimetableSlots.Add(new TimetableSlot
        {
            Id = Guid.NewGuid(),
            SectionId = section.Id,
            SubjectId = subject.Id,
            TeacherId = coveringTeacher.Id,
            DayOfWeek = 1,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
            ManuallyEdited = true,
        });

        // A stale/duplicate assignment row for the same (section, subject) but a different
        // teacher — this is the one that gets silently skipped today.
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = duplicateTeacher.Id, SectionId = section.Id, SubjectId = subject.Id });
        await db.SaveChangesAsync();

        var recordingLogger = new RecordingLogger();
        var controller = ControllerAs(db, coveringTeacher, permissions: new AllowingPermissionService(), logger: recordingLogger);

        var result = await controller.Generate(new GenerateTimetableRequest(department.Id));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Contains(recordingLogger.Warnings, w => w.Contains(duplicateTeacher.Id.ToString()) && w.Contains("already covered"));
        // No new auto-generated slot was created for the duplicate assignment — only the
        // pre-existing manually-edited slot remains.
        var slots = await db.TimetableSlots.Where(s => s.SectionId == section.Id).ToListAsync();
        Assert.Single(slots);
    }

    // A global (non-HoD) create_timetable holder — GetDepartmentScopeAsync returns null,
    // same as an Admin's binding with no "hod" role.
    private class FakeGlobalTimetablePermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, string permissionCode) => Task.FromResult(permissionCode == "create_timetable");
        public Task<Guid?> GetDepartmentScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
    }

    private static TimetableController GlobalCallerController(AppDbContext db, User caller)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, caller.Id.ToString())], "TestAuth"));
        return new TimetableController(db, new FakeGlobalTimetablePermissionService(), new FakeNotificationRouter(), new CollegeScopeService(db), NullLogger<TimetableController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
    }

    // #129 — PatchSlot's HoD-scope guard previously only ran when departmentScope was
    // non-null, so a global create_timetable holder could patch any slot in any college by
    // guessing/enumerating slot ids.
    [Fact]
    public async Task PatchSlot_ForbidsGlobalCallerFromCrossCollegeSlot()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var otherCollegeDepartment = NewDepartment(); // random CollegeId, different from caller's
        var section = NewSection(otherCollegeDepartment.Id);
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = otherCollegeDepartment.Id, Code = "CS101", Name = "Intro" };
        var slotTeacher = NewUser(AccountType.Teacher);
        var slot = new TimetableSlot { Id = Guid.NewGuid(), SectionId = section.Id, SubjectId = subject.Id, TeacherId = slotTeacher.Id, DayOfWeek = 1, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0) };
        db.Users.AddRange(caller, slotTeacher);
        db.Departments.Add(otherCollegeDepartment);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        db.TimetableSlots.Add(slot);
        await db.SaveChangesAsync();

        var controller = GlobalCallerController(db, caller);
        var result = await controller.PatchSlot(slot.Id, new PatchSlotRequest(null, null, null, null, null));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task PatchSlot_AllowsGlobalCallerToPatchSameCollegeSlot()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), Name = "CS", CollegeId = caller.CollegeId };
        var section = NewSection(department.Id);
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        var slotTeacher = NewUser(AccountType.Teacher);
        var slot = new TimetableSlot { Id = Guid.NewGuid(), SectionId = section.Id, SubjectId = subject.Id, TeacherId = slotTeacher.Id, DayOfWeek = 1, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0) };
        db.Users.AddRange(caller, slotTeacher);
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        db.TimetableSlots.Add(slot);
        await db.SaveChangesAsync();

        var controller = GlobalCallerController(db, caller);
        var result = await controller.PatchSlot(slot.Id, new PatchSlotRequest(null, 3, null, null, "Room 5"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<TimetableSlotDto>(ok.Value);
        Assert.Equal(3, dto.DayOfWeek);
    }

    // TWA-13 + Notification Router (shared, #80) — requesting a timetable change routes a
    // TimetableRequest notification to every Admin in the requesting teacher's own college.
    private class RecordingNotificationRouter : INotificationRouter
    {
        public List<(Guid RecipientId, NotificationType Type)> Routed { get; } = new();

        public Task<Notification> RouteAsync(Guid recipientId, NotificationType type, object payload, CancellationToken cancellationToken = default)
        {
            Routed.Add((recipientId, type));
            return Task.FromResult(new Notification { Id = Guid.NewGuid(), RecipientId = recipientId, Type = type });
        }
    }

    private static TimetableController ControllerAs(AppDbContext db, User user, INotificationRouter notifications)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new TimetableController(db, new FakePermissionService(), notifications, new CollegeScopeService(db), NullLogger<TimetableController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    [Fact]
    public async Task Twa13_CreateChangeRequest_NotifiesEveryAdminInTheTeachersCollege()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var teacher = NewUser(AccountType.Teacher);
        teacher.CollegeId = collegeId;
        var admin = NewUser(AccountType.AdminTier);
        admin.CollegeId = collegeId;
        var otherCollegeAdmin = NewUser(AccountType.AdminTier);
        db.Users.AddRange(teacher, admin, otherCollegeAdmin);
        db.RoleBindings.AddRange(
            new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", GrantedAt = DateTime.UtcNow },
            new RoleBinding { Id = Guid.NewGuid(), UserId = otherCollegeAdmin.Id, RoleCode = "admin", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var router = new RecordingNotificationRouter();
        var controller = ControllerAs(db, teacher, router);

        var result = await controller.CreateChangeRequest(new CreateChangeRequestRequest("Move CS101 to a bigger room"));

        Assert.IsType<OkObjectResult>(result.Result);
        var routed = Assert.Single(router.Routed);
        Assert.Equal(admin.Id, routed.RecipientId);
        Assert.Equal(NotificationType.TimetableRequest, routed.Type);
    }

    // MySections() must be sourced from TeacherSectionAssignments, not TimetableSlots — this
    // is what fixes Dashboard's false-403 (a section can appear on the teacher's timetable via
    // a manually-patched slot without a matching TeacherSectionAssignment ever existing).
    [Fact]
    public async Task MySections_ReturnsOnlySectionsFromTeacherSectionAssignments_NotFromTimetableSlots()
    {
        await using var db = NewDb();
        var department = NewDepartment();
        var teacher = NewUser(AccountType.Teacher);
        var assignedSection = NewSection(department.Id);
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        db.Departments.Add(department);
        db.Users.Add(teacher);
        db.Sections.Add(assignedSection);
        db.Subjects.Add(subject);
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = assignedSection.Id, SubjectId = subject.Id });

        // A slot for a *different* section exists on the teacher's timetable (e.g. via a
        // manual PatchSlot reassignment) with no corresponding TeacherSectionAssignment —
        // MySections must NOT include it.
        var unassignedSection = NewSection(department.Id);
        db.Sections.Add(unassignedSection);
        db.TimetableSlots.Add(new TimetableSlot
        {
            Id = Guid.NewGuid(),
            SectionId = unassignedSection.Id,
            SubjectId = subject.Id,
            TeacherId = teacher.Id,
            DayOfWeek = 1,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.MySections();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var sections = Assert.IsType<List<AssignedSectionDto>>(ok.Value);
        var section = Assert.Single(sections);
        Assert.Equal(assignedSection.Id, section.SectionId);
    }

    [Fact]
    public async Task SectionRoster_ReturnsEnrolledStudentsWithIdentifier_WhenTeacherAssignedToSection()
    {
        await using var db = NewDb();
        var department = NewDepartment();
        var teacher = NewUser(AccountType.Teacher);
        var section = NewSection(department.Id);
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        var student = NewUser(AccountType.Student);
        db.Departments.Add(department);
        db.Users.AddRange(teacher, student);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = section.Id, SubjectId = subject.Id });
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = section.Id, StudentId = student.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.SectionRoster(section.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var roster = Assert.IsType<List<SectionRosterStudentDto>>(ok.Value);
        var entry = Assert.Single(roster);
        Assert.Equal(student.Id, entry.StudentId);
        Assert.Equal(student.Identifier, entry.Identifier);
    }

    [Fact]
    public async Task SectionRoster_Forbidden_WhenTeacherNotAssignedToSection()
    {
        await using var db = NewDb();
        var department = NewDepartment();
        var teacher = NewUser(AccountType.Teacher);
        var section = NewSection(department.Id);
        db.Departments.Add(department);
        db.Users.Add(teacher);
        db.Sections.Add(section);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.SectionRoster(section.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }
}
