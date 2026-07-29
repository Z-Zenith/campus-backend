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

public class SubjectsControllerTests
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

    private static SubjectsController ControllerAs(AppDbContext db, User user)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new SubjectsController(db, new PermissionService(db), new CollegeScopeService(db))
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

    private static (Section Section, Subject Subject, User Teacher) SeedTaughtSection(AppDbContext db, User student)
    {
        var teacher = NewUser(AccountType.Teacher);
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = Guid.NewGuid(), Year = 3, Name = "3rd Year CSE - A" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = Guid.NewGuid(), Code = "CS301", Name = "Operating Systems" };
        db.Users.Add(teacher);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = section.Id, StudentId = student.Id });
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = section.Id, SubjectId = subject.Id });
        return (section, subject, teacher);
    }

    // SDA-18: acceptance-critical — "every enrolled subject has a non-empty course-info
    // and teacher-info entry".
    [Fact]
    public async Task Sda18_Mine_ReturnsCourseAndTeacherInfoForEveryEnrolledSubject()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        var (_, subject, teacher) = SeedTaughtSection(db, student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Mine();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var subjects = Assert.IsType<List<MySubjectDto>>(ok.Value);
        var entry = Assert.Single(subjects);
        Assert.Equal(subject.Id, entry.SubjectId);
        Assert.Equal("CS301", entry.SubjectCode);
        Assert.Equal("Operating Systems", entry.SubjectName);
        Assert.Equal(teacher.Id, entry.TeacherId);
        Assert.Equal(teacher.FullName, entry.TeacherName);
    }

    // SDA-18
    [Fact]
    public async Task Sda18_Mine_ExcludesSubjectsFromSectionsCallerIsNotEnrolledIn()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var otherStudent = NewUser(AccountType.Student);
        db.Users.AddRange(student, otherStudent);
        SeedTaughtSection(db, otherStudent);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Mine();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var subjects = Assert.IsType<List<MySubjectDto>>(ok.Value);
        Assert.Empty(subjects);
    }

    // SDA-18: a student enrolled in multiple sections/subjects sees all of them.
    [Fact]
    public async Task Sda18_Mine_ReturnsMultipleSubjectsAcrossDifferentSectionsAndTeachers()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        var (_, firstSubject, firstTeacher) = SeedTaughtSection(db, student);
        var (_, secondSubject, secondTeacher) = SeedTaughtSection(db, student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Mine();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var subjects = Assert.IsType<List<MySubjectDto>>(ok.Value);
        Assert.Equal(2, subjects.Count);
        Assert.Contains(subjects, s => s.SubjectId == firstSubject.Id && s.TeacherId == firstTeacher.Id);
        Assert.Contains(subjects, s => s.SubjectId == secondSubject.Id && s.TeacherId == secondTeacher.Id);
    }

    // SDA-18: Subject.TeacherId is the canonical teacher elsewhere (AssignmentsController
    // gates assignment creation on it) — when it's set, it must win over the
    // TeacherSectionAssignment row's own TeacherId so a student isn't told a different
    // teacher here than assignments for the same subject come from.
    [Fact]
    public async Task Sda18_Mine_PrefersSubjectTeacherId_OverAssignmentTeacher_WhenBothPresent()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var (_, subject, assignmentTeacher) = SeedTaughtSection(db, student);
        var canonicalTeacher = NewUser(AccountType.Teacher);
        db.Users.Add(canonicalTeacher);
        subject.TeacherId = canonicalTeacher.Id;
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Mine();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var subjects = Assert.IsType<List<MySubjectDto>>(ok.Value);
        var entry = Assert.Single(subjects);
        Assert.Equal(canonicalTeacher.Id, entry.TeacherId);
        Assert.NotEqual(assignmentTeacher.Id, entry.TeacherId);
    }

    // SDA-18: co-teaching (two different teachers assigned to the same section+subject,
    // which the schema's unique index on (teacher_id, section_id, subject_id) allows) must
    // not be collapsed into a single entry when Subject.TeacherId is unset — Distinct()
    // should only fold together true duplicates, not legitimately different assignment rows.
    [Fact]
    public async Task Sda18_Mine_DoesNotCollapseCoTeachingAssignments_WhenSubjectTeacherIdIsUnset()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var (section, subject, firstTeacher) = SeedTaughtSection(db, student);
        var secondTeacher = NewUser(AccountType.Teacher);
        db.Users.Add(secondTeacher);
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment
        {
            Id = Guid.NewGuid(),
            TeacherId = secondTeacher.Id,
            SectionId = section.Id,
            SubjectId = subject.Id,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Mine();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var subjects = Assert.IsType<List<MySubjectDto>>(ok.Value);
        Assert.Equal(2, subjects.Count);
        Assert.Contains(subjects, s => s.TeacherId == firstTeacher.Id);
        Assert.Contains(subjects, s => s.TeacherId == secondTeacher.Id);
    }

    // #159: a student enrolled in two different sections that both teach the same subject
    // (with Subject.TeacherId set as the canonical teacher) via different
    // TeacherSectionAssignment-level teachers used to see the subject listed twice, because
    // the old Distinct() ran before the SubjectTeacherId ?? AssignmentTeacherId fallback
    // collapsed the rows to an identical final DTO.
    [Fact]
    public async Task Issue159_Mine_CollapsesSameSubjectAcrossTwoSections_WhenCanonicalTeacherIsSet()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);

        var canonicalTeacher = NewUser(AccountType.Teacher);
        var otherAssignmentTeacher = NewUser(AccountType.Teacher);
        db.Users.AddRange(canonicalTeacher, otherAssignmentTeacher);

        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = Guid.NewGuid(), Code = "CS401", Name = "Distributed Systems", TeacherId = canonicalTeacher.Id };
        db.Subjects.Add(subject);

        var firstSection = new Section { Id = Guid.NewGuid(), DepartmentId = Guid.NewGuid(), Year = 4, Name = "4th Year CSE - A" };
        var secondSection = new Section { Id = Guid.NewGuid(), DepartmentId = Guid.NewGuid(), Year = 4, Name = "4th Year CSE - B" };
        db.Sections.AddRange(firstSection, secondSection);

        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = firstSection.Id, StudentId = student.Id });
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = secondSection.Id, StudentId = student.Id });

        // Two assignment rows for the same subject, via two different sections and two
        // different assignment-level teachers — but Subject.TeacherId (canonical) is the
        // same for both, so after the fallback both collapse to one final teacher.
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = canonicalTeacher.Id, SectionId = firstSection.Id, SubjectId = subject.Id });
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = otherAssignmentTeacher.Id, SectionId = secondSection.Id, SubjectId = subject.Id });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Mine();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var subjects = Assert.IsType<List<MySubjectDto>>(ok.Value);
        var entry = Assert.Single(subjects);
        Assert.Equal(subject.Id, entry.SubjectId);
        Assert.Equal(canonicalTeacher.Id, entry.TeacherId);
    }

    // Admin-facing subject management — gap the platform had no feature ID or endpoint for.
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
        var result = await controller.Create(new CreateSubjectRequest(department.Id, "CS101", "Intro to CS", null));

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
        var result = await controller.Create(new CreateSubjectRequest(otherCollegeDepartment.Id, "EE101", "Circuits", null));

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
        db.Subjects.Add(new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Existing" });
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(new CreateSubjectRequest(department.Id, "CS101", "Intro to CS", null));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_RejectsTeacherFromADifferentCollege()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var otherCollegeTeacher = NewUser(AccountType.Teacher);
        db.Users.AddRange(caller, otherCollegeTeacher);
        db.Departments.Add(department);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(new CreateSubjectRequest(department.Id, "CS101", "Intro to CS", otherCollegeTeacher.Id));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_CreatesSubjectForSameCollegeCaller()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var teacher = new User { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Identifier = "t1", PasswordHash = "hash", FullName = "Teacher One", AccountType = AccountType.Teacher, IsActive = true };
        db.Users.AddRange(caller, teacher);
        db.Departments.Add(department);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.Create(new CreateSubjectRequest(department.Id, "CS101", "Intro to CS", teacher.Id));

        var ok = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<SubjectDto>(ok.Value);
        Assert.Equal("CS101", dto.Code);
        Assert.Equal(teacher.Id, dto.TeacherId);
        Assert.Equal("Teacher One", dto.TeacherName);
    }

    [Fact]
    public async Task List_ReturnsOnlyCallersCollegeSubjects()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var ownDepartment = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var otherDepartment = new Department { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "EE" };
        db.Users.Add(caller);
        db.Departments.AddRange(ownDepartment, otherDepartment);
        db.Subjects.Add(new Subject { Id = Guid.NewGuid(), DepartmentId = ownDepartment.Id, Code = "CS101", Name = "Own" });
        db.Subjects.Add(new Subject { Id = Guid.NewGuid(), DepartmentId = otherDepartment.Id, Code = "EE101", Name = "Other" });
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.List(null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var subjects = Assert.IsType<List<SubjectDto>>(ok.Value);
        Assert.Single(subjects);
        Assert.Equal("CS101", subjects[0].Code);
    }

    [Fact]
    public async Task Update_RejectsDuplicateCodeAgainstAnotherSubjectInSameDepartment()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var existing = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Existing" };
        var toRename = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS102", Name = "ToRename" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Subjects.AddRange(existing, toRename);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.Update(toRename.Id, new UpdateSubjectRequest("CS101", "Renamed", null));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Delete_RejectsWhenSubjectHasTimetableSlots()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        var teacher = NewUser(AccountType.Teacher);
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 1, Name = "1st Year CSE - A" };
        db.Users.AddRange(caller, teacher);
        db.Departments.Add(department);
        db.Subjects.Add(subject);
        db.Sections.Add(section);
        db.TimetableSlots.Add(new TimetableSlot
        {
            Id = Guid.NewGuid(),
            SectionId = section.Id,
            SubjectId = subject.Id,
            TeacherId = teacher.Id,
            DayOfWeek = 1,
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(10, 0),
        });
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.Delete(subject.Id);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Single(db.Subjects.Local, s => s.Id == subject.Id);
    }

    [Fact]
    public async Task Delete_RemovesUnusedSubject()
    {
        await using var db = NewDb();
        var caller = NewUser(AccountType.AdminTier);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = caller.CollegeId, Name = "CS" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro" };
        db.Users.Add(caller);
        db.Departments.Add(department);
        db.Subjects.Add(subject);
        await GrantManageDepartmentsAsync(db, caller);

        var controller = ControllerAs(db, caller);
        var result = await controller.Delete(subject.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.DoesNotContain(db.Subjects.Local, s => s.Id == subject.Id);
    }
}
