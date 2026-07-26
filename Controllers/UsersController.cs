using System.Security.Claims;
using BackendApi.Contracts;
using BackendApi.Data;
using BackendApi.Data.Entities;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController(AppDbContext db, IPasswordHasher passwordHasher, ITotpService totpService, IPermissionService permissions, ICollegeScopeService collegeScope) : ControllerBase
{
    // AWA-09: account creation. Returns the TOTP provisioning URI once, at creation time,
    // since SDA-02/TWA-03 login always requires a TOTP code alongside password.
    //
    // Gated on manage_accounts now that the controller carries [Authorize] (added for
    // AWA-07): previously this action had no authentication at all, so adding
    // authentication alone would have left it reachable — and unrestricted — by any
    // logged-in account of any role. That's a wider hole than before, not a narrower
    // one, so the permission check has to land in the same change that adds [Authorize].
    [HttpPost]
    public async Task<ActionResult<CreateUserResponse>> Create(CreateUserRequest request)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (!await permissions.HasPermissionAsync(caller.Id, "manage_accounts"))
        {
            return Forbid();
        }
        // #129: manage_accounts is a global-scoped permission enforced platform-wide today,
        // but is meant to be college-scoped (architecture doc Section 9) — without this, an
        // IT/Admin at one college could create accounts (including admin/it/finance) at any
        // other college.
        if (!await collegeScope.IsSameCollegeAsync(caller.Id, request.CollegeId))
        {
            return Forbid();
        }

        // #140: enforce a minimum strength policy before hashing, same as ResetPassword and
        // AuthController.ChangePassword — an account's very first password shouldn't be weaker
        // than what every later password change requires.
        if (!PasswordPolicy.IsValid(request.InitialPassword, out var passwordError))
        {
            return BadRequest(new { error = "weak_password", message = passwordError });
        }

        // #131: only the raw secret (used below for the one-time provisioning URI/response)
        // ever exists outside the DB. What lands in User.TotpSecret is always the encrypted
        // form — never the raw Base32 value GenerateSecret() returns.
        var totpSecret = totpService.GenerateSecret();

        var user = new User
        {
            Id = Guid.NewGuid(),
            CollegeId = request.CollegeId,
            AccountType = request.AccountType,
            Identifier = request.Identifier,
            PasswordHash = passwordHasher.Hash(request.InitialPassword),
            TotpSecret = totpService.Protect(totpSecret),
            FullName = request.FullName,
            DepartmentId = request.DepartmentId,
            IsActive = true,
        };

        db.Users.Add(user);

        // A freshly-created teacher account previously got no RoleBinding at all, so
        // HasPermissionAsync("create_group") (and every other role-default permission,
        // e.g. add_internal_marks) returned false until an Admin separately bound them via
        // RolesController — meaning "create a group" silently 403'd for TWA-05's own stated
        // acceptance criterion ("every teacher account... can create at least one group").
        // Bind every new teacher to the baseline "lecturer" role by default; HoD or any
        // additional role/permission grant is still assigned separately via RolesController
        // as before — this only establishes the floor every teacher account is guaranteed.
        //
        // Scope matters here, not just the role code: roles.default_scope_kind seeds
        // "lecturer" as department-scoped (db/init/02_seed_roles_and_permissions.sql), the
        // same restriction RolesController.CreateRoleBinding enforces for any *manually*
        // created lecturer binding (ScopeKind.Department requires a DepartmentId). Hardcoding
        // Global here would grant a broader scope than an Admin manually binding the same
        // role would ever be allowed to create — a real privilege escalation even though
        // HasPermissionAsync doesn't currently branch on ScopeType for this role, since any
        // future scope-aware check (or a department-scoped read like GetDepartmentScopeAsync
        // gaining a lecturer case) must not retroactively see this account as global-scoped.
        // Global is only a fallback for the (permitted, per CreateUserRequest) case where no
        // department was supplied at all — TWA-05's guarantee must hold even then.
        if (request.AccountType == AccountType.Teacher)
        {
            db.RoleBindings.Add(new RoleBinding
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RoleCode = "lecturer",
                ScopeType = user.DepartmentId is not null ? ScopeKind.Department : ScopeKind.Global,
                DepartmentId = user.DepartmentId,
                GrantedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();

        var provisioningUri = totpService.BuildProvisioningUri(totpSecret, request.Identifier, "Campus Platform");
        return CreatedAtAction(nameof(Create), new { id = user.Id }, new CreateUserResponse(user.Id, provisioningUri, totpSecret));
    }

    // AWA-07, AWA-08. Self-view needs no special permission. Viewing another user's
    // record is scoped to students in the caller's own college; view_all_student_records
    // and view_all_student_performance are gated *independently per data section* below —
    // NOT ORed into one blanket gate. AWA-13 lets Admin grant either permission code to a
    // user on its own (e.g. a registrar granted view_all_student_performance only, for
    // report-card generation), specifically so it can diverge from view_all_student_records
    // (remarks/browsing-history/suspicious-flags are materially more sensitive than marks).
    // Collapsing the two into an OR would let a marks-only grant see disciplinary/behavioural
    // data it was never meant to reach — so each section checks its own permission, and only
    // the "is this a valid target at all" gate is shared.
    [HttpGet("{id}/profile")]
    public async Task<ActionResult<StudentRecordDto>> GetProfile(Guid id)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }

        var user = await db.Users.FindAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var isSelf = caller.Id == id;
        var isValidCrossViewTarget = user.AccountType == AccountType.Student && user.CollegeId == caller.CollegeId;

        // New TWA teacher-facing performance view (per-student, not just TWA-04's
        // section-aggregate dashboard) — deliberately scoped narrower than the
        // college-wide view_all_student_performance grant: a teacher can view performance
        // only for a student enrolled in a section that teacher is actually assigned to
        // (same TeacherSectionAssignments + SectionEnrollments join every other
        // teacher-scoped endpoint in this codebase uses, e.g. MarksController.InternalRoster).
        // This is treated as an extension of access the teacher already effectively has via
        // TWA-04's section aggregate and TWA-16's marks entry, not a new broad grant — it
        // does NOT extend to canViewRecords (remarks/browsing-history/suspicious-flags),
        // which stay gated behind the more sensitive view_all_student_records permission.
        var isTeachersOwnStudent = !isSelf
            && caller.AccountType == AccountType.Teacher
            && user.AccountType == AccountType.Student
            && user.CollegeId == caller.CollegeId
            && await IsTeachersOwnStudentAsync(caller.Id, id);

        var canViewRecords = isSelf
            || (isValidCrossViewTarget && await permissions.HasPermissionAsync(caller.Id, "view_all_student_records"));
        var canViewPerformance = isSelf
            || isTeachersOwnStudent
            || (isValidCrossViewTarget && await permissions.HasPermissionAsync(caller.Id, "view_all_student_performance"));

        if (!canViewRecords && !canViewPerformance)
        {
            return Forbid();
        }

        // r.Teacher.FullName in the projection below joins regardless of Teacher.IsActive —
        // a remark must survive the submitting teacher being deactivated later (AWA-07
        // acceptance criterion).
        var remarks = canViewRecords
            ? await db.TeacherReports
                .Where(r => r.StudentId == id)
                .OrderByDescending(r => r.SubmittedAt)
                .Select(r => new TeacherRemarkDto(r.Id, r.TeacherId, r.Teacher.FullName, r.Content, r.SubmittedAt))
                .ToListAsync()
            : [];

        var browsingSummaries = canViewRecords
            ? await db.BrowsingHistorySummaries
                .Where(s => s.StudentId == id)
                .OrderByDescending(s => s.GeneratedAt)
                .Select(s => new BrowsingSummaryReportDto(s.Id, s.SummaryText, s.GeneratedAt))
                .ToListAsync()
            : [];

        var suspiciousFlags = canViewRecords
            ? await db.SuspiciousFlags
                .Where(f => f.StudentId == id)
                .OrderByDescending(f => f.FlaggedAt)
                .Select(f => new SuspiciousFlagReportDto(f.Id, f.ConfidenceScore, f.FlaggedAt, f.AssignmentId, f.ClassSessionId))
                .ToListAsync()
            : [];

        // AWA-08: same PublishedMarksQueries helper MarksController's Mine() (SDA-15) and
        // Ward() (PRT-02) call — one query per mark type, shared by every "view a
        // student's marks" surface, so none of the three can drift on the publish rule.
        var internalMarks = canViewPerformance
            ? await PublishedMarksQueries.GetPublishedInternalMarksAsync(db, id)
            : [];
        var externalMarks = canViewPerformance
            ? await PublishedMarksQueries.GetPublishedExternalMarksAsync(db, id)
            : [];

        return Ok(new StudentRecordDto(
            user.Id,
            user.FullName,
            user.Identifier,
            user.AccountType.ToString(),
            user.CollegeId,
            user.DepartmentId,
            user.IsActive,
            remarks,
            browsingSummaries,
            suspiciousFlags,
            internalMarks,
            externalMarks));
    }

    // AWA-10. Same reasoning as Create above: gated on reset_password so that adding
    // [Authorize] to this controller (for AWA-07) doesn't leave this reachable — and
    // able to take over any account — for any authenticated caller regardless of role.
    [HttpPost("{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetPasswordRequest request)
    {
        var caller = await CurrentUserAsync();
        if (caller is null)
        {
            return Unauthorized();
        }
        if (!await permissions.HasPermissionAsync(caller.Id, "reset_password"))
        {
            return Forbid();
        }

        var user = await db.Users.FindAsync(id);
        if (user is null)
        {
            return NotFound();
        }
        // #128: reset_password is a global-scoped permission today; without this check any
        // holder (typically IT) can take over accounts — including other colleges' admins —
        // by resetting their password. Mirrors GetProfile's existing correct CollegeId check.
        if (user.CollegeId != caller.CollegeId)
        {
            return Forbid();
        }

        // #140: same minimum strength policy as account creation and self-service change.
        if (!PasswordPolicy.IsValid(request.NewPassword, out var passwordError))
        {
            return BadRequest(new { error = "weak_password", message = passwordError });
        }

        user.PasswordHash = passwordHasher.Hash(request.NewPassword);

        // #132 — an admin-initiated reset must also cut off any session issued under the old
        // password (often the exact reason the reset is being done — a phished/compromised
        // account). Without this, the target's existing JWT/session stayed valid for the rest
        // of its ~60-minute lifetime regardless of the reset. Mirrors the single-active-session
        // flip Login performs on the target user's *next* login.
        var activeSessions = await db.UserSessions.Where(s => s.UserId == id && s.IsActive).ToListAsync();
        foreach (var session in activeSessions)
        {
            session.IsActive = false;
        }

        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<User?> CurrentUserAsync()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);
        return await db.Users.FindAsync(userId);
    }

    // Same shape as MarksController.InternalRoster's authorization check: the student must
    // be enrolled in a section the teacher holds at least one TeacherSectionAssignment for —
    // it doesn't matter which subject, since this gates a general performance view, not a
    // subject-specific one.
    private async Task<bool> IsTeachersOwnStudentAsync(Guid teacherId, Guid studentId)
    {
        var teacherSectionIds = await db.TeacherSectionAssignments
            .Where(a => a.TeacherId == teacherId)
            .Select(a => a.SectionId)
            .Distinct()
            .ToListAsync();
        if (teacherSectionIds.Count == 0)
        {
            return false;
        }

        return await db.SectionEnrollments
            .AnyAsync(e => e.StudentId == studentId && teacherSectionIds.Contains(e.SectionId));
    }
}
