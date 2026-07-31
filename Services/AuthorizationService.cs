using BackendApi.Data;
using Casbin;
using Casbin.Model;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Services;

// Single native authorization surface, replacing PermissionService.cs. Two engines behind one
// interface: Casbin (flat role -> permission-code RBAC, Part A) and a coded, Zanzibar-style
// relationship engine (Part B) modeled on OpenFGA's own Check(user, relation, object) shape and
// union-of-relations composition, but resolved directly against this app's own tables instead of
// a separate tuple store -- there is no second copy of "who teaches what"/"who owns this note" to
// keep in sync, so it cannot drift the way a real OpenFGA integration's dual-written tuples could.
public class AuthorizationService(AppDbContext db, ILogger<AuthorizationService> logger) : IAppAuthorizationService
{
    // ---- Part A: Casbin (flat RBAC) --------------------------------------------------------

    // Casbin's own rbac_with_deny_model.conf, with the obj dimension dropped: the permission
    // catalog is one flat code string per action, no separate resource/object to match against,
    // and no domain/department dimension -- RoleBinding.DepartmentId is used only by
    // GetDepartmentScopeAsync below, never by a permission check, so adding a "dom" column here
    // would be unused ceremony.
    private const string ModelConf = """
        [request_definition]
        r = sub, act

        [policy_definition]
        p = sub, act, eft

        [role_definition]
        g = _, _

        [policy_effect]
        e = some(where (p.eft == allow)) && !some(where (p.eft == deny))

        [matchers]
        m = g(r.sub, p.sub) && r.act == p.act
        """;

    public async Task<bool> HasPermissionAsync(Guid userId, string permissionCode)
    {
        var enforcer = await GetEnforcerAsync(userId);
        var allowed = await enforcer.EnforceAsync(userId.ToString(), permissionCode);
        if (!allowed)
        {
            logger.LogDebug("Permission {Code} denied for user {UserId}", permissionCode, userId);
        }
        return allowed;
    }

    public async Task<IReadOnlyDictionary<string, bool>> HasPermissionsAsync(Guid userId, params string[] permissionCodes)
    {
        var enforcer = await GetEnforcerAsync(userId); // one load, shared across every code below
        var results = new Dictionary<string, bool>(permissionCodes.Length);
        foreach (var code in permissionCodes)
        {
            results[code] = await enforcer.EnforceAsync(userId.ToString(), code);
        }
        return results;
    }

    // Same "most-recently-created, non-expired grant wins" resolution as the Casbin adapter's
    // explicit-grant lookup (LoadPoliciesAsync below), but scoped to one code and returning the
    // grant's own data instead of a bool — for status-display endpoints that need ExpiresAt.
    // Role-derived permissions have no ExpiresAt to report, so this only reflects explicit grants,
    // not the full HasPermissionAsync resolution (role-granted access reports Granted=false here).
    public async Task<(bool Granted, DateTime? ExpiresAt)> GetPermissionGrantStatusAsync(Guid userId, string permissionCode)
    {
        var now = DateTime.UtcNow;
        var activeGrant = await db.PermissionGrants
            .Where(g => g.UserId == userId && g.PermissionCode == permissionCode)
            .Where(g => g.ExpiresAt == null || g.ExpiresAt > now)
            .OrderByDescending(g => g.CreatedAt)
            .FirstOrDefaultAsync();

        return activeGrant is { Granted: true }
            ? (true, activeGrant.ExpiresAt)
            : (false, null);
    }

    // Built fresh on every call, deliberately not cached across calls (even within the same
    // request-scoped instance): AWA-13's own acceptance criterion is that revoking a grant
    // takes effect on the very next check with no cache to invalidate and no re-login required
    // (see RolesControllerTests.DeletePermissionGrant_RevokedOverrideStopsApplyingImmediately,
    // which exercises exactly this — two checks against the same service instance straddling a
    // revoke). HasPermissionsAsync still shares one build across its whole batch, since a batch
    // is one logical operation, not a sequence of independently-timed checks.
    private async Task<IEnforcer> GetEnforcerAsync(Guid userId)
    {
        // Not via a custom IReadOnlyAdapter (that path silently no-ops: IPolicyStore.AddPolicy
        // calls made during LoadPolicyAsync don't reach EnforceAsync's matcher, confirmed against
        // Casbin.NET 2.21.2 directly). Casbin's own AddPolicyAsync/AddGroupingPolicyAsync are the
        // verified-working way to populate an in-memory enforcer.
        var model = DefaultModel.CreateFromText(ModelConf);
        var enforcer = new Enforcer(model);
        await LoadPoliciesAsync(enforcer, userId);
        return enforcer;
    }

    // User-scoped (not a global load) -- mirrors PermissionService's own resolution exactly:
    // most-recently-created, non-expired explicit grant per permission code wins; role-derived
    // permissions come from every role this user is bound to.
    private async Task LoadPoliciesAsync(IEnforcer enforcer, Guid userId)
    {
        var now = DateTime.UtcNow;
        var sub = userId.ToString();

        var explicitByCode = await db.PermissionGrants
            .Where(g => g.UserId == userId && (g.ExpiresAt == null || g.ExpiresAt > now))
            .GroupBy(g => g.PermissionCode)
            .Select(grp => grp.OrderByDescending(g => g.CreatedAt).First())
            .ToListAsync();

        foreach (var grant in explicitByCode)
        {
            await enforcer.AddPolicyAsync(sub, grant.PermissionCode, grant.Granted ? "allow" : "deny");
        }

        var roleCodes = await db.RoleBindings
            .Where(b => b.UserId == userId)
            .Select(b => b.RoleCode)
            .Distinct()
            .ToListAsync();

        foreach (var roleCode in roleCodes)
        {
            await enforcer.AddGroupingPolicyAsync(sub, roleCode);
        }

        var rolePermissions = await db.Roles
            .Where(r => roleCodes.Contains(r.Code))
            .SelectMany(r => r.PermissionCodes.Select(p => new { RoleCode = r.Code, PermissionCode = p.Code }))
            .ToListAsync();

        foreach (var rp in rolePermissions)
        {
            await enforcer.AddPolicyAsync(rp.RoleCode, rp.PermissionCode, "allow");
        }
    }

    // ---- Part B: coded relationship engine -------------------------------------------------

    // Base (directly-assigned) relations. Object id is "type:id" conceptually; a relation whose
    // source table has a compound key (teacher_section_assignments is keyed by
    // (teacher_id, section_id, subject_id)) encodes that as a compound "a:b" string id, matching
    // OpenFGA's own object-id-as-string convention rather than collapsing subject-scoping away.
    private static readonly Dictionary<(string Type, string Relation), Func<AppDbContext, Guid, string, Task<bool>>> BaseRelations = new()
    {
        // Accepts either a bare section id ("does this teacher teach this section, any subject" —
        // TimetableController's SectionFeedback/GetTaughtSubjects checks) or a "sectionId:subjectId"
        // compound id ("does this teacher teach this exact subject in this section" —
        // MarksController.CreateInternal) — both are real, distinct questions this table answers.
        // Every predicate parses/validates `id` in plain C# BEFORE building the query — EF Core's
        // expression-tree compiler wraps any exception thrown *inside* a query lambda in its own
        // InvalidOperationException, which would hide ParseId's ArgumentException from callers.
        [("section", "teacher")] = (db, u, id) =>
        {
            var parts = id.Split(':');
            switch (parts.Length)
            {
                case 1:
                    var sectionOnly = ParseId(parts[0]);
                    return db.TeacherSectionAssignments.AnyAsync(a => a.TeacherId == u && a.SectionId == sectionOnly);
                case 2:
                    var sectionId = ParseId(parts[0]);
                    var subjectId = ParseId(parts[1]);
                    return db.TeacherSectionAssignments.AnyAsync(a =>
                        a.TeacherId == u && a.SectionId == sectionId && a.SubjectId == subjectId);
                default:
                    throw new ArgumentException($"Expected a section id or \"sectionId:subjectId\" compound id, got '{id}'.");
            }
        },
        [("section", "student")] = (db, u, id) =>
        {
            var sectionId = ParseId(id);
            return db.SectionEnrollments.AnyAsync(e => e.StudentId == u && e.SectionId == sectionId);
        },
        // Distinct from ("section","teacher") above: a TimetableSlot's teacher_id is seeded from
        // TeacherSectionAssignments at Generate() time but can be independently repointed later
        // (PatchSlot, e.g. a substitute teacher) without a matching TeacherSectionAssignments row.
        // Attendance/roster access is scoped to "whoever this specific slot is currently assigned
        // to," not "anyone generally assigned to teach the section+subject" -- using the section
        // relation here would silently change who can mark attendance after a slot is patched.
        [("slot", "teacher")] = (db, u, id) =>
        {
            var slotId = ParseId(id);
            return db.TimetableSlots.AnyAsync(s => s.Id == slotId && s.TeacherId == u);
        },
        [("student", "parent")] = (db, u, id) =>
        {
            var studentId = ParseId(id);
            return db.ParentWards.AnyAsync(w => w.ParentUserId == u && w.StudentId == studentId);
        },
        [("note", "owner")] = (db, u, id) =>
        {
            var noteId = ParseId(id);
            return db.Notes.AnyAsync(n => n.Id == noteId && n.OwnerId == u);
        },
        [("code_project", "owner")] = (db, u, id) =>
        {
            var projectId = ParseId(id);
            return db.CodeProjects.AnyAsync(p => p.Id == projectId && p.OwnerId == u);
        },
        [("group", "member")] = (db, u, id) =>
        {
            var groupId = ParseId(id);
            return db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == u);
        },
        [("group", "owner")] = (db, u, id) =>
        {
            var groupId = ParseId(id);
            return db.Groups.AnyAsync(g => g.Id == groupId && g.CreatedBy == u);
        },
        [("submission", "owner")] = (db, u, id) =>
        {
            var submissionId = ParseId(id);
            return db.Submissions.AnyAsync(s => s.Id == submissionId && s.StudentId == u);
        },
        // "Owns" here means "is the assignment/subject's teacher" — AssignmentsController's
        // AI-services gates (plagiarism, copy-check, autograde, grading) all check this against
        // an assignment (Assignments.TeacherId is copied from the subject's teacher at creation,
        // per AssignmentsController.Create), scoped to the assignment rather than the subject
        // since an assignment's teacher can differ from the subject's *current* teacher if the
        // subject's assignment changes after the assignment was created.
        [("assignment", "teacher")] = (db, u, id) =>
        {
            var assignmentId = ParseId(id);
            return db.Assignments.AnyAsync(a => a.Id == assignmentId && a.TeacherId == u);
        },
        // Used only at assignment-creation time (Subjects.TeacherId may be null if a subject
        // has no assigned teacher yet).
        [("subject", "teacher")] = (db, u, id) =>
        {
            var subjectId = ParseId(id);
            return db.Subjects.AnyAsync(s => s.Id == subjectId && s.TeacherId == u);
        },
        [("thread", "participant")] = (db, u, id) =>
        {
            var threadId = ParseId(id);
            return db.MessageThreads.AnyAsync(t => t.Id == threadId && (t.StudentId == u || t.TeacherId == u));
        },
        [("event", "owner")] = (db, u, id) =>
        {
            var eventId = ParseId(id);
            return db.Events.AnyAsync(e => e.Id == eventId && e.CreatedBy == u);
        },
    };

    // Derived (computed) relations -- a relation defined as "any of these other relations on the
    // same object hold," mirroring OpenFGA's union syntax (e.g. `define mark_attendance: teacher`)
    // directly and generically instead of one-off boolean composition in each controller.
    private static readonly Dictionary<(string Type, string Relation), string[]> DerivedRelations = new()
    {
        [("slot", "mark_attendance")] = ["teacher"],
        [("section", "add_internal_marks")] = ["teacher"],
        [("student", "view_records")] = ["parent"],
    };

    public Task<bool> CheckRelationAsync(Guid userId, string relation, string resourceType, string resourceId) =>
        CheckRelationWithLoggingAsync(userId, relation, resourceType, resourceId, depth: 0);

    public async Task<IReadOnlyDictionary<(string Relation, string ResourceType, string ResourceId), bool>> CheckRelationsAsync(
        Guid userId, params (string Relation, string ResourceType, string ResourceId)[] checks)
    {
        var results = new Dictionary<(string Relation, string ResourceType, string ResourceId), bool>(checks.Length);
        foreach (var c in checks)
        {
            results[(c.Relation, c.ResourceType, c.ResourceId)] =
                await CheckRelationWithLoggingAsync(userId, c.Relation, c.ResourceType, c.ResourceId, depth: 0);
        }
        return results;
    }

    // Shared try/catch + logging path for both public entry points above -- so a caller bug
    // (unknown relation, malformed id, misconfigured cycle) is always logged the same way.
    private async Task<bool> CheckRelationWithLoggingAsync(Guid userId, string relation, string resourceType, string resourceId, int depth)
    {
        try
        {
            return await ResolveRelationAsync(userId, relation, resourceType, resourceId, depth);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Relation check failed: {Relation} on {Type}:{Id}", relation, resourceType, resourceId);
            throw;
        }
    }

    // The one place BaseRelations/DerivedRelations get resolved -- recursion is capped at depth 5
    // so a misconfigured circular DerivedRelations entry fails loudly on first use instead of
    // overflowing the stack.
    private async Task<bool> ResolveRelationAsync(Guid userId, string relation, string resourceType, string resourceId, int depth)
    {
        if (depth > 5)
        {
            throw new InvalidOperationException(
                $"Relation resolution exceeded max depth (5) for '{relation}' on '{resourceType}' " +
                "-- likely a cyclic DerivedRelations entry.");
        }

        if (BaseRelations.TryGetValue((resourceType, relation), out var predicate))
        {
            return await predicate(db, userId, resourceId);
        }

        if (DerivedRelations.TryGetValue((resourceType, relation), out var underlying))
        {
            foreach (var r in underlying)
            {
                if (await ResolveRelationAsync(userId, r, resourceType, resourceId, depth + 1))
                {
                    return true;
                }
            }
            return false;
        }

        throw new ArgumentException($"No relation '{relation}' defined for resource type '{resourceType}'.");
    }

    private static Guid ParseId(string id) =>
        Guid.TryParse(id, out var g) ? g : throw new ArgumentException($"'{id}' is not a valid resource id.");

    // ---- Roles / department scope ----------------------------------------------------------

    public Task<bool> HasAnyRoleAsync(Guid userId, params string[] roleCodes) =>
        db.RoleBindings.AnyAsync(b => b.UserId == userId && roleCodes.Contains(b.RoleCode));

    // Not an authorization check -- a scope lookup, identical to today's PermissionService.
    public async Task<Guid?> GetDepartmentScopeAsync(Guid userId)
    {
        var hodBinding = await db.RoleBindings
            .Where(b => b.UserId == userId && b.RoleCode == "hod" && b.DepartmentId != null)
            .FirstOrDefaultAsync();

        return hodBinding?.DepartmentId;
    }
}
