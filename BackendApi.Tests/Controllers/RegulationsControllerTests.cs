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

public class RegulationsControllerTests
{
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
        FullName = "Test User",
        AccountType = accountType,
        IsActive = true,
    };

    private static RegulationsController ControllerAs(AppDbContext db, User user)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new RegulationsController(db, new PermissionService(db), new CollegeScopeService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private static async Task GrantManageDepartmentsAsync(AppDbContext db, User admin)
    {
        var manageDepartmentsPermission = new Permission { Code = "manage_departments", Description = "x" };
        var role = new Role { Code = "admin_with_departments" };
        role.PermissionCodes.Add(manageDepartmentsPermission);
        db.Permissions.Add(manageDepartmentsPermission);
        db.Roles.Add(role);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin_with_departments", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Create_ForbidsCallerWithoutManageDepartmentsPermission()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(new CreateRegulationRequest(department.Id, "R20", "Regulation 2020", 2020));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Create_ForbidsCrossCollegeDepartment()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var otherCollegeDepartment = new Department { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "EE" };
        db.Users.Add(caller);
        db.Departments.Add(otherCollegeDepartment);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(new CreateRegulationRequest(otherCollegeDepartment.Id, "R20", "Regulation 2020", 2020));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Create_RejectsDuplicateCodeWithinSameDepartment()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Regulations.Add(new Regulation { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "R20", Name = "Regulation 2020", EffectiveFromYear = 2020, IsActive = true });
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(new CreateRegulationRequest(department.Id, "R20", "Duplicate", 2021));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_CreatesRegulationForSameCollegeCaller()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(new CreateRegulationRequest(department.Id, "R20", "Regulation 2020", 2020));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<RegulationDto>(created.Value);
        Assert.Equal("R20", dto.Code);
        Assert.True(dto.IsActive);
    }

    [Fact]
    public async Task List_ReturnsOnlyCallersCollegeRegulations()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var ownDepartment = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var otherDepartment = new Department { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "EE" };
        db.Users.Add(caller);
        db.Departments.AddRange(ownDepartment, otherDepartment);
        db.Regulations.Add(new Regulation { Id = Guid.NewGuid(), DepartmentId = ownDepartment.Id, Code = "R20", Name = "Own", EffectiveFromYear = 2020, IsActive = true });
        db.Regulations.Add(new Regulation { Id = Guid.NewGuid(), DepartmentId = otherDepartment.Id, Code = "R19", Name = "Other", EffectiveFromYear = 2019, IsActive = true });
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.List(null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var regulations = Assert.IsType<List<RegulationDto>>(ok.Value);
        Assert.Single(regulations);
        Assert.Equal("R20", regulations[0].Code);
    }

    [Fact]
    public async Task CreateOffering_RejectsSubjectFromADifferentDepartment()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var otherDepartment = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "EE" };
        var regulation = new Regulation { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "R20", Name = "R20", EffectiveFromYear = 2020, IsActive = true };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = otherDepartment.Id, Code = "EE101", Name = "Circuits" };
        db.Users.Add(caller);
        db.Departments.AddRange(department, otherDepartment);
        db.Regulations.Add(regulation);
        db.Subjects.Add(subject);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.CreateOffering(regulation.Id, new CreateOfferingRequest(subject.Id, 1, 3, 1, 0, 4.0m, false, false, 75.0m));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateOffering_RejectsInvalidCredits()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var regulation = new Regulation { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "R20", Name = "R20", EffectiveFromYear = 2020, IsActive = true };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Regulations.Add(regulation);
        db.Subjects.Add(subject);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.CreateOffering(regulation.Id, new CreateOfferingRequest(subject.Id, 1, 3, 1, 0, 0m, false, false, 75.0m));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateOffering_CreatesOfferingForSubjectInSameDepartment()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var regulation = new Regulation { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "R20", Name = "R20", EffectiveFromYear = 2020, IsActive = true };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Regulations.Add(regulation);
        db.Subjects.Add(subject);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.CreateOffering(regulation.Id, new CreateOfferingRequest(subject.Id, 3, 3, 1, 0, 4.0m, false, false, 75.0m));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<RegulationSubjectOfferingDto>(created.Value);
        Assert.Equal(3, dto.Semester);
        Assert.Equal(4.0m, dto.Credits);
        Assert.Equal("CS101", dto.SubjectCode);
    }

    [Fact]
    public async Task CreateOffering_RejectsDuplicateOfferingForSameSubject()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var regulation = new Regulation { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "R20", Name = "R20", EffectiveFromYear = 2020, IsActive = true };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Regulations.Add(regulation);
        db.Subjects.Add(subject);
        db.RegulationSubjectOfferings.Add(new RegulationSubjectOffering { Id = Guid.NewGuid(), RegulationId = regulation.Id, SubjectId = subject.Id, Semester = 1, Credits = 3.0m, MinAttendancePercent = 75.0m });
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.CreateOffering(regulation.Id, new CreateOfferingRequest(subject.Id, 1, 3, 0, 0, 3.0m, false, false, 75.0m));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateUnit_RejectsDuplicateUnitNumberForSameOffering()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var regulation = new Regulation { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "R20", Name = "R20", EffectiveFromYear = 2020, IsActive = true };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        var offering = new RegulationSubjectOffering { Id = Guid.NewGuid(), RegulationId = regulation.Id, SubjectId = subject.Id, Semester = 1, Credits = 3.0m, MinAttendancePercent = 75.0m };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Regulations.Add(regulation);
        db.Subjects.Add(subject);
        db.RegulationSubjectOfferings.Add(offering);
        db.CurriculumUnits.Add(new CurriculumUnit { Id = Guid.NewGuid(), OfferingId = offering.Id, UnitNumber = 1, Title = "Unit 1" });
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.CreateUnit(offering.Id, new CreateCurriculumUnitRequest(1, "Duplicate Unit", null));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateUnit_CreatesUnitForOffering()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var regulation = new Regulation { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "R20", Name = "R20", EffectiveFromYear = 2020, IsActive = true };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        var offering = new RegulationSubjectOffering { Id = Guid.NewGuid(), RegulationId = regulation.Id, SubjectId = subject.Id, Semester = 1, Credits = 3.0m, MinAttendancePercent = 75.0m };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Regulations.Add(regulation);
        db.Subjects.Add(subject);
        db.RegulationSubjectOfferings.Add(offering);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.CreateUnit(offering.Id, new CreateCurriculumUnitRequest(1, "Introduction to Algorithms", "Big-O, sorting, searching"));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<CurriculumUnitDto>(created.Value);
        Assert.Equal("Introduction to Algorithms", dto.Title);
    }

    [Fact]
    public async Task CreateChapter_CreatesChapterForUnit()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var regulation = new Regulation { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "R20", Name = "R20", EffectiveFromYear = 2020, IsActive = true };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        var offering = new RegulationSubjectOffering { Id = Guid.NewGuid(), RegulationId = regulation.Id, SubjectId = subject.Id, Semester = 1, Credits = 3.0m, MinAttendancePercent = 75.0m };
        var unit = new CurriculumUnit { Id = Guid.NewGuid(), OfferingId = offering.Id, UnitNumber = 1, Title = "Unit 1" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Regulations.Add(regulation);
        db.Subjects.Add(subject);
        db.RegulationSubjectOfferings.Add(offering);
        db.CurriculumUnits.Add(unit);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.CreateChapter(unit.Id, new CreateCurriculumChapterRequest(1, "Big-O Notation", null));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<CurriculumChapterDto>(created.Value);
        Assert.Equal("Big-O Notation", dto.Title);
    }

    [Fact]
    public async Task ListChapters_ForbidsCrossCollegeCaller()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var otherCollegeDepartment = new Department { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "CS" };
        var regulation = new Regulation { Id = Guid.NewGuid(), DepartmentId = otherCollegeDepartment.Id, Code = "R20", Name = "R20", EffectiveFromYear = 2020, IsActive = true };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = otherCollegeDepartment.Id, Code = "CS101", Name = "Intro" };
        var offering = new RegulationSubjectOffering { Id = Guid.NewGuid(), RegulationId = regulation.Id, SubjectId = subject.Id, Semester = 1, Credits = 3.0m, MinAttendancePercent = 75.0m };
        var unit = new CurriculumUnit { Id = Guid.NewGuid(), OfferingId = offering.Id, UnitNumber = 1, Title = "Unit 1" };
        db.Users.Add(caller);
        db.Departments.Add(otherCollegeDepartment);
        db.Regulations.Add(regulation);
        db.Subjects.Add(subject);
        db.RegulationSubjectOfferings.Add(offering);
        db.CurriculumUnits.Add(unit);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.ListChapters(unit.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task DeleteUnit_RemovesUnit()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var regulation = new Regulation { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "R20", Name = "R20", EffectiveFromYear = 2020, IsActive = true };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        var offering = new RegulationSubjectOffering { Id = Guid.NewGuid(), RegulationId = regulation.Id, SubjectId = subject.Id, Semester = 1, Credits = 3.0m, MinAttendancePercent = 75.0m };
        var unit = new CurriculumUnit { Id = Guid.NewGuid(), OfferingId = offering.Id, UnitNumber = 1, Title = "Unit 1" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Regulations.Add(regulation);
        db.Subjects.Add(subject);
        db.RegulationSubjectOfferings.Add(offering);
        db.CurriculumUnits.Add(unit);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.DeleteUnit(unit.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.DoesNotContain(db.CurriculumUnits.Local, u => u.Id == unit.Id);
    }
}
