using BackendApi.Data;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Services;

// PermissionGrant is an explicit per-user override (used for TWA-19-style time-bound
// grants); role_default_permissions is the baseline bundle per architecture doc Section 9.
public class PermissionService(AppDbContext db) : IPermissionService
{
    public async Task<bool> HasPermissionAsync(Guid userId, string permissionCode)
    {
        var now = DateTime.UtcNow;

        var explicitGrant = await db.PermissionGrants
            .Where(g => g.UserId == userId && g.PermissionCode == permissionCode)
            .Where(g => g.ExpiresAt == null || g.ExpiresAt > now)
            .OrderByDescending(g => g.CreatedAt)
            .FirstOrDefaultAsync();
        if (explicitGrant is not null)
        {
            return explicitGrant.Granted;
        }

        var roleCodes = await db.RoleBindings
            .Where(b => b.UserId == userId)
            .Select(b => b.RoleCode)
            .ToListAsync();

        return await db.Roles
            .Where(r => roleCodes.Contains(r.Code))
            .SelectMany(r => r.PermissionCodes)
            .AnyAsync(p => p.Code == permissionCode);
    }

    public async Task<Guid?> GetDepartmentScopeAsync(Guid userId)
    {
        var hodBinding = await db.RoleBindings
            .Where(b => b.UserId == userId && b.RoleCode == "hod" && b.DepartmentId != null)
            .FirstOrDefaultAsync();

        return hodBinding?.DepartmentId;
    }

    public async Task<List<string>> GetEffectivePermissionsAsync(Guid userId, IEnumerable<string> candidateCodes)
    {
        var held = new List<string>();
        foreach (var code in candidateCodes)
        {
            if (await HasPermissionAsync(userId, code))
            {
                held.Add(code);
            }
        }
        return held;
    }
}
