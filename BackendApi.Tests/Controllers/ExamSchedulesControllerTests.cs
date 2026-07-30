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

public class ExamSchedulesControllerTests
{
    private class AllowingPermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, string permissionCode) => Task.FromResult(permissionCode == "create_timetable");
        public Task<Guid?> GetDepartmentScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
    }

    private class DenyingPermissionService : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, string permissionCode) => Task.FromResult(false);
        public Task<Guid?> GetDepartmentScopeAsync(Guid userId) => Task.FromResult<Guid?>(null);
    }

    private class DepartmentScopedPermissionService(Guid departmentId) : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid userId, string permissionCode) => Task.FromResult(permissionCode == "create_timetable");
        public Task<Guid?> GetDepartmentScopeAsync(Guid userId) => Task.FromResult<Guid?>(departmentId);
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

    private static ExamSchedulesController ControllerAs(AppDbContext db, User user, IPermissionService? permissions = null) => new(
        db, permissions ?? new AllowingPermissionService(), new CollegeScopeService(db))
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
    public async Task ListSections_ForbidsCrossCollegeDepartment()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var otherCollegeDepartment = new Department { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "EE" };
        db.Users.Add(caller);
        db.Departments.Add(otherCollegeDepartment);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.ListSections(otherCollegeDepartment.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task ListSections_ReturnsSectionsForDepartment()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 2, Name = "B" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Sections.Add(section);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.ListSections(department.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var sections = Assert.IsType<List<SectionDto>>(ok.Value);
        var dto = Assert.Single(sections);
        Assert.Equal("B", dto.Name);
        Assert.Equal(2, dto.Year);
    }

    [Fact]
    public async Task Create_ForbidsCallerWithoutCreateTimetablePermission()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 1, Name = "A" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller, new DenyingPermissionService());
        var result = await controller.Create(section.Id, new CreateExamScheduleRequest(subject.Id, ExamType.Internal, new DateOnly(2026, 9, 1), new TimeOnly(9, 0), new TimeOnly(11, 0), "Room 1"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Create_RejectsSubjectFromADifferentDepartment()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var otherDepartment = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "EE" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 1, Name = "A" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = otherDepartment.Id, Code = "EE101", Name = "Circuits" };
        db.Users.Add(caller);
        db.Departments.AddRange(department, otherDepartment);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(section.Id, new CreateExamScheduleRequest(subject.Id, ExamType.Internal, new DateOnly(2026, 9, 1), new TimeOnly(9, 0), new TimeOnly(11, 0), null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_RejectsEndTimeAtOrBeforeStartTime()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 1, Name = "A" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(section.Id, new CreateExamScheduleRequest(subject.Id, ExamType.Internal, new DateOnly(2026, 9, 1), new TimeOnly(9, 0), new TimeOnly(9, 0), null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_CreatesScheduleForValidSubjectAndSection()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 1, Name = "A" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(section.Id, new CreateExamScheduleRequest(subject.Id, ExamType.External, new DateOnly(2026, 12, 10), new TimeOnly(10, 0), new TimeOnly(13, 0), "Hall A"));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<ExamScheduleDto>(created.Value);
        Assert.Equal(ExamType.External, dto.ExamType);
        Assert.Equal("CS101", dto.SubjectCode);
        Assert.Equal("Hall A", dto.Room);
    }

    [Fact]
    public async Task Create_RejectsDuplicateScheduleForSameSectionSubjectAndExamType()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 1, Name = "A" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        db.ExamSchedules.Add(new ExamSchedule
        {
            Id = Guid.NewGuid(),
            SectionId = section.Id,
            SubjectId = subject.Id,
            ExamType = ExamType.Internal,
            ExamDate = new DateOnly(2026, 9, 1),
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(11, 0),
            CreatedBy = caller.Id,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(section.Id, new CreateExamScheduleRequest(subject.Id, ExamType.Internal, new DateOnly(2026, 9, 5), new TimeOnly(9, 0), new TimeOnly(11, 0), null));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task List_ForbidsSectionOutsideCallersDepartmentScope()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 1, Name = "A" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Sections.Add(section);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller, new DepartmentScopedPermissionService(Guid.NewGuid()));
        var result = await controller.List(section.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Delete_RemovesSchedule()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 1, Name = "A" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        var schedule = new ExamSchedule
        {
            Id = Guid.NewGuid(),
            SectionId = section.Id,
            SubjectId = subject.Id,
            ExamType = ExamType.Internal,
            ExamDate = new DateOnly(2026, 9, 1),
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(11, 0),
            CreatedBy = caller.Id,
        };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        db.ExamSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.Delete(schedule.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.DoesNotContain(db.ExamSchedules.Local, e => e.Id == schedule.Id);
    }

    [Fact]
    public async Task Update_UpdatesScheduleFields()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 1, Name = "A" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        var schedule = new ExamSchedule
        {
            Id = Guid.NewGuid(),
            SectionId = section.Id,
            SubjectId = subject.Id,
            ExamType = ExamType.Internal,
            ExamDate = new DateOnly(2026, 9, 1),
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(11, 0),
            CreatedBy = caller.Id,
        };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        db.ExamSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.Update(schedule.Id, new UpdateExamScheduleRequest(new DateOnly(2026, 9, 10), new TimeOnly(14, 0), new TimeOnly(16, 0), "Room 2"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ExamScheduleDto>(ok.Value);
        Assert.Equal(new DateOnly(2026, 9, 10), dto.ExamDate);
        Assert.Equal("Room 2", dto.Room);
    }
}
