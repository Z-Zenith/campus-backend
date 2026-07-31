using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BackendApi.Tests.Controllers;

public class AssignmentsControllerTests
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

    private static Department NewDepartment() => new() { Id = Guid.NewGuid(), Name = "CS", CollegeId = Guid.NewGuid() };

    private static AssignmentsController ControllerAs(
        AppDbContext db, User user, FakeAiServicesClient? aiServices = null, FakeCopyleaksClient? copyleaks = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "TestAuth"));
        return new AssignmentsController(db, aiServices ?? new FakeAiServicesClient(), copyleaks ?? new FakeCopyleaksClient())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal, Request = { Scheme = "https", Host = new HostString("campus.test") } },
            },
        };
    }

    // #135 (already merged, see below) requires the submitting student to be enrolled in a
    // section the assignment's subject is taught to - seed that link too so these #159 tests
    // (which predate #135) still reach the "already submitted" logic they're actually testing.
    private static (Subject Subject, Assignment Assignment) SeedAssignment(AppDbContext db, User teacher, User student, DateTime? windowStart = null, DateTime? windowEnd = null)
    {
        var departmentId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = departmentId, Code = "CS101", Name = "Intro", TeacherId = teacher.Id };
        var assignment = new Assignment
        {
            Id = Guid.NewGuid(),
            SubjectId = subject.Id,
            TeacherId = teacher.Id,
            Title = "HW1",
            Type = AssignmentType.FileUpload,
            DueDate = DateTime.UtcNow.AddDays(1),
            SubmissionWindowStart = windowStart ?? DateTime.UtcNow.AddHours(-1),
            SubmissionWindowEnd = windowEnd ?? DateTime.UtcNow.AddHours(1),
        };
        db.Subjects.Add(subject);
        db.Assignments.Add(assignment);
        db.Sections.Add(new Section { Id = sectionId, DepartmentId = departmentId, Year = 1, Name = "A" });
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacher.Id, SectionId = sectionId, SubjectId = subject.Id });
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = sectionId, StudentId = student.Id });
        return (subject, assignment);
    }

    // #159: Submit had no equivalent of AutoSubmit's "already_submitted" guard, so a student
    // could call it repeatedly and accumulate unlimited submissions for the same assignment.
    [Fact]
    public async Task Issue159_Submit_RejectsSecondSubmission_WithConflict()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var student = NewUser(AccountType.Student);
        db.Users.AddRange(teacher, student);
        var (_, assignment) = SeedAssignment(db, teacher, student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var first = await controller.Submit(assignment.Id, new SubmitAssignmentRequest("https://example.com/a.pdf", AssignmentType.FileUpload));
        Assert.IsType<OkObjectResult>(first.Result);

        var second = await controller.Submit(assignment.Id, new SubmitAssignmentRequest("https://example.com/b.pdf", AssignmentType.FileUpload));

        var conflict = Assert.IsType<ConflictObjectResult>(second.Result);
        Assert.Single(await db.Submissions.Where(s => s.AssignmentId == assignment.Id && s.StudentId == student.Id).ToListAsync());
    }

    // #159: a prior auto-submission also blocks a later manual Submit call, consistent with
    // AutoSubmit's own "already has a submission (manual or auto)" rule.
    [Fact]
    public async Task Issue159_Submit_RejectsWhenAlreadyAutoSubmitted()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var student = NewUser(AccountType.Student);
        db.Users.AddRange(teacher, student);
        var (_, assignment) = SeedAssignment(db, teacher, student);
        db.Submissions.Add(new Submission
        {
            Id = Guid.NewGuid(),
            AssignmentId = assignment.Id,
            StudentId = student.Id,
            ContentUrl = "https://example.com/auto.pdf",
            SubmittedAt = DateTime.UtcNow,
            IsAutosubmitted = true,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Submit(assignment.Id, new SubmitAssignmentRequest("https://example.com/manual.pdf", AssignmentType.FileUpload));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Issue159_Submit_AllowsFirstSubmission()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var student = NewUser(AccountType.Student);
        db.Users.AddRange(teacher, student);
        var (_, assignment) = SeedAssignment(db, teacher, student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Submit(assignment.Id, new SubmitAssignmentRequest("https://example.com/a.pdf", AssignmentType.FileUpload));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SubmissionDto>(ok.Value);
        Assert.False(dto.IsAutosubmitted);
    }

    [Fact]
    public async Task Mine_ReturnsAssignmentsForTheStudentsOwnSection_WithSubmissionStatus()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var student = NewUser(AccountType.Student);
        db.Users.AddRange(teacher, student);
        var (subject, assignment) = SeedAssignment(db, teacher, student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var submitted = await controller.Submit(assignment.Id, new SubmitAssignmentRequest("https://example.com/a.pdf", AssignmentType.FileUpload));
        Assert.IsType<OkObjectResult>(submitted.Result);

        var result = await controller.Mine();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summaries = Assert.IsType<List<AssignmentSummaryDto>>(ok.Value);
        var summary = Assert.Single(summaries);
        Assert.Equal(assignment.Id, summary.Id);
        Assert.Equal(subject.Name, summary.SubjectName);
        Assert.NotNull(summary.SubmittedAt);
        Assert.False(summary.IsLate);
    }

    [Fact]
    public async Task Mine_ExcludesAssignmentsForSubjectsNotTaughtToTheStudentsSection()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var student = NewUser(AccountType.Student);
        db.Users.AddRange(teacher, student);
        // A second, unrelated assignment for a subject/section the student isn't enrolled in.
        var otherTeacher = NewUser(AccountType.Teacher);
        var otherSubject = new Subject { Id = Guid.NewGuid(), DepartmentId = Guid.NewGuid(), Code = "MA101", Name = "Calculus", TeacherId = otherTeacher.Id };
        var otherAssignment = new Assignment
        {
            Id = Guid.NewGuid(),
            SubjectId = otherSubject.Id,
            TeacherId = otherTeacher.Id,
            Title = "Unrelated",
            Type = AssignmentType.Essay,
            DueDate = DateTime.UtcNow.AddDays(1),
            SubmissionWindowStart = DateTime.UtcNow.AddHours(-1),
            SubmissionWindowEnd = DateTime.UtcNow.AddHours(1),
        };
        db.Users.Add(otherTeacher);
        db.Subjects.Add(otherSubject);
        db.Assignments.Add(otherAssignment);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Mine();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var summaries = Assert.IsType<List<AssignmentSummaryDto>>(ok.Value);
        Assert.Empty(summaries);
    }

    private sealed record Fixture(Guid TeacherId, Guid StudentId, Guid OtherStudentId, Guid SubjectId, Guid SectionId, Guid AssignmentId);

    // #135: an assignment's subject is taught to a section via TeacherSectionAssignments,
    // and a student is only bound to that subject by being enrolled (SectionEnrollments) in
    // one of the sections it's taught to. `enrollStudent: false` reproduces the pre-fix IDOR:
    // a student with valid auth but no enrollment link to this assignment's subject at all.
    private static async Task<Fixture> SeedAsync(AppDbContext db, bool enrollStudent = true)
    {
        var collegeId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var otherStudentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();

        db.Departments.Add(new Department { Id = departmentId, CollegeId = collegeId, Name = "CS" });
        db.Users.Add(new User { Id = teacherId, CollegeId = collegeId, Identifier = "teacher-1", PasswordHash = "hash", FullName = "Teacher", IsActive = true, AccountType = AccountType.Teacher, DepartmentId = departmentId });
        db.Users.Add(new User { Id = studentId, CollegeId = collegeId, Identifier = "student-1", PasswordHash = "hash", FullName = "Student", IsActive = true, AccountType = AccountType.Student, DepartmentId = departmentId });
        db.Users.Add(new User { Id = otherStudentId, CollegeId = collegeId, Identifier = "student-2", PasswordHash = "hash", FullName = "Other Student", IsActive = true, AccountType = AccountType.Student, DepartmentId = departmentId });
        db.Sections.Add(new Section { Id = sectionId, DepartmentId = departmentId, Year = 1, Name = "A" });
        db.Subjects.Add(new Subject { Id = subjectId, DepartmentId = departmentId, Code = "CS101", Name = "Intro to CS", TeacherId = teacherId });
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacherId, SectionId = sectionId, SubjectId = subjectId });
        db.Assignments.Add(new Assignment
        {
            Id = assignmentId,
            SubjectId = subjectId,
            TeacherId = teacherId,
            Title = "HW1",
            Type = AssignmentType.FileUpload,
            DueDate = DateTime.UtcNow.AddDays(7),
            SubmissionWindowStart = DateTime.UtcNow.AddHours(-1),
            SubmissionWindowEnd = DateTime.UtcNow.AddDays(7),
        });

        if (enrollStudent)
        {
            db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = sectionId, StudentId = studentId });
        }
        // otherStudentId is deliberately never enrolled anywhere — stands in for "any other
        // authenticated student in the system" probing this assignment id.

        await db.SaveChangesAsync();
        return new Fixture(teacherId, studentId, otherStudentId, subjectId, sectionId, assignmentId);
    }

    private static AssignmentsController ControllerAs(AppDbContext db, Guid userId) =>
        new(db, new FakeAiServicesClient(), new FakeCopyleaksClient())
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

    [Fact]
    public async Task Submit_Succeeds_WhenStudentIsEnrolledInSubjectsSection()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db);
        var controller = ControllerAs(db, fixture.StudentId);

        var result = await controller.Submit(fixture.AssignmentId, new SubmitAssignmentRequest("https://files.campus.local/a.pdf", AssignmentType.FileUpload));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<SubmissionDto>(ok.Value);
    }

    // #135 (IDOR): before the fix, any authenticated student could submit against any
    // assignment id, regardless of enrollment — only AccountType==Student was checked.
    [Fact]
    public async Task Submit_ReturnsNotFound_WhenStudentNotEnrolledInAssignmentsSubject()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db);
        var controller = ControllerAs(db, fixture.OtherStudentId);

        var result = await controller.Submit(fixture.AssignmentId, new SubmitAssignmentRequest("https://files.campus.local/a.pdf", AssignmentType.FileUpload));

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(await db.Submissions.ToListAsync());
    }

    [Fact]
    public async Task AutoSubmit_Succeeds_WhenStudentIsEnrolledInSubjectsSection()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db);
        var controller = ControllerAs(db, fixture.StudentId);

        var result = await controller.AutoSubmit(fixture.AssignmentId, new SubmitAssignmentRequest("https://files.campus.local/a.pdf", AssignmentType.FileUpload));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<SubmissionDto>(ok.Value);
    }

    // #11: Create previously let any AdminTier caller bypass the teacher-ownership
    // check unconditionally — an Admin at a different college than the subject must not be
    // able to create assignments for it.
    [Fact]
    public async Task Create_ForbidsAdmin_WhenSubjectIsAtADifferentCollege()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var otherCollegeId = Guid.NewGuid();
        var teacher = NewUser(AccountType.Teacher, collegeId);
        var admin = NewUser(AccountType.AdminTier, otherCollegeId);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "CS" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro", TeacherId = teacher.Id };
        db.Users.AddRange(teacher, admin);
        db.Departments.Add(department);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var request = new CreateAssignmentRequest(subject.Id, "HW1", null, AssignmentType.FileUpload, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1), null);
        var result = await controller.Create(request);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Empty(await db.Assignments.ToListAsync());
    }

    // #11: same college — an Admin should still be able to create assignments for a
    // subject taught at their own institution, even if they're not its assigned teacher.
    [Fact]
    public async Task Create_AllowsAdmin_WhenSubjectIsAtTheSameCollege()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var teacher = NewUser(AccountType.Teacher, collegeId);
        var admin = NewUser(AccountType.AdminTier, collegeId);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "CS" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro", TeacherId = teacher.Id };
        db.Users.AddRange(teacher, admin);
        db.Departments.Add(department);
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var request = new CreateAssignmentRequest(subject.Id, "HW1", null, AssignmentType.FileUpload, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1), null);
        var result = await controller.Create(request);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // #135 (IDOR): same gap as Submit(), on the auto-submit path used by the desktop app's
    // exit-detection trigger (SDA-11).
    [Fact]
    public async Task AutoSubmit_ReturnsNotFound_WhenStudentNotEnrolledInAssignmentsSubject()
    {
        using var db = NewDb();
        var fixture = await SeedAsync(db);
        var controller = ControllerAs(db, fixture.OtherStudentId);

        var result = await controller.AutoSubmit(fixture.AssignmentId, new SubmitAssignmentRequest("https://files.campus.local/a.pdf", AssignmentType.FileUpload));

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(await db.Submissions.ToListAsync());
    }

    // AIS-03
    [Fact]
    public async Task Ais03_CopyCheck_ForbidsCallersWhoAreNotTheAssignmentsTeacher()
    {
        await using var db = NewDb();
        var otherTeacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = Guid.NewGuid(), Title = "A1", Type = AssignmentType.Code, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        db.Users.Add(otherTeacher);
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherTeacher);
        var result = await controller.CopyCheck(assignment.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Ais03_CopyCheck_PersistsFlaggedMatchesFromAiServices()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Code, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submissionA = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "print(1)", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        var submissionB = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "print(1)", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(teacher);
        db.Assignments.Add(assignment);
        db.Submissions.AddRange(submissionA, submissionB);
        await db.SaveChangesAsync();

        var fakeAi = new FakeAiServicesClient
        {
            SimilarityMatches = [new(submissionA.Id.ToString(), submissionB.Id.ToString(), 0.95)],
        };
        var controller = ControllerAs(db, teacher, fakeAi);
        var result = await controller.CopyCheck(assignment.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var matches = Assert.IsType<List<CopyCheckMatchDto>>(ok.Value);
        var match = Assert.Single(matches);
        Assert.Equal(0.95m, match.SimilarityScore);
        Assert.Single(await db.CopyCheckFlags.ToListAsync());
    }

    [Fact]
    public async Task Ais03_CopyCheck_ReCheckReplacesPreviousFlags()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Code, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submissionA = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "print(1)", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        var submissionB = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "print(2)", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(teacher);
        db.Assignments.Add(assignment);
        db.Submissions.AddRange(submissionA, submissionB);
        db.CopyCheckFlags.Add(new CopyCheckFlag { Id = Guid.NewGuid(), SubmissionAId = submissionA.Id, SubmissionBId = submissionB.Id, SimilarityScore = 0.99m, FlaggedAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();

        // Nothing matches this time — the stale flag from the previous run must go away.
        var controller = ControllerAs(db, teacher, new FakeAiServicesClient());
        var result = await controller.CopyCheck(assignment.Id);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(await db.CopyCheckFlags.ToListAsync());
    }

    // #11: CanAccessAssignmentAsync (shared by CopyCheck, RequestPlagiarismCheck,
    // PlagiarismReport, AutogradeSuggestion, and Grade) previously let any AdminTier caller
    // bypass the teacher-ownership check unconditionally — an Admin at a different college
    // than the assignment's subject must not be able to trigger a copy-check on it.
    [Fact]
    public async Task CopyCheck_ForbidsAdmin_WhenAssignmentSubjectIsAtADifferentCollege()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var otherCollegeId = Guid.NewGuid();
        var teacher = NewUser(AccountType.Teacher, collegeId);
        var admin = NewUser(AccountType.AdminTier, otherCollegeId);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "CS" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro", TeacherId = teacher.Id };
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = subject.Id, TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Code, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        db.Users.AddRange(teacher, admin);
        db.Departments.Add(department);
        db.Subjects.Add(subject);
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.CopyCheck(assignment.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // #11: same college — an Admin should still be able to trigger a copy-check for
    // an assignment at their own institution, even if they're not its assigned teacher.
    [Fact]
    public async Task CopyCheck_AllowsAdmin_WhenAssignmentSubjectIsAtTheSameCollege()
    {
        await using var db = NewDb();
        var collegeId = Guid.NewGuid();
        var teacher = NewUser(AccountType.Teacher, collegeId);
        var admin = NewUser(AccountType.AdminTier, collegeId);
        var department = new Department { Id = Guid.NewGuid(), CollegeId = collegeId, Name = "CS" };
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = department.Id, Code = "CS101", Name = "Intro", TeacherId = teacher.Id };
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = subject.Id, TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Code, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        db.Users.AddRange(teacher, admin);
        db.Departments.Add(department);
        db.Subjects.Add(subject);
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin, new FakeAiServicesClient());
        var result = await controller.CopyCheck(assignment.Id);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // AIS-02
    [Fact]
    public async Task Ais02_RequestPlagiarismCheck_ForbidsCallersWhoAreNotTheAssignmentsTeacher()
    {
        await using var db = NewDb();
        var otherTeacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = Guid.NewGuid(), Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "an essay", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(otherTeacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherTeacher);
        var result = await controller.RequestPlagiarismCheck(submission.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Ais02_RequestPlagiarismCheck_WithoutCopyleaksConfigured_Returns503()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "an essay", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(teacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher, copyleaks: new FakeCopyleaksClient { Configured = false });
        var result = Assert.IsType<ObjectResult>(await controller.RequestPlagiarismCheck(submission.Id));

        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public async Task Ais02_RequestPlagiarismCheck_SubmitsScanKeyedToTheSubmission()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "an essay", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(teacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        var fakeCopyleaks = new FakeCopyleaksClient();
        var controller = ControllerAs(db, teacher, copyleaks: fakeCopyleaks);
        var result = Assert.IsType<AcceptedResult>(await controller.RequestPlagiarismCheck(submission.Id));

        var accepted = Assert.IsType<PlagiarismCheckAcceptedDto>(result.Value);
        Assert.Equal("pending", accepted.Status);
        Assert.Equal(submission.Id.ToString("N"), fakeCopyleaks.LastScanId);
        Assert.Equal("an essay", fakeCopyleaks.LastContent);
        // The shared secret must NOT ride in the webhook URL (it would leak into access/proxy
        // logs); it now travels in properties.developerPayload in the submit body instead.
        Assert.DoesNotContain("secret", fakeCopyleaks.LastWebhookUrlTemplate!);
        Assert.Contains(submission.Id.ToString("N"), fakeCopyleaks.LastWebhookUrlTemplate!);
    }

    [Fact]
    public async Task Ais02_PlagiarismReport_ForbidsCallersWhoAreNotTheAssignmentsTeacher()
    {
        await using var db = NewDb();
        var otherTeacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = Guid.NewGuid(), Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "an essay", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(otherTeacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherTeacher);
        var result = await controller.PlagiarismReport(submission.Id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Ais02_PlagiarismReport_ReturnsPendingStatus_BeforeTheWebhookFires()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "an essay", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(teacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = Assert.IsType<OkObjectResult>(await controller.PlagiarismReport(submission.Id));

        var status = Assert.IsType<PlagiarismReportStatusDto>(result.Value);
        Assert.Equal("pending", status.Status);
    }

    [Fact]
    public async Task Ais02_PlagiarismReport_ReturnsThePersistedReport_OnceOneExists()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "an essay", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(teacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        db.PlagiarismReports.Add(new PlagiarismReport
        {
            Id = Guid.NewGuid(),
            SubmissionId = submission.Id,
            SimilarityScore = 0.12m,
            CopyleaksScanId = submission.Id.ToString("N"),
            MatchedSources = "[\"https://example.com\"]",
            CheckedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = Assert.IsType<OkObjectResult>(await controller.PlagiarismReport(submission.Id));

        var dto = Assert.IsType<PlagiarismReportDto>(result.Value);
        Assert.Equal(0.12m, dto.SimilarityScore);
        Assert.Equal(["https://example.com"], dto.MatchedSources);
    }

    // AIS-04
    [Fact]
    public async Task Ais04_AutogradeSuggestion_ForbidsCallersWhoAreNotTheAssignmentsTeacher()
    {
        await using var db = NewDb();
        var otherTeacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = Guid.NewGuid(), Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "my essay", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(otherTeacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherTeacher);
        var result = await controller.AutogradeSuggestion(submission.Id, new RequestAutogradeSuggestion([new("Thesis", ["thesis"], 1.0)], 100));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Ais04_AutogradeSuggestion_RejectsRubricWeightsThatDoNotSumToOne()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "my essay", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(teacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.AutogradeSuggestion(submission.Id, new RequestAutogradeSuggestion([new("Thesis", ["thesis"], 0.5)], 100));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Ais04_AutogradeSuggestion_PersistsSuggestedGrade_AndReturnsAdvisoryDetail()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "my essay about the thesis", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        db.Users.Add(teacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        var fakeAi = new FakeAiServicesClient
        {
            AutogradeResult = new(82.5, 100, 0.8, ["Thesis"], ["Good coverage of the thesis criterion."]),
        };
        var controller = ControllerAs(db, teacher, fakeAi);
        var result = await controller.AutogradeSuggestion(submission.Id, new RequestAutogradeSuggestion([new("Thesis", ["thesis"], 1.0)], 100));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<AutogradeSuggestionDto>(ok.Value);
        Assert.Equal(82.5m, dto.SuggestedGrade);
        Assert.Equal("Good coverage of the thesis criterion.", Assert.Single(dto.Feedback));

        var stored = Assert.Single(await db.AutogradeSuggestions.ToListAsync());
        Assert.False(stored.ConfirmedByTeacher);
    }

    [Fact]
    public async Task Ais04_Grade_ConfirmsTheSuggestion_ButDoesNotThrowForATeacherOnlyBookkeepingRecord()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacher.Id, Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "my essay", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        var suggestion = new BackendApi.Data.Entities.AutogradeSuggestion { Id = Guid.NewGuid(), SubmissionId = submission.Id, SuggestedGrade = 80m, ConfirmedByTeacher = false };
        db.Users.Add(teacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        db.AutogradeSuggestions.Add(suggestion);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.Grade(submission.Id, new ConfirmGradeRequest(suggestion.Id));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ConfirmedGradeDto>(ok.Value);
        Assert.True(dto.ConfirmedByTeacher);
        Assert.NotNull(dto.ConfirmedAt);
    }

    [Fact]
    public async Task Ais04_Grade_ForbidsCallersWhoAreNotTheAssignmentsTeacher()
    {
        await using var db = NewDb();
        var otherTeacher = NewUser(AccountType.Teacher);
        var assignment = new Assignment { Id = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = Guid.NewGuid(), Title = "A1", Type = AssignmentType.Essay, DueDate = new DateTime(2026, 8, 15), SubmissionWindowStart = new DateTime(2026, 8, 1), SubmissionWindowEnd = new DateTime(2026, 8, 15) };
        var submission = new Submission { Id = Guid.NewGuid(), AssignmentId = assignment.Id, StudentId = Guid.NewGuid(), ContentUrl = "my essay", SubmittedAt = DateTime.UtcNow, IsLate = false, IsAutosubmitted = false };
        var suggestion = new BackendApi.Data.Entities.AutogradeSuggestion { Id = Guid.NewGuid(), SubmissionId = submission.Id, SuggestedGrade = 80m, ConfirmedByTeacher = false };
        db.Users.Add(otherTeacher);
        db.Assignments.Add(assignment);
        db.Submissions.Add(submission);
        db.AutogradeSuggestions.Add(suggestion);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, otherTeacher);
        var result = await controller.Grade(submission.Id, new ConfirmGradeRequest(suggestion.Id));

        Assert.IsType<ForbidResult>(result.Result);
    }
}
