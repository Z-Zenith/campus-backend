using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Services;

public class AuthorizationServiceTests
{
    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AuthorizationService NewService(AppDbContext db) =>
        new(db, NullLogger<AuthorizationService>.Instance);

    private static User NewUser(AccountType accountType = AccountType.Teacher) => new()
    {
        Id = Guid.NewGuid(),
        CollegeId = Guid.NewGuid(),
        Identifier = $"user-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        FullName = "Test User",
        AccountType = accountType,
        IsActive = true,
    };

    // ---- Part A: HasPermissionAsync ----------------------------------------------------

    [Fact]
    public async Task HasPermissionAsync_ExplicitDenyOverridesRoleGrant()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        db.Roles.Add(new Role { Code = "lecturer" });
        db.Permissions.Add(new Permission { Code = "add_internal_marks", Description = "d" });
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = userId, RoleCode = "lecturer", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        (await db.Roles.FindAsync("lecturer"))!.PermissionCodes.Add((await db.Permissions.FindAsync("add_internal_marks"))!);
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "add_internal_marks",
            Granted = false, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await NewService(db).HasPermissionAsync(userId, "add_internal_marks");

        Assert.False(result);
    }

    [Fact]
    public async Task HasPermissionAsync_ExplicitAllowGrantsEvenWithoutRole()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "manage_fees",
            Granted = true, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.True(await NewService(db).HasPermissionAsync(userId, "manage_fees"));
    }

    [Fact]
    public async Task HasPermissionAsync_ExpiredDenyIgnored_FallsThroughToRoleGrant()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        db.Roles.Add(new Role { Code = "hod" });
        db.Permissions.Add(new Permission { Code = "approve_external_marks", Description = "d" });
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = userId, RoleCode = "hod", GrantedAt = DateTime.UtcNow });
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "approve_external_marks",
            Granted = false, ExpiresAt = DateTime.UtcNow.AddDays(-1), GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        (await db.Roles.FindAsync("hod"))!.PermissionCodes.Add((await db.Permissions.FindAsync("approve_external_marks"))!);
        await db.SaveChangesAsync();

        Assert.True(await NewService(db).HasPermissionAsync(userId, "approve_external_marks"));
    }

    [Fact]
    public async Task HasPermissionAsync_ExpiredAllowIgnored_NoRoleFallback_ReturnsFalse()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "manage_fees",
            Granted = true, ExpiresAt = DateTime.UtcNow.AddDays(-1), GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.False(await NewService(db).HasPermissionAsync(userId, "manage_fees"));
    }

    [Fact]
    public async Task HasPermissionAsync_MostRecentExplicitGrantWins()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "manage_fees",
            Granted = true, GrantedBy = Guid.NewGuid(), CreatedAt = now.AddMinutes(-10),
        });
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "manage_fees",
            Granted = false, GrantedBy = Guid.NewGuid(), CreatedAt = now,
        });
        await db.SaveChangesAsync();

        Assert.False(await NewService(db).HasPermissionAsync(userId, "manage_fees"));
    }

    [Fact]
    public async Task HasPermissionAsync_NoExplicitGrant_FallsThroughToRoleCheck()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        db.Roles.Add(new Role { Code = "finance" });
        db.Permissions.Add(new Permission { Code = "manage_fees", Description = "d" });
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = userId, RoleCode = "finance", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        (await db.Roles.FindAsync("finance"))!.PermissionCodes.Add((await db.Permissions.FindAsync("manage_fees"))!);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.HasPermissionAsync(userId, "manage_fees"));
        Assert.False(await service.HasPermissionAsync(userId, "view_all_fee_records"));
    }

    [Fact]
    public async Task HasPermissionAsync_UnknownUser_ReturnsFalse()
    {
        await using var db = NewDb();

        Assert.False(await NewService(db).HasPermissionAsync(Guid.NewGuid(), "manage_fees"));
    }

    [Fact]
    public async Task HasPermissionsAsync_BatchReturnsCorrectPerCodeResults()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "manage_fees",
            Granted = true, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        });
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "reset_password",
            Granted = false, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var results = await NewService(db).HasPermissionsAsync(userId, "manage_fees", "reset_password", "manage_accounts");

        Assert.True(results["manage_fees"]);
        Assert.False(results["reset_password"]);
        Assert.False(results["manage_accounts"]);
    }

    [Fact]
    public async Task GetPermissionGrantStatusAsync_ActiveGrant_ReturnsGrantedAndExpiresAt()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.AddDays(3);
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "add_external_marks",
            Granted = true, ExpiresAt = expiresAt, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (granted, returnedExpiresAt) = await NewService(db).GetPermissionGrantStatusAsync(userId, "add_external_marks");

        Assert.True(granted);
        Assert.Equal(expiresAt, returnedExpiresAt);
    }

    [Fact]
    public async Task GetPermissionGrantStatusAsync_NoGrant_ReturnsFalseAndNullExpiry()
    {
        await using var db = NewDb();

        var (granted, expiresAt) = await NewService(db).GetPermissionGrantStatusAsync(Guid.NewGuid(), "add_external_marks");

        Assert.False(granted);
        Assert.Null(expiresAt);
    }

    [Fact]
    public async Task GetPermissionGrantStatusAsync_ExpiredGrant_ReturnsFalseAndNullExpiry()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "add_external_marks",
            Granted = true, ExpiresAt = DateTime.UtcNow.AddDays(-1), GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (granted, expiresAt) = await NewService(db).GetPermissionGrantStatusAsync(userId, "add_external_marks");

        Assert.False(granted);
        Assert.Null(expiresAt);
    }

    [Fact]
    public async Task GetPermissionGrantStatusAsync_ExplicitDeny_ReturnsFalseAndNullExpiry()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(), UserId = userId, PermissionCode = "add_external_marks",
            Granted = false, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var (granted, expiresAt) = await NewService(db).GetPermissionGrantStatusAsync(userId, "add_external_marks");

        Assert.False(granted);
        Assert.Null(expiresAt);
    }

    // ---- Part B: CheckRelationAsync (base relations) -----------------------------------

    [Fact]
    public async Task CheckRelationAsync_SectionTeacher_CompoundId_ExactSubjectMatch()
    {
        await using var db = NewDb();
        var teacherId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var otherSubjectId = Guid.NewGuid();
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacherId, SectionId = sectionId, SubjectId = subjectId });
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(teacherId, "teacher", "section", $"{sectionId}:{subjectId}"));
        Assert.False(await service.CheckRelationAsync(teacherId, "teacher", "section", $"{sectionId}:{otherSubjectId}"));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "teacher", "section", $"{sectionId}:{subjectId}"));
    }

    [Fact]
    public async Task CheckRelationAsync_SectionTeacher_BareSectionId_AnySubject()
    {
        await using var db = NewDb();
        var teacherId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacherId, SectionId = sectionId, SubjectId = Guid.NewGuid() });
        await db.SaveChangesAsync();

        Assert.True(await NewService(db).CheckRelationAsync(teacherId, "teacher", "section", sectionId.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_SlotTeacher_ReflectsCurrentSlotAssignment()
    {
        await using var db = NewDb();
        var owningTeacher = Guid.NewGuid();
        var otherTeacher = Guid.NewGuid();
        var slot = new TimetableSlot
        {
            Id = Guid.NewGuid(), SectionId = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = owningTeacher,
            DayOfWeek = 1, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        };
        db.TimetableSlots.Add(slot);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(owningTeacher, "teacher", "slot", slot.Id.ToString()));
        Assert.False(await service.CheckRelationAsync(otherTeacher, "teacher", "slot", slot.Id.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_SectionStudent_ReflectsEnrollment()
    {
        await using var db = NewDb();
        var studentId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        db.SectionEnrollments.Add(new SectionEnrollment { Id = Guid.NewGuid(), SectionId = sectionId, StudentId = studentId });
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(studentId, "student", "section", sectionId.ToString()));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "student", "section", sectionId.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_StudentParent_RevocationTakesEffectImmediately()
    {
        await using var db = NewDb();
        var parentId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var link = new ParentWard { Id = Guid.NewGuid(), ParentUserId = parentId, StudentId = studentId, CreatedAt = DateTime.UtcNow };
        db.ParentWards.Add(link);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(parentId, "parent", "student", studentId.ToString()));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "parent", "student", studentId.ToString()));

        db.ParentWards.Remove(link);
        await db.SaveChangesAsync();

        Assert.False(await service.CheckRelationAsync(parentId, "parent", "student", studentId.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_NoteOwner()
    {
        await using var db = NewDb();
        var ownerId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var note = new Note { Id = Guid.NewGuid(), OwnerId = ownerId, Title = "t", ContentMarkdown = "c", CreatedAt = now, UpdatedAt = now };
        db.Notes.Add(note);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(ownerId, "owner", "note", note.Id.ToString()));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "owner", "note", note.Id.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_CodeProjectOwner()
    {
        await using var db = NewDb();
        var ownerId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var project = new CodeProject
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, Name = "p", EntryFilePath = "main.py",
            ActiveFilePath = "main.py", Stdin = "", CreatedAt = now, UpdatedAt = now,
        };
        db.CodeProjects.Add(project);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(ownerId, "owner", "code_project", project.Id.ToString()));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "owner", "code_project", project.Id.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_GroupMemberAndOwner_AreDistinctRelations()
    {
        await using var db = NewDb();
        var creatorId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var group = new Group { Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Name = "g", CreatedBy = creatorId };
        db.Groups.Add(group);
        db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = creatorId, JoinedAt = DateTime.UtcNow });
        db.GroupMembers.Add(new GroupMember { Id = Guid.NewGuid(), GroupId = group.Id, UserId = memberId, JoinedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(memberId, "member", "group", group.Id.ToString()));
        Assert.False(await service.CheckRelationAsync(outsiderId, "member", "group", group.Id.ToString()));
        Assert.True(await service.CheckRelationAsync(creatorId, "owner", "group", group.Id.ToString()));
        // A mere member who isn't the creator must not satisfy "owner" — proves the two
        // relations are genuinely distinct, not aliases of each other.
        Assert.False(await service.CheckRelationAsync(memberId, "owner", "group", group.Id.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_SubmissionOwner()
    {
        await using var db = NewDb();
        var studentId = Guid.NewGuid();
        var submission = new Submission
        {
            Id = Guid.NewGuid(), AssignmentId = Guid.NewGuid(), StudentId = studentId,
            ContentUrl = "http://example.com/x", SubmittedAt = DateTime.UtcNow,
        };
        db.Submissions.Add(submission);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(studentId, "owner", "submission", submission.Id.ToString()));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "owner", "submission", submission.Id.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_ThreadParticipant_EitherSideMatches()
    {
        await using var db = NewDb();
        var studentId = Guid.NewGuid();
        var teacherId = Guid.NewGuid();
        var thread = new MessageThread { Id = Guid.NewGuid(), StudentId = studentId, TeacherId = teacherId, CreatedAt = DateTime.UtcNow };
        db.MessageThreads.Add(thread);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(studentId, "participant", "thread", thread.Id.ToString()));
        Assert.True(await service.CheckRelationAsync(teacherId, "participant", "thread", thread.Id.ToString()));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "participant", "thread", thread.Id.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_EventOwner()
    {
        await using var db = NewDb();
        var creatorId = Guid.NewGuid();
        var ev = new Event
        {
            Id = Guid.NewGuid(), CollegeId = Guid.NewGuid(), Title = "Orientation",
            StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(1), CreatedBy = creatorId,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(creatorId, "owner", "event", ev.Id.ToString()));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "owner", "event", ev.Id.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_AssignmentTeacher()
    {
        await using var db = NewDb();
        var teacherId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var assignment = new Assignment
        {
            Id = Guid.NewGuid(), SubjectId = subjectId, TeacherId = teacherId, Title = "HW1",
            Type = AssignmentType.FileUpload, DueDate = DateTime.UtcNow.AddDays(7),
            SubmissionWindowStart = DateTime.UtcNow, SubmissionWindowEnd = DateTime.UtcNow.AddDays(7),
        };
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(teacherId, "teacher", "assignment", assignment.Id.ToString()));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "teacher", "assignment", assignment.Id.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_SubjectTeacher()
    {
        await using var db = NewDb();
        var teacherId = Guid.NewGuid();
        var subject = new Subject { Id = Guid.NewGuid(), DepartmentId = Guid.NewGuid(), Code = "CS101", Name = "Intro to CS", TeacherId = teacherId };
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(teacherId, "teacher", "subject", subject.Id.ToString()));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "teacher", "subject", subject.Id.ToString()));
    }

    // ---- Part B: CheckRelationAsync (derived relations) ---------------------------------

    [Fact]
    public async Task CheckRelationAsync_DerivedMarkAttendance_DelegatesToSlotTeacher()
    {
        await using var db = NewDb();
        var teacherId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var slot = new TimetableSlot
        {
            Id = Guid.NewGuid(), SectionId = Guid.NewGuid(), SubjectId = Guid.NewGuid(), TeacherId = teacherId,
            DayOfWeek = 1, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        };
        db.TimetableSlots.Add(slot);
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(teacherId, "mark_attendance", "slot", slot.Id.ToString()));
        Assert.False(await service.CheckRelationAsync(studentId, "mark_attendance", "slot", slot.Id.ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_DerivedAddInternalMarks_DelegatesToSectionTeacher()
    {
        await using var db = NewDb();
        var teacherId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        db.TeacherSectionAssignments.Add(new TeacherSectionAssignment { Id = Guid.NewGuid(), TeacherId = teacherId, SectionId = sectionId, SubjectId = subjectId });
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(teacherId, "add_internal_marks", "section", $"{sectionId}:{subjectId}"));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "add_internal_marks", "section", $"{sectionId}:{subjectId}"));
    }

    [Fact]
    public async Task CheckRelationAsync_DerivedViewRecords_DelegatesToParent()
    {
        await using var db = NewDb();
        var parentId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        db.ParentWards.Add(new ParentWard { Id = Guid.NewGuid(), ParentUserId = parentId, StudentId = studentId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.CheckRelationAsync(parentId, "view_records", "student", studentId.ToString()));
        Assert.False(await service.CheckRelationAsync(Guid.NewGuid(), "view_records", "student", studentId.ToString()));
    }

    // ---- Part B: failure modes -----------------------------------------------------------

    [Fact]
    public async Task CheckRelationAsync_UnknownRelation_ThrowsArgumentException()
    {
        await using var db = NewDb();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(db).CheckRelationAsync(Guid.NewGuid(), "no_such_relation", "note", Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_UnknownResourceType_ThrowsArgumentException()
    {
        await using var db = NewDb();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(db).CheckRelationAsync(Guid.NewGuid(), "owner", "no_such_type", Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task CheckRelationAsync_MalformedResourceId_ThrowsArgumentException()
    {
        await using var db = NewDb();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(db).CheckRelationAsync(Guid.NewGuid(), "owner", "note", "not-a-guid"));
    }

    [Fact]
    public async Task CheckRelationAsync_WrongCompoundIdShape_ThrowsArgumentException()
    {
        await using var db = NewDb();

        // "section","teacher" only accepts a bare id or a 2-part "a:b" compound id.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewService(db).CheckRelationAsync(Guid.NewGuid(), "teacher", "section", $"{Guid.NewGuid()}:{Guid.NewGuid()}:{Guid.NewGuid()}"));
    }

    [Fact]
    public async Task CheckRelationsAsync_BatchMixOfValidAndInvalid_ValidResolveAndInvalidThrows()
    {
        await using var db = NewDb();
        var ownerId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var note = new Note { Id = Guid.NewGuid(), OwnerId = ownerId, Title = "t", ContentMarkdown = "c", CreatedAt = now, UpdatedAt = now };
        db.Notes.Add(note);
        await db.SaveChangesAsync();

        var service = NewService(db);

        // The valid check alone still resolves correctly...
        Assert.True(await service.CheckRelationAsync(ownerId, "owner", "note", note.Id.ToString()));

        // ...and a batch containing an invalid entry propagates that failure rather than
        // silently returning a partial result.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CheckRelationsAsync(ownerId,
                ("owner", "note", note.Id.ToString()),
                ("no_such_relation", "note", note.Id.ToString())));
    }

    // ---- Roles / department scope --------------------------------------------------------

    [Fact]
    public async Task HasAnyRoleAsync_TrueWhenUserHoldsAnyListedRole()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = userId, RoleCode = "hod", GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.True(await service.HasAnyRoleAsync(userId, "lecturer", "hod"));
        Assert.False(await service.HasAnyRoleAsync(userId, "finance", "it"));
    }

    [Fact]
    public async Task GetDepartmentScopeAsync_ReturnsHodDepartment_NullWhenNotHod()
    {
        await using var db = NewDb();
        var hodId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = hodId, RoleCode = "hod", DepartmentId = departmentId, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var service = NewService(db);
        Assert.Equal(departmentId, await service.GetDepartmentScopeAsync(hodId));
        Assert.Null(await service.GetDepartmentScopeAsync(otherId));
    }
}
