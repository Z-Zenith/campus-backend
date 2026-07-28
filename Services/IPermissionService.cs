namespace BackendApi.Services;

public interface IPermissionService
{
    Task<bool> HasPermissionAsync(Guid userId, string permissionCode);

    Task<Guid?> GetDepartmentScopeAsync(Guid userId);

    // Flat capability check for nav/UI visibility decisions (GET /me/capabilities,
    // GET /users/search's gate) — deliberately not a full scoped-permission model, just
    // "does the user hold this code at all." Delegates to HasPermissionAsync per code so
    // there is exactly one place that resolves role-default-vs-PermissionGrant precedence.
    Task<List<string>> GetEffectivePermissionsAsync(Guid userId, IEnumerable<string> candidateCodes);
}
