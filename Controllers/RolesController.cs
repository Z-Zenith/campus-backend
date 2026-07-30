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
[Route("api/v1")]
[Authorize]
public class RolesController(AppDbContext db, IPermissionService permissions, ICollegeScopeService collegeScope) : ControllerBase
{
    // AWA-13 — #127: scoped to the caller's own college. Without this, any holder of
    // manage_roles_and_permissions (a college-scoped role in intent, per architecture doc
    // Section 9) could enumerate every role binding on the platform, across every college.
    [HttpGet("role-bindings")]
    public async Task<ActionResult<List<RoleBindingDto>>> ListRoleBindings()
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_roles_and_permissions"))
        {
            return Forbid();
        }

        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
        var bindings = await db.RoleBindings
            .Include(b => b.User)
            .Where(b => b.User.CollegeId == callerCollegeId)
            .OrderByDescending(b => b.GrantedAt)
            .ToListAsync();
        return Ok(bindings.Select(ToDto).ToList());
    }

    // AWA-13 — #127: the target user must belong to the caller's own college, otherwise a
    // College-A admin could grant admin/manage_accounts/etc. to a College-B account (full
    // cross-tenant takeover).
    [HttpPost("role-bindings")]
    public async Task<ActionResult<RoleBindingDto>> CreateRoleBinding(CreateRoleBindingRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_roles_and_permissions"))
        {
            return Forbid();
        }

        var user = await db.Users.FindAsync(request.UserId);
        if (user is null)
        {
            return NotFound("User not found.");
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, user.CollegeId))
        {
            return Forbid();
        }
        if (!await db.Roles.AnyAsync(r => r.Code == request.RoleCode))
        {
            return BadRequest("Unknown role code.");
        }
        if (request.ScopeType == ScopeKind.Department && (request.DepartmentId is null || request.SectionId is not null))
        {
            return BadRequest("DepartmentId (and only DepartmentId) is required for a department-scoped binding.");
        }
        if (request.ScopeType == ScopeKind.Global && (request.DepartmentId is not null || request.SectionId is not null))
        {
            return BadRequest("DepartmentId and SectionId must both be null for a global-scoped binding.");
        }
        if (request.ScopeType == ScopeKind.Section && (request.SectionId is null || request.DepartmentId is not null))
        {
            return BadRequest("SectionId (and only SectionId) is required for a section-scoped binding.");
        }

        Guid? sectionCollegeId = null;
        if (request.SectionId is { } sectionId)
        {
            sectionCollegeId = await db.Sections
                .Where(s => s.Id == sectionId)
                .Select(s => (Guid?)s.Department.CollegeId)
                .FirstOrDefaultAsync();
            if (sectionCollegeId is null)
            {
                return BadRequest("Unknown section.");
            }
            if (!await collegeScope.IsSameCollegeAsync(userId, sectionCollegeId.Value))
            {
                return Forbid();
            }

            // class_teacher: at most 2 active bindings per section (checked at this
            // granularity - RoleCode/ScopeType, not just SectionId - so a future
            // section-scoped role with a different cap doesn't inherit this one).
            if (request.RoleCode == "class_teacher")
            {
                var existingClassTeachers = await db.RoleBindings
                    .CountAsync(b => b.SectionId == sectionId && b.RoleCode == "class_teacher");
                if (existingClassTeachers >= 2)
                {
                    return Conflict(new { error = "class_teacher_limit_reached", message = "This section already has 2 class teachers assigned." });
                }
            }
        }

        var binding = new RoleBinding
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            RoleCode = request.RoleCode,
            ScopeType = request.ScopeType,
            DepartmentId = request.DepartmentId,
            SectionId = request.SectionId,
            GrantedAt = DateTime.UtcNow,
        };
        db.RoleBindings.Add(binding);
        await db.SaveChangesAsync();

        binding.User = user;
        return Ok(ToDto(binding));
    }

    // AWA-13 — #127
    [HttpGet("permission-grants")]
    public async Task<ActionResult<List<PermissionGrantDto>>> ListPermissionGrants()
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_roles_and_permissions"))
        {
            return Forbid();
        }

        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
        var grants = await db.PermissionGrants
            .Include(g => g.User)
            .Where(g => g.User.CollegeId == callerCollegeId)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync();
        return Ok(grants.Select(ToDto).ToList());
    }

    // AWA-13 — #127
    [HttpPost("permission-grants")]
    public async Task<ActionResult<PermissionGrantDto>> CreatePermissionGrant(CreatePermissionGrantRequest request)
    {
        var currentUserId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(currentUserId, "manage_roles_and_permissions"))
        {
            return Forbid();
        }

        var user = await db.Users.FindAsync(request.UserId);
        if (user is null)
        {
            return NotFound("User not found.");
        }
        if (!await collegeScope.IsSameCollegeAsync(currentUserId, user.CollegeId))
        {
            return Forbid();
        }
        if (!await db.Permissions.AnyAsync(p => p.Code == request.PermissionCode))
        {
            return BadRequest("Unknown permission code.");
        }
        if (request.ExpiresAt is { } expiresAt && expiresAt <= DateTime.UtcNow)
        {
            return BadRequest("ExpiresAt must be in the future.");
        }

        var grant = new PermissionGrant
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            PermissionCode = request.PermissionCode,
            Granted = request.Granted,
            ExpiresAt = request.ExpiresAt,
            GrantedBy = currentUserId,
            CreatedAt = DateTime.UtcNow,
        };
        db.PermissionGrants.Add(grant);
        await db.SaveChangesAsync();

        grant.User = user;
        return Ok(ToDto(grant));
    }

    // AWA-13 — deleting the override row is the revoke: PermissionService (IPermissionService)
    // reads permission_grants live on every check, so removal takes effect on the caller's very
    // next request without needing to re-login, satisfying the AC.
    [HttpDelete("permission-grants/{id}")]
    public async Task<IActionResult> DeletePermissionGrant(Guid id)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_roles_and_permissions"))
        {
            return Forbid();
        }

        var grant = await db.PermissionGrants.Include(g => g.User).FirstOrDefaultAsync(g => g.Id == id);
        if (grant is null)
        {
            return NotFound();
        }
        // #127: without this, any manage_roles_and_permissions holder could revoke another
        // college's grants by id.
        if (!await collegeScope.IsSameCollegeAsync(userId, grant.User.CollegeId))
        {
            return Forbid();
        }

        db.PermissionGrants.Remove(grant);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // AWA-14: create a department. Gated to whoever holds manage_departments
    // (Admin by default; IT can be granted it via a PermissionGrant per Section 9).
    [HttpPost("departments")]
    public async Task<ActionResult<DepartmentDto>> CreateDepartment(CreateDepartmentRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required.");
        }

        var collegeExists = await db.Colleges.AnyAsync(c => c.Id == request.CollegeId);
        if (!collegeExists)
        {
            return BadRequest("Unknown college.");
        }
        // #126/#127-class check: a manage_departments holder must not be able to create
        // departments inside another college.
        if (!await collegeScope.IsSameCollegeAsync(userId, request.CollegeId))
        {
            return Forbid();
        }

        var department = new Department
        {
            Id = Guid.NewGuid(),
            CollegeId = request.CollegeId,
            Name = request.Name,
        };
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(CreateDepartment), new { id = department.Id }, ToDto(department));
    }

    // AWA-14: assign (or reassign) the Head of Department. A department has at most one
    // active HoD binding at a time — departments.hod_role_binding_id is the single pointer
    // to the active binding, so reassigning must both point it at a freshly-created binding
    // and remove the previous binding row in the same transaction. Removing it (rather than
    // just repointing the FK) matters because PermissionService/GetDepartmentScopeAsync read
    // role_bindings directly — a stale row left behind would keep granting the old HoD's
    // department-scoped permissions even after they were replaced.
    [HttpPost("departments/{id}/hod")]
    public async Task<ActionResult<DepartmentDto>> AssignHod(Guid id, AssignHodRequest request)
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var department = await db.Departments.FindAsync(id);
        if (department is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, department.CollegeId))
        {
            return Forbid();
        }

        var candidate = await db.Users.FindAsync(request.UserId);
        if (candidate is null)
        {
            return BadRequest("Unknown user.");
        }
        if (candidate.CollegeId != department.CollegeId)
        {
            return BadRequest("The user must belong to the department's college.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();

        var previousBindingId = department.HodRoleBindingId;

        var newBinding = new RoleBinding
        {
            Id = Guid.NewGuid(),
            UserId = candidate.Id,
            RoleCode = "hod",
            ScopeType = ScopeKind.Department,
            DepartmentId = department.Id,
            GrantedAt = DateTime.UtcNow,
        };
        db.RoleBindings.Add(newBinding);
        await db.SaveChangesAsync();

        department.HodRoleBindingId = newBinding.Id;
        await db.SaveChangesAsync();

        if (previousBindingId is { } oldId && oldId != newBinding.Id)
        {
            var previousBinding = await db.RoleBindings.FindAsync(oldId);
            if (previousBinding is not null)
            {
                db.RoleBindings.Remove(previousBinding);
                await db.SaveChangesAsync();
            }
        }

        await transaction.CommitAsync();

        return Ok(ToDto(department, newBinding.UserId));
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static DepartmentDto ToDto(Department d, Guid? hodUserId = null) =>
        new(d.Id, d.CollegeId, d.Name, d.HodRoleBindingId, hodUserId);

    private static RoleBindingDto ToDto(RoleBinding b) => new(
        b.Id, b.UserId, b.User.FullName, b.RoleCode, b.ScopeType, b.DepartmentId, b.SectionId, b.GrantedAt);

    private static PermissionGrantDto ToDto(PermissionGrant g) => new(
        g.Id, g.UserId, g.User.FullName, g.PermissionCode, g.Granted, g.ExpiresAt, g.GrantedBy, g.CreatedAt);
}
