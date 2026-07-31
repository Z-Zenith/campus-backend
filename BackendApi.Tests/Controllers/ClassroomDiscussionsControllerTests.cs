using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Tests.Controllers;

public class ClassroomDiscussionsControllerTests
{
    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

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

    private static ClassroomDiscussionsController ControllerAs(AppDbContext db, User user) =>
        new(db)
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

    private static (Department department, Section section, Subject subject) SeedAcademicStructure(AppDbContext db, Guid collegeId)
    {
        var department = new Department { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "CS" };
        var section = new Section { Id = Guid.NewGuid(), DepartmentId = department.Id, Year = 3, Name = "3rd Year CSE - A" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS301", Name = "Data Structures" };
        db.Departments.Add(department);
        db.Sections.Add(section);
        db.Subjects.Add(subject);
        return (department, section, subject);
    }

    [Fact]
    public async Task Provision_CreatesOneDiscussionPerSectionSubjectPair()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var (_, section, subject) = SeedAcademicStructure(db, admin.CollegeId);
        var teacher = NewUser(AccountType.Teacher, admin.CollegeId);
        db.Users.AddRange(admin, teacher);
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment
        {
            Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = section.Id, SubjectId = subject.Id,
        });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, admin).Provision();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProvisionClassroomDiscussionsResponse>(ok.Value);
        Assert.Equal(1, response.DiscussionsCreated);
        Assert.True(await db.ClassroomDiscussions.AnyAsync(d => d.SectionId == section.Id && d.SubjectId == subject.Id));
    }

    [Fact]
    public async Task Provision_IsIdempotent()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var (_, section, subject) = SeedAcademicStructure(db, admin.CollegeId);
        var teacher = NewUser(AccountType.Teacher, admin.CollegeId);
        db.Users.AddRange(admin, teacher);
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment
        {
            Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = section.Id, SubjectId = subject.Id,
        });
        await db.SaveChangesAsync();

        await ControllerAs(db, admin).Provision();
        var second = await ControllerAs(db, admin).Provision();

        var ok = Assert.IsType<OkObjectResult>(second.Result);
        var response = Assert.IsType<ProvisionClassroomDiscussionsResponse>(ok.Value);
        Assert.Equal(0, response.DiscussionsCreated);
        Assert.Equal(1, await db.ClassroomDiscussions.CountAsync());
    }

    [Fact]
    public async Task Provision_ForbidsNonAdmin()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        db.Users.Add(teacher);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, teacher).Provision();

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task MyDiscussions_ReturnsOnlyDiscussionsForTheStudentsEnrolledSections()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var student = NewUser(AccountType.Student, collegeId);
        var (_, mySection, subject) = SeedAcademicStructure(db, collegeId);
        var otherSection = new Section { Id = Guid.NewGuid(), DepartmentId = mySection.DepartmentId, Year = 3, Name = "3rd Year CSE - B" };
        var myDiscussion = new ClassroomDiscussion { Id = Guid.NewGuid(), SectionId = mySection.Id, SubjectId = subject.Id, CreatedAt = DateTime.UtcNow };
        var otherDiscussion = new ClassroomDiscussion { Id = Guid.NewGuid(), SectionId = otherSection.Id, SubjectId = subject.Id, CreatedAt = DateTime.UtcNow };
        db.Users.Add(student);
        db.Sections.Add(otherSection);
        db.ClassroomDiscussions.AddRange(myDiscussion, otherDiscussion);
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = mySection.Id, StudentId = student.Id });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, student).MyDiscussions();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var discussions = Assert.IsType<List<ClassroomDiscussionDto>>(ok.Value);
        Assert.Single(discussions);
        Assert.Equal(myDiscussion.Id, discussions[0].Id);
    }

    [Fact]
    public async Task CreatePost_AllowsEnrolledStudent()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var student = NewUser(AccountType.Student, collegeId);
        var (_, section, subject) = SeedAcademicStructure(db, collegeId);
        var discussion = new ClassroomDiscussion { Id = Guid.NewGuid(), SectionId = section.Id, SubjectId = subject.Id, CreatedAt = DateTime.UtcNow };
        db.Users.Add(student);
        db.ClassroomDiscussions.Add(discussion);
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = section.Id, StudentId = student.Id });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, student).CreatePost(discussion.Id, new CreateClassroomDiscussionPostRequest("Hello"));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreatePost_ForbidsStudentNotEnrolledInTheSection()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var outsider = NewUser(AccountType.Student, collegeId);
        var (_, section, subject) = SeedAcademicStructure(db, collegeId);
        var discussion = new ClassroomDiscussion { Id = Guid.NewGuid(), SectionId = section.Id, SubjectId = subject.Id, CreatedAt = DateTime.UtcNow };
        db.Users.Add(outsider);
        db.ClassroomDiscussions.Add(discussion);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, outsider).CreatePost(discussion.Id, new CreateClassroomDiscussionPostRequest("Hello"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreatePost_AllowsTeacherAssignedToThatSectionAndSubject()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var teacher = NewUser(AccountType.Teacher, collegeId);
        var (_, section, subject) = SeedAcademicStructure(db, collegeId);
        var discussion = new ClassroomDiscussion { Id = Guid.NewGuid(), SectionId = section.Id, SubjectId = subject.Id, CreatedAt = DateTime.UtcNow };
        db.Users.Add(teacher);
        db.ClassroomDiscussions.Add(discussion);
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment
        {
            Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = section.Id, SubjectId = subject.Id,
        });
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, teacher).CreatePost(discussion.Id, new CreateClassroomDiscussionPostRequest("Welcome"));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreatePost_ForbidsTeacherNotAssignedToThatSectionSubjectPair()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var otherTeacher = NewUser(AccountType.Teacher, collegeId);
        var (_, section, subject) = SeedAcademicStructure(db, collegeId);
        var discussion = new ClassroomDiscussion { Id = Guid.NewGuid(), SectionId = section.Id, SubjectId = subject.Id, CreatedAt = DateTime.UtcNow };
        db.Users.Add(otherTeacher);
        db.ClassroomDiscussions.Add(discussion);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, otherTeacher).CreatePost(discussion.Id, new CreateClassroomDiscussionPostRequest("Hello"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    // #11-class regression: AdminTier previously bypassed CanAccessAsync unconditionally.
    [Fact]
    public async Task CreatePost_ForbidsAdminFromAnotherCollege()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var admin = NewUser(AccountType.AdminTier);
        var (_, section, subject) = SeedAcademicStructure(db, collegeId);
        var discussion = new ClassroomDiscussion { Id = Guid.NewGuid(), SectionId = section.Id, SubjectId = subject.Id, CreatedAt = DateTime.UtcNow };
        db.Users.Add(admin);
        db.ClassroomDiscussions.Add(discussion);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, admin).CreatePost(discussion.Id, new CreateClassroomDiscussionPostRequest("Hello"));

        Assert.IsType<ForbidResult>(result.Result);
    }
}
