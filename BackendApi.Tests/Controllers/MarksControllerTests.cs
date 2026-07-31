using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Controllers;

public class MarksControllerTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private sealed record Fixture(AppDbContext Db, Guid TeacherId, Guid StudentId, Guid SubjectId, Guid SectionId, Guid AssignmentId);

    private static async Task<Fixture> SeedAsync(AppDbContext db, bool grantPermission = true, bool enrollStudent = true, bool assignTeacher = true)
    {
        var collegeId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();

        db.Departments.Add(new Department { Id = departmentId, CollegeId = collegeId, Name = "CS" });
        db.Users.Add(new User { Id = teacherId, CollegeId = collegeId, Identifier = "teacher-1", PasswordHash = "hash", FullName = "Teacher One", IsActive = true, AccountType = AccountType.Teacher, DepartmentId = departmentId });
        db.Users.Add(new User { Id = studentId, CollegeId = collegeId, Identifier = "student-1", PasswordHash = "hash", FullName = "Student One", IsActive = true, AccountType = AccountType.Student, DepartmentId = departmentId });
        db.Sections.Add(new Section { Id = sectionId, DepartmentId = departmentId, Year = 1, Name = "A" });
        db.Subjects.Add(new Subject { Id = subjectId, DepartmentId = departmentId, Code = "CS101", Name = "Intro to CS", TeacherId = teacherId });
        db.Assignments.Add(new Assignment
        {
            Id = assignmentId,
            SubjectId = subjectId,
            TeacherId = teacherId,
            Title = "HW1",
            Type = AssignmentType.FileUpload,
            DueDate = DateTime.UtcNow.AddDays(7),
            SubmissionWindowStart = DateTime.UtcNow,
            SubmissionWindowEnd = DateTime.UtcNow.AddDays(7),
        });

        if (assignTeacher)
        {
            db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacherId, SectionId = sectionId, SubjectId = subjectId });
        }
        if (enrollStudent)
        {
            db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = sectionId, StudentId = studentId });
        }
        if (grantPermission)
        {
            db.Roles.Add(new Role { Code = "lecturer" });
            db.Permissions.Add(new Permission { Code = "add_internal_marks", Description = "Publish internal marks (TWA-16)" });
            db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = teacherId, RoleCode = "lecturer", GrantedAt = DateTime.UtcNow });
        }

        await db.SaveChangesAsync();

        if (grantPermission)
        {
            var role = await db.Roles.FindAsync("lecturer");
            var permission = await db.Permissions.FindAsync("add_internal_marks");
            role!.PermissionCodes.Add(permission!);
            await db.SaveChangesAsync();
        }

        return new Fixture(db, teacherId, studentId, subjectId, sectionId, assignmentId);
    }

    private static MarksController BuildController(AppDbContext db, Guid callerId)
    {
        var controller = new MarksController(db, new AuthorizationService(db, NullLogger<AuthorizationService>.Instance), new CollegeScopeService(db));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, callerId.ToString())]);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    [Fact]
    public async Task CreateInternal_PersistsUnpublishedByDefault()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db);
        var controller = BuildController(db, fixture.TeacherId);

        var result = await controller.CreateInternal(new CreateInternalMarkRequest(fixture.StudentId, fixture.SubjectId, fixture.AssignmentId, 87));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<InternalMarkRecordDto>(ok.Value);
        Assert.False(dto.Published);
        Assert.Null(dto.PublishedAt);

        var stored = await db.InternalMarks.SingleAsync();
        Assert.False(stored.Published);
    }

    [Fact]
    public async Task CreateInternal_PublishesWhenRequested()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db);
        var controller = BuildController(db, fixture.TeacherId);

        var result = await controller.CreateInternal(new CreateInternalMarkRequest(fixture.StudentId, fixture.SubjectId, fixture.AssignmentId, 92, Publish: true));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<InternalMarkRecordDto>(ok.Value);
        Assert.True(dto.Published);
        Assert.NotNull(dto.PublishedAt);
    }

    [Fact]
    public async Task CreateInternal_Forbidden_WhenCallerLacksPermission()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db, grantPermission: false);
        var controller = BuildController(db, fixture.TeacherId);

        var result = await controller.CreateInternal(new CreateInternalMarkRequest(fixture.StudentId, fixture.SubjectId, fixture.AssignmentId, 50));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreateInternal_Forbidden_WhenTeacherNotAssignedToSubject()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db, assignTeacher: false);
        var controller = BuildController(db, fixture.TeacherId);

        var result = await controller.CreateInternal(new CreateInternalMarkRequest(fixture.StudentId, fixture.SubjectId, fixture.AssignmentId, 50));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreateInternal_Forbidden_WhenStudentNotEnrolledInTeachersSection()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db, enrollStudent: false);
        var controller = BuildController(db, fixture.TeacherId);

        var result = await controller.CreateInternal(new CreateInternalMarkRequest(fixture.StudentId, fixture.SubjectId, fixture.AssignmentId, 50));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task InternalRoster_ListsEnrolledStudent_WithExistingMark()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db);
        var controller = BuildController(db, fixture.TeacherId);
        await controller.CreateInternal(new CreateInternalMarkRequest(fixture.StudentId, fixture.SubjectId, fixture.AssignmentId, 88, Publish: true));

        var result = await controller.InternalRoster(fixture.SubjectId, fixture.AssignmentId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var roster = Assert.IsType<List<InternalMarksRosterEntryDto>>(ok.Value);
        var entry = Assert.Single(roster);
        Assert.Equal(fixture.StudentId, entry.StudentId);
        Assert.Equal(88, entry.Marks);
        Assert.True(entry.Published);
    }

    [Fact]
    public async Task InternalRoster_Forbidden_WhenCallerNotAssignedToSubject()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db, assignTeacher: false);
        var controller = BuildController(db, fixture.TeacherId);

        var result = await controller.InternalRoster(fixture.SubjectId, fixture.AssignmentId);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task CreateInternal_DoesNotUnpublishOnSubsequentUnpublishedEdit()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db);
        var controller = BuildController(db, fixture.TeacherId);

        await controller.CreateInternal(new CreateInternalMarkRequest(fixture.StudentId, fixture.SubjectId, fixture.AssignmentId, 60, Publish: true));
        var second = await controller.CreateInternal(new CreateInternalMarkRequest(fixture.StudentId, fixture.SubjectId, fixture.AssignmentId, 75));

        var ok = Assert.IsType<OkObjectResult>(second.Result);
        var dto = Assert.IsType<InternalMarkRecordDto>(ok.Value);
        Assert.True(dto.Published);
        Assert.Equal(75, dto.Marks);
        Assert.Single(await db.InternalMarks.ToListAsync());
    }

    private static async Task<(Guid CallerId, Guid StudentId, Guid SubjectId)> SeedExternalMarksFixtureAsync(
        AppDbContext db, Guid callerCollegeId, Guid studentCollegeId, Guid subjectCollegeId)
    {
        var callerId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var department = new Department { Id = Guid.NewGuid(), CollegeId = subjectCollegeId, Name = "CS" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro to CS" };

        db.Departments.Add(department);
        db.Subjects.Add(subject);
        db.Users.Add(new User { Id = callerId, CollegeId = callerCollegeId, Identifier = $"caller-{callerId:N}", PasswordHash = "hash", FullName = "Caller", IsActive = true, AccountType = AccountType.Teacher });
        db.Users.Add(new User { Id = studentId, CollegeId = studentCollegeId, Identifier = $"student-{studentId:N}", PasswordHash = "hash", FullName = "Student", IsActive = true, AccountType = AccountType.Student });
        db.Permissions.Add(new Permission { Code = "add_external_marks", Description = "x" });
        db.Roles.Add(new Role { Code = "hod" });
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = callerId, RoleCode = "hod", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var role = await db.Roles.FindAsync("hod");
        var permission = await db.Permissions.FindAsync("add_external_marks");
        role!.PermissionCodes.Add(permission!);
        await db.SaveChangesAsync();

        return (callerId, studentId, subject.Id);
    }

    // #129 — add_external_marks has no college/department scoping column at all; this
    // endpoint must still reject a grant holder submitting marks for a student or subject
    // outside their own college.
    [Fact]
    public async Task CreateExternal_SucceedsWhenStudentAndSubjectAreInCallersCollege()
    {
        using var db = NewDb();
        var college = Guid.NewGuid();
        var (callerId, studentId, subjectId) = await SeedExternalMarksFixtureAsync(db, college, college, college);
        var controller = BuildController(db, callerId);

        var result = await controller.CreateExternal(new CreateExternalMarkRequest(studentId, subjectId, "A"));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateExternal_ForbidsCrossCollegeStudent()
    {
        using var db = NewDb();
        var callerCollege = Guid.NewGuid();
        var (callerId, studentId, subjectId) = await SeedExternalMarksFixtureAsync(db, callerCollege, Guid.NewGuid(), callerCollege);
        var controller = BuildController(db, callerId);

        var result = await controller.CreateExternal(new CreateExternalMarkRequest(studentId, subjectId, "A"));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.ExternalMarks.ToListAsync());
    }

    [Fact]
    public async Task CreateExternal_ForbidsCrossCollegeSubject()
    {
        using var db = NewDb();
        var callerCollege = Guid.NewGuid();
        var (callerId, studentId, subjectId) = await SeedExternalMarksFixtureAsync(db, callerCollege, callerCollege, Guid.NewGuid());
        var controller = BuildController(db, callerId);

        var result = await controller.CreateExternal(new CreateExternalMarkRequest(studentId, subjectId, "A"));

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.ExternalMarks.ToListAsync());
    }

    // #83 — regression coverage: CreateExternal/ApproveExternal (TWA-17/TWA-20) already had
    // [Authorize] + an add_external_marks/approve_external_marks permission check wired (not
    // the 501-stub-with-no-auth state #83 was originally filed against), but lacked test
    // coverage locking that guard in place. SeedAsync's default fixture only grants
    // add_internal_marks, so the teacher here has neither external-marks permission.
    [Fact]
    public async Task CreateExternal_Forbidden_WhenCallerLacksAddExternalMarksPermission()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db);
        var controller = BuildController(db, fixture.TeacherId);

        var result = await controller.CreateExternal(new CreateExternalMarkRequest(fixture.StudentId, fixture.SubjectId, "A"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task ApproveExternal_Forbidden_WhenCallerLacksApproveExternalMarksPermission()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db);
        var controller = BuildController(db, fixture.TeacherId);

        var result = await controller.ApproveExternal(Guid.NewGuid());

        Assert.IsType<ForbidResult>(result.Result);
    }

    // #126 (fix: enforce college scope on external-marks approval). The helpers below drive
    // the Admin (global, non-department-scoped) approve_external_marks path: the caller holds
    // the permission via a direct grant and has no hod/department binding, so
    // GetDepartmentScopeAsync returns null and the college clamp is what applies.
    private static PermissionGrant GrantApproveExternalMarks(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        PermissionCode = "approve_external_marks",
        Granted = true,
        GrantedBy = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static User SeedApproverAdmin(AppDbContext db, Guid collegeId)
    {
        var admin = new User { Id = Guid.NewGuid(), CollegeId = collegeId, Identifier = $"admin-{Guid.NewGuid():N}", PasswordHash = "hash", FullName = "Admin", IsActive = true, AccountType = AccountType.AdminTier };
        db.Users.Add(admin);
        db.PermissionGrants.Add(GrantApproveExternalMarks(admin.Id));
        return admin;
    }

    // Builds a pending external mark for a student in the given college. SubmittedBy points at
    // an existing user so the pending-queue projection's SubmittedByNavigation resolves.
    private static ExternalMark SeedPendingExternalMark(AppDbContext db, Guid studentCollegeId, Guid submittedBy)
    {
        var department = new Department { Id = Guid.NewGuid(), CollegeId = studentCollegeId, Name = "CS" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro to CS" };
        var student = new User { Id = Guid.NewGuid(), CollegeId = studentCollegeId, Identifier = $"student-{Guid.NewGuid():N}", PasswordHash = "hash", FullName = "Student", IsActive = true, AccountType = AccountType.Student };
        var mark = new ExternalMark { Id = Guid.NewGuid(), StudentId = student.Id, SubjectId = subject.Id, Grade = "A", SubmittedBy = submittedBy, SubmittedAt = DateTime.UtcNow, Approved = false, Published = false };
        db.Departments.Add(department);
        db.Subjects.Add(subject);
        db.Users.Add(student);
        db.ExternalMarks.Add(mark);
        return mark;
    }

    [Fact]
    public async Task PendingExternal_AdminPath_ExcludesPendingMarksFromOtherColleges()
    {
        using var db = NewDb();
        var callerCollege = Guid.NewGuid();
        var admin = SeedApproverAdmin(db, callerCollege);
        var mine = SeedPendingExternalMark(db, callerCollege, admin.Id);
        SeedPendingExternalMark(db, Guid.NewGuid(), admin.Id);
        await db.SaveChangesAsync();

        var controller = BuildController(db, admin.Id);
        var result = await controller.PendingExternal();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var pending = Assert.IsType<List<PendingExternalMarkDto>>(ok.Value);
        var entry = Assert.Single(pending);
        Assert.Equal(mine.Id, entry.Id);
    }

    [Fact]
    public async Task ApproveExternal_AdminPath_NotFound_ForStudentInAnotherCollege_AndDoesNotFlipApproved()
    {
        using var db = NewDb();
        var callerCollege = Guid.NewGuid();
        var admin = SeedApproverAdmin(db, callerCollege);
        var crossCollegeMark = SeedPendingExternalMark(db, Guid.NewGuid(), admin.Id);
        await db.SaveChangesAsync();

        var controller = BuildController(db, admin.Id);
        var result = await controller.ApproveExternal(crossCollegeMark.Id);

        Assert.IsType<NotFoundResult>(result.Result);
        var reloaded = await db.ExternalMarks.FindAsync(crossCollegeMark.Id);
        Assert.False(reloaded!.Approved);
        Assert.False(reloaded.Published);
    }

    // Note: a same-college approve happy-path test isn't included here because the atomic
    // flip uses ExecuteUpdateAsync, which the EF Core InMemory provider doesn't support (the
    // pre-existing approve tests short-circuit before it for the same reason). The
    // cross-college guard above runs before ExecuteUpdateAsync, so it exercises the fix fully.
}
