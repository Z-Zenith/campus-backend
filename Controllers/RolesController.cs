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
        if (request.ScopeType == ScopeKind.Department && request.DepartmentId is null)
        {
            return BadRequest("DepartmentId is required for a department-scoped binding.");
        }
        if (request.ScopeType == ScopeKind.Global && request.DepartmentId is not null)
        {
            return BadRequest("DepartmentId must be null for a global-scoped binding.");
        }

        var binding = new RoleBinding
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            RoleCode = request.RoleCode,
            ScopeType = request.ScopeType,
            DepartmentId = request.DepartmentId,
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

    // AWA-14: list departments in the caller's own college. No application-level way to see
    // existing departments existed before this — DepartmentsPage/SubjectsPage callers had to be
    // handed a raw department id with no way to look one up.
    [HttpGet("departments")]
    public async Task<ActionResult<List<DepartmentDto>>> ListDepartments()
    {
        var userId = CurrentUserId();
        if (!await permissions.HasPermissionAsync(userId, "manage_departments"))
        {
            return Forbid();
        }

        var callerCollegeId = await collegeScope.GetCollegeIdAsync(userId);
        var departments = await db.Departments
            .Include(d => d.HodRoleBinding!).ThenInclude(b => b.User)
            .Where(d => d.CollegeId == callerCollegeId)
            .OrderBy(d => d.Name)
            .ToListAsync();
        return Ok(departments.Select(d => ToDto(d, d.HodRoleBinding?.UserId, d.HodRoleBinding?.User.FullName)).ToList());
    }

    // AWA-14: rename a department. HoD reassignment already has its own endpoint (AssignHod
    // below) — this only ever touches Name, so it can't be used to move a department to a
    // different college or otherwise bypass the college-scope checks CreateDepartment enforces.
    [HttpPut("departments/{id}")]
    public async Task<ActionResult<DepartmentDto>> UpdateDepartment(Guid id, UpdateDepartmentRequest request)
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

        var department = await db.Departments.Include(d => d.HodRoleBinding!).ThenInclude(b => b.User).FirstOrDefaultAsync(d => d.Id == id);
        if (department is null)
        {
            return NotFound();
        }
        if (!await collegeScope.IsSameCollegeAsync(userId, department.CollegeId))
        {
            return Forbid();
        }

        department.Name = request.Name;
        await db.SaveChangesAsync();

        return Ok(ToDto(department, department.HodRoleBinding?.UserId, department.HodRoleBinding?.User.FullName));
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

        return Ok(ToDto(department, newBinding.UserId, candidate.FullName));
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub")!);

    private static DepartmentDto ToDto(Department d, Guid? hodUserId = null, string? hodUserFullName = null) =>
        new(d.Id, d.CollegeId, d.Name, d.HodRoleBindingId, hodUserId, hodUserFullName);

    private static RoleBindingDto ToDto(RoleBinding b) => new(
        b.Id, b.UserId, b.User.FullName, b.RoleCode, b.ScopeType, b.DepartmentId, b.GrantedAt);

    private static PermissionGrantDto ToDto(PermissionGrant g) => new(
        g.Id, g.UserId, g.User.FullName, g.PermissionCode, g.Granted, g.ExpiresAt, g.GrantedBy, g.CreatedAt);
}
