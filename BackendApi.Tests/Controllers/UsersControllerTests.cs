using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Tests.Controllers;

public class UsersControllerTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        CollegeId = Guid.NewGuid(),
        Identifier = $"user-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        FullName = "Test User",
        AccountType = AccountType.Teacher,
        IsActive = true,
    };

    private static User NewUser(AccountType accountType, Guid? collegeId = null, bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        CollegeId = collegeId ?? Guid.NewGuid(),
        Identifier = $"user-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        FullName = "Test User",
        AccountType = accountType,
        IsActive = isActive,
    };

    // Mirrors PermissionService's actual resolution, as a direct grant so tests don't need
    // to seed roles/role-bindings just to exercise the controller's permission check.
    private static PermissionGrant GrantViewAllStudentRecords(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        PermissionCode = "view_all_student_records",
        Granted = true,
        GrantedBy = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static PermissionGrant GrantViewAllStudentPerformance(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        PermissionCode = "view_all_student_performance",
        Granted = true,
        GrantedBy = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static UsersController ControllerAs(AppDbContext db, Guid userId, ITotpService? totpService = null) =>
        new(db, new BcryptPasswordHasher(), totpService ?? new TotpService(new EphemeralDataProtectionProvider()), new PermissionService(db), new CollegeScopeService(db))
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

    private static UsersController ControllerAs(AppDbContext db, User user, ITotpService? totpService = null) =>
        ControllerAs(db, user.Id, totpService);

    // AWA-10 / #78 — ResetPassword must bind a request DTO ({"newPassword": "..."}), not a
    // bare JSON string, so it matches every other mutating endpoint's calling convention.
    [Fact]
    public async Task ResetPassword_HashesNewPasswordFromRequestBody()
    {
        await using var db = NewDb();
        var admin = NewUser();
        var target = NewUser();
        target.CollegeId = admin.CollegeId;
        db.Users.AddRange(admin, target);
        db.Permissions.Add(new Permission { Code = "reset_password", Description = "x" });
        var role = new Role { Code = "admin" };
        role.PermissionCodes.Add(db.Permissions.Local.First());
        db.Roles.Add(role);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.ResetPassword(target.Id, new ResetPasswordRequest("a-new-password1"));

        Assert.IsType<NoContentResult>(result);
        var updated = await db.Users.FindAsync(target.Id);
        Assert.NotEqual("hash", updated!.PasswordHash);
    }

    // #140 — no server-side strength check existed before; a 1-character reset password
    // used to be accepted and hashed as-is.
    [Fact]
    public async Task ResetPassword_RejectsWeakPassword()
    {
        await using var db = NewDb();
        var admin = NewUser();
        var target = NewUser();
        target.CollegeId = admin.CollegeId;
        db.Users.AddRange(admin, target);
        db.Permissions.Add(new Permission { Code = "reset_password", Description = "x" });
        var role = new Role { Code = "admin" };
        role.PermissionCodes.Add(db.Permissions.Local.First());
        db.Roles.Add(role);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.ResetPassword(target.Id, new ResetPasswordRequest("a"));

        Assert.IsType<BadRequestObjectResult>(result);
        var unchanged = await db.Users.FindAsync(target.Id);
        Assert.Equal("hash", unchanged!.PasswordHash);
    }

    // #132 — an admin-initiated reset must revoke the target's existing sessions, not just
    // update PasswordHash. Otherwise a JWT issued before the reset (e.g. to an attacker on a
    // compromised account the admin is resetting specifically because of that) stays valid for
    // the rest of its ~60-minute lifetime.
    [Fact]
    public async Task ResetPassword_RevokesAllActiveSessions_ForTheTargetUser()
    {
        await using var db = NewDb();
        var admin = NewUser();
        var target = NewUser();
        target.CollegeId = admin.CollegeId; // same college - #128's cross-college check is orthogonal to this test
        db.Users.AddRange(admin, target);
        db.Permissions.Add(new Permission { Code = "reset_password", Description = "x" });
        var role = new Role { Code = "admin" };
        role.PermissionCodes.Add(db.Permissions.Local.First());
        db.Roles.Add(role);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });

        var targetSession = new UserSession { Id = Guid.NewGuid(), UserId = target.Id, IsActive = true };
        var adminSession = new UserSession { Id = Guid.NewGuid(), UserId = admin.Id, IsActive = true };
        db.UserSessions.AddRange(targetSession, adminSession);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.ResetPassword(target.Id, new ResetPasswordRequest("a-new-password"));

        Assert.IsType<NoContentResult>(result);
        Assert.False((await db.UserSessions.AsNoTracking().SingleAsync(s => s.Id == targetSession.Id)).IsActive);
        // The admin's own session (a different user) must be untouched.
        Assert.True((await db.UserSessions.AsNoTracking().SingleAsync(s => s.Id == adminSession.Id)).IsActive);
    }

    // #128 — cross-college account takeover: an admin holding reset_password (checked
    // globally) must not be able to reset a user's password at a *different* college.
    [Fact]
    public async Task ResetPassword_ForbidsCrossCollegeTarget()
    {
        await using var db = NewDb();
        var admin = NewUser();
        var target = NewUser(); // different (random) CollegeId than admin, by construction
        db.Users.AddRange(admin, target);
        db.Permissions.Add(new Permission { Code = "reset_password", Description = "x" });
        var role = new Role { Code = "admin" };
        role.PermissionCodes.Add(db.Permissions.Local.First());
        db.Roles.Add(role);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.ResetPassword(target.Id, new ResetPasswordRequest("a-new-password"));

        Assert.IsType<ForbidResult>(result);
        var unchanged = await db.Users.FindAsync(target.Id);
        Assert.Equal("hash", unchanged!.PasswordHash);
    }

    // #140 — same policy on account creation: InitialPassword must meet the minimum bar too.
    [Fact]
    public async Task Create_RejectsWeakInitialPassword()
    {
        await using var db = NewDb();
        var admin = NewUser();
        db.Users.Add(admin);
        db.Permissions.Add(new Permission { Code = "manage_accounts", Description = "x" });
        var role = new Role { Code = "admin" };
        role.PermissionCodes.Add(db.Permissions.Local.First());
        db.Roles.Add(role);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin.Id);
        var result = await controller.Create(new CreateUserRequest(admin.CollegeId, AccountType.Student, "new-student", "weak", "New Student", null));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await db.Users.AnyAsync(u => u.Identifier == "new-student"));
    }

    // #131 — Create must never persist the raw Base32 TOTP secret. What lands in
    // User.TotpSecret has to be the encrypted (Protect()'d) form, distinct from both the raw
    // secret and from the one-time value returned to the caller for provisioning.
    [Fact]
    public async Task Create_PersistsEncryptedTotpSecret_NotRawPlaintext()
    {
        await using var db = NewDb();
        var admin = NewUser();
        db.Users.Add(admin);
        db.Permissions.Add(new Permission { Code = "manage_accounts", Description = "x" });
        var role = new Role { Code = "admin" };
        role.PermissionCodes.Add(db.Permissions.Local.First());
        db.Roles.Add(role);
        db.RoleBindings.Add(new RoleBinding { Id = Guid.NewGuid(), UserId = admin.Id, RoleCode = "admin", ScopeType = ScopeKind.Global, GrantedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var totpService = new TotpService(new EphemeralDataProtectionProvider());
        var controller = ControllerAs(db, admin.Id, totpService);
        var request = new CreateUserRequest(admin.CollegeId, AccountType.Student, "student-1", "initial-pass", "New Student", null);

        var result = await controller.Create(request);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<CreateUserResponse>(created.Value);

        var stored = await db.Users.FindAsync(response.UserId);
        Assert.NotNull(stored!.TotpSecret);

        // The DB-facing value must not be (or contain) the raw secret handed back in the
        // creation response — that would mean encryption was skipped entirely.
        Assert.NotEqual(response.TotpSecret, stored.TotpSecret);
        Assert.DoesNotContain(response.TotpSecret, stored.TotpSecret);

        // Round-trip: the stored ciphertext must still decrypt+verify against a code
        // generated from the raw secret that was returned once at creation time.
        var totp = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(response.TotpSecret));
        var code = totp.ComputeTotp();
        Assert.True(totpService.ValidateCode(stored.TotpSecret, code));
    }

    // AWA-09: class-level [Authorize] was added to this controller for AWA-07's GetProfile
    // gating — this endpoint had no authentication or permission check at all before, so
    // it needs its own explicit gate rather than inheriting only "any authenticated user".
    [Fact]
    public async Task Awa09_Create_ForbidsCallersWithoutManageAccountsPermission()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Create(new CreateUserRequest(
            Guid.NewGuid(), AccountType.Student, "new-student", "pw", "New Student", null));

        Assert.IsType<ForbidResult>(result.Result);
    }

    // AWA-09
    [Fact]
    public async Task Awa09_Create_SucceedsForCallerWithManageAccountsPermission()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(),
            UserId = admin.Id,
            PermissionCode = "manage_accounts",
            Granted = true,
            GrantedBy = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.Create(new CreateUserRequest(
            admin.CollegeId, AccountType.Student, "new-student", "initial-pass1", "New Student", null));

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // AWA-10 — same reasoning as Awa09_Create_ForbidsCallersWithoutManageAccountsPermission —
    // acceptance-critical that this doesn't allow arbitrary account takeover.
    [Fact]
    public async Task Awa10_ResetPassword_ForbidsCallersWithoutResetPasswordPermission()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        var victim = NewUser(AccountType.AdminTier);
        db.Users.AddRange(student, victim);
        var originalHash = victim.PasswordHash;
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.ResetPassword(victim.Id, new ResetPasswordRequest("attacker-controlled-password"));

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(originalHash, (await db.Users.FindAsync(victim.Id))!.PasswordHash);
    }

    // AWA-10
    [Fact]
    public async Task Awa10_ResetPassword_SucceedsForCallerWithResetPasswordPermission()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var target = NewUser(AccountType.Student, admin.CollegeId);
        db.Users.AddRange(admin, target);
        db.PermissionGrants.Add(new PermissionGrant
        {
            Id = Guid.NewGuid(),
            UserId = admin.Id,
            PermissionCode = "reset_password",
            Granted = true,
            GrantedBy = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.ResetPassword(target.Id, new ResetPasswordRequest("new-password"));

        Assert.IsType<NoContentResult>(result);
    }

    // AWA-07, AWA-08
    [Fact]
    public async Task Awa07_GetProfile_AllowsSelfView_WithoutAnySpecialPermission()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.GetProfile(student.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StudentRecordDto>(ok.Value);
        Assert.Equal(student.Id, dto.Id);
    }

    // AWA-07
    [Fact]
    public async Task Awa07_GetProfile_ForbidsViewingAnotherStudent_WithoutViewAllStudentRecordsPermission()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        // Same college as the caller so this test isolates the missing-permission branch —
        // a college mismatch alone would also Forbid and mask a broken permission check.
        var student = NewUser(AccountType.Student, teacher.CollegeId);
        db.Users.AddRange(teacher, student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.GetProfile(student.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // AWA-07: this endpoint is scoped to student records specifically — an Admin with
    // view_all_student_records still can't use it to browse another employee's profile.
    [Fact]
    public async Task Awa07_GetProfile_ForbidsViewingNonStudentAccount_EvenWithPermission()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var teacher = NewUser(AccountType.Teacher);
        db.Users.AddRange(admin, teacher);
        db.PermissionGrants.Add(GrantViewAllStudentRecords(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.GetProfile(teacher.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // AWA-07: student records are college-tenant data — an Admin's permission doesn't
    // extend across colleges.
    [Fact]
    public async Task Awa07_GetProfile_ForbidsViewingStudentFromAnotherCollege()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var otherCollegeStudent = NewUser(AccountType.Student);
        db.Users.AddRange(admin, otherCollegeStudent);
        db.PermissionGrants.Add(GrantViewAllStudentRecords(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.GetProfile(otherCollegeStudent.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // AWA-07: acceptance-critical — "record includes remarks... even if the submitting
    // teacher is no longer active".
    [Fact]
    public async Task Awa07_GetProfile_IncludesRemarks_FromADeactivatedTeacher()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student, admin.CollegeId);
        var deactivatedTeacher = NewUser(AccountType.Teacher, isActive: false);
        db.Users.AddRange(admin, student, deactivatedTeacher);
        db.PermissionGrants.Add(GrantViewAllStudentRecords(admin.Id));
        db.TeacherReports.Add(new TeacherReport
        {
            Id = Guid.NewGuid(),
            TeacherId = deactivatedTeacher.Id,
            StudentId = student.Id,
            Content = "Missed three assignments in a row.",
            SubmittedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.GetProfile(student.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StudentRecordDto>(ok.Value);
        var remark = Assert.Single(dto.Remarks);
        Assert.Equal("Missed three assignments in a row.", remark.Content);
        Assert.Equal(deactivatedTeacher.Id, remark.TeacherId);
        Assert.Equal(deactivatedTeacher.FullName, remark.TeacherName);
    }

    // AWA-07: system-generated reports (AIS-01 browsing summary, AIS-07 suspicious flag)
    // surface alongside remarks.
    [Fact]
    public async Task Awa07_GetProfile_IncludesBrowsingSummariesAndSuspiciousFlags()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student, admin.CollegeId);
        db.Users.AddRange(admin, student);
        db.PermissionGrants.Add(GrantViewAllStudentRecords(admin.Id));
        db.BrowsingHistorySummaries.Add(new BrowsingHistorySummary
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            SummaryText = "Mostly educational sites this week.",
            GeneratedAt = DateTime.UtcNow,
        });
        db.SuspiciousFlags.Add(new SuspiciousFlag
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            ConfidenceScore = 0.87m,
            FlaggedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.GetProfile(student.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StudentRecordDto>(ok.Value);
        Assert.Single(dto.BrowsingSummaries);
        Assert.Single(dto.SuspiciousFlags);
    }

    // AWA-07
    [Fact]
    public async Task Awa07_GetProfile_ReturnsNotFound_ForUnknownUser()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        db.PermissionGrants.Add(GrantViewAllStudentRecords(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.GetProfile(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // AWA-08: view_all_student_performance alone is sufficient to view the student at
    // all (not Forbidden) — the endpoint's AWA-07 sections are gated separately, see
    // Awa08_GetProfile_PerformanceOnlyPermission_DoesNotUnlockAwa07Data.
    [Fact]
    public async Task Awa08_GetProfile_AllowsViewByPerformancePermissionAlone()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student, admin.CollegeId);
        db.Users.AddRange(admin, student);
        db.PermissionGrants.Add(GrantViewAllStudentPerformance(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.GetProfile(student.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StudentRecordDto>(ok.Value);
        Assert.Equal(student.Id, dto.Id);
    }

    // AWA-08: published-only filter — same rule SDA-15 enforces on Mine(). An
    // unpublished mark is invisible to the student, so it must be invisible to
    // Admin too; otherwise Admin would see data the student cannot see themselves.
    [Fact]
    public async Task Awa08_GetProfile_ReturnsOnlyPublishedMarks()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student, admin.CollegeId);
        var subject = new Subject
        {
            Id = Guid.NewGuid(),
            DepartmentId = Guid.NewGuid(),
            Code = $"SUB-{Guid.NewGuid():N}"[..8],
            Name = "Mathematics",
        };
        db.Users.AddRange(admin, student);
        db.Subjects.Add(subject);
        db.PermissionGrants.Add(GrantViewAllStudentPerformance(admin.Id));
        db.InternalMarks.AddRange(
            new InternalMark { Id = Guid.NewGuid(), StudentId = student.Id, SubjectId = subject.Id, Marks = 90, Published = true, PublishedAt = DateTime.UtcNow },
            new InternalMark { Id = Guid.NewGuid(), StudentId = student.Id, SubjectId = subject.Id, Marks = 40, Published = false });
        db.ExternalMarks.Add(new ExternalMark
        {
            Id = Guid.NewGuid(),
            StudentId = student.Id,
            SubjectId = subject.Id,
            Grade = "A",
            Published = true,
            ApprovedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.GetProfile(student.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StudentRecordDto>(ok.Value);
        var published = Assert.Single(dto.InternalMarks);
        Assert.Equal(90, published.Marks);
        Assert.Equal("Mathematics", published.SubjectName);
        var external = Assert.Single(dto.ExternalMarks);
        Assert.Equal("A", external.Grade);
    }

    // AWA-07/AWA-08: the two permissions are gated independently, not ORed into one
    // blanket gate — a caller with ONLY view_all_student_performance (e.g. a registrar
    // granted marks-only access via AWA-13) must see marks but NOT the more sensitive
    // AWA-07 data (remarks, browsing summaries, suspicious flags).
    [Fact]
    public async Task Awa08_GetProfile_PerformanceOnlyPermission_DoesNotUnlockAwa07Data()
    {
        await using var db = NewDb();
        var registrar = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student, registrar.CollegeId);
        db.Users.AddRange(registrar, student);
        db.PermissionGrants.Add(GrantViewAllStudentPerformance(registrar.Id));
        db.TeacherReports.Add(new TeacherReport
        {
            Id = Guid.NewGuid(),
            TeacherId = registrar.Id,
            StudentId = student.Id,
            Content = "Confidential disciplinary note.",
            SubmittedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, registrar);
        var result = await controller.GetProfile(student.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StudentRecordDto>(ok.Value);
        Assert.Empty(dto.Remarks);
    }

    // AWA-07/AWA-08: the reverse of the above — view_all_student_records alone doesn't
    // unlock marks.
    [Fact]
    public async Task Awa07_GetProfile_RecordsOnlyPermission_DoesNotUnlockAwa08Marks()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var student = NewUser(AccountType.Student, admin.CollegeId);
        var subject = new Subject
        {
            Id = Guid.NewGuid(),
            DepartmentId = Guid.NewGuid(),
            Code = $"SUB-{Guid.NewGuid():N}"[..8],
            Name = "Mathematics",
        };
        db.Users.AddRange(admin, student);
        db.Subjects.Add(subject);
        db.PermissionGrants.Add(GrantViewAllStudentRecords(admin.Id));
        db.InternalMarks.Add(new InternalMark { Id = Guid.NewGuid(), StudentId = student.Id, SubjectId = subject.Id, Marks = 90, Published = true, PublishedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.GetProfile(student.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<StudentRecordDto>(ok.Value);
        Assert.Empty(dto.InternalMarks);
    }

    // AWA-08: same scope rules as AWA-07 — student target only, same college only.
    [Fact]
    public async Task Awa08_GetProfile_ForbidsViewingAnotherStudent_WithoutAnyPermission()
    {
        await using var db = NewDb();
        var teacher = NewUser(AccountType.Teacher);
        var student = NewUser(AccountType.Student, teacher.CollegeId);
        db.Users.AddRange(teacher, student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, teacher);
        var result = await controller.GetProfile(student.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // AWA-08: the cross-college isolation, exercised against the AWA-08 permission
    // specifically (AWA-07 already has its own equivalent test).
    [Fact]
    public async Task Awa08_GetProfile_ForbidsStudentFromAnotherCollege_EvenWithPermission()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var otherCollegeStudent = NewUser(AccountType.Student);
        db.Users.AddRange(admin, otherCollegeStudent);
        db.PermissionGrants.Add(GrantViewAllStudentPerformance(admin.Id));
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.GetProfile(otherCollegeStudent.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // Search must not be reachable by a caller holding none of the admin-capability
    // permissions — otherwise any authenticated token (including a student's) could
    // enumerate every user in the college by name.
    [Fact]
    public async Task Search_ForbidsCallerWithNoAdminCapabilityPermission()
    {
        await using var db = NewDb();
        var student = NewUser(AccountType.Student);
        db.Users.Add(student);
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, student);
        var result = await controller.Search("anything");

        Assert.IsType<ForbidResult>(result.Result);
    }

    // Any one admin-capability permission is enough to search — the search itself is
    // low-sensitivity (name/identifier only); each caller's actual write action
    // re-checks its own specific permission independently.
    [Fact]
    public async Task Search_SucceedsForCallerHoldingAnyAdminCapabilityPermission()
    {
        await using var db = NewDb();
        var finance = NewUser(AccountType.AdminTier);
        var target = NewUser(AccountType.Student, finance.CollegeId);
        target.FullName = "Alice Example";
        db.Users.AddRange(finance, target);
        db.PermissionGrants.Add(new PermissionGrant { Id = Guid.NewGuid(), UserId = finance.Id, PermissionCode = "manage_fees", Granted = true, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, finance);
        var result = await controller.Search("alice");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var matches = Assert.IsAssignableFrom<List<UserSearchResultDto>>(ok.Value);
        var match = Assert.Single(matches);
        Assert.Equal(target.Id, match.Id);
    }

    // Matches on Identifier too, not just FullName (e.g. searching by roll number/username).
    [Fact]
    public async Task Search_MatchesOnIdentifier()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var target = NewUser(AccountType.Student, admin.CollegeId);
        target.Identifier = "roll-2026-042";
        db.Users.AddRange(admin, target);
        db.PermissionGrants.Add(new PermissionGrant { Id = Guid.NewGuid(), UserId = admin.Id, PermissionCode = "manage_accounts", Granted = true, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.Search("2026-042");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var matches = Assert.IsAssignableFrom<List<UserSearchResultDto>>(ok.Value);
        Assert.Single(matches);
    }

    // College-scoped: this endpoint must not let an admin-capable caller enumerate
    // another college's users by name — same tenant-isolation rule every other
    // cross-college-sensitive endpoint in this controller already enforces.
    [Fact]
    public async Task Search_ExcludesUsersFromAnotherCollege()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        var otherCollegeUser = NewUser(AccountType.Student);
        otherCollegeUser.FullName = "Alice Example";
        db.Users.AddRange(admin, otherCollegeUser);
        db.PermissionGrants.Add(new PermissionGrant { Id = Guid.NewGuid(), UserId = admin.Id, PermissionCode = "manage_accounts", Granted = true, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.Search("alice");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var matches = Assert.IsAssignableFrom<List<UserSearchResultDto>>(ok.Value);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task Search_ReturnsEmptyList_ForBlankQuery()
    {
        await using var db = NewDb();
        var admin = NewUser(AccountType.AdminTier);
        db.Users.Add(admin);
        db.PermissionGrants.Add(new PermissionGrant { Id = Guid.NewGuid(), UserId = admin.Id, PermissionCode = "manage_accounts", Granted = true, GrantedBy = Guid.NewGuid(), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = ControllerAs(db, admin);
        var result = await controller.Search("   ");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var matches = Assert.IsAssignableFrom<List<UserSearchResultDto>>(ok.Value);
        Assert.Empty(matches);
    }
}
