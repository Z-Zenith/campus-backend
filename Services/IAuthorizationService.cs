namespace BackendApi.Services;

// Replaces IPermissionService. One surface for every permission/role/relationship question in
// the backend: HasPermissionAsync is Casbin-backed flat RBAC; CheckRelationAsync is a coded,
// Zanzibar-style relationship engine (base relations read live from their source tables, plus
// derived relations computed as a union of other relations); HasAnyRoleAsync/GetDepartmentScopeAsync
// are the smaller lookups that used to be duplicated ad hoc in individual controllers.
public interface IAppAuthorizationService
{
    Task<bool> HasPermissionAsync(Guid userId, string permissionCode);

    Task<bool> CheckRelationAsync(Guid userId, string relation, string resourceType, string resourceId);

    Task<bool> HasAnyRoleAsync(Guid userId, params string[] roleCodes);

    Task<Guid?> GetDepartmentScopeAsync(Guid userId);

    // Status-display endpoints (e.g. "can I still submit external marks, and until when") need
    // the grant's actual data, not just a yes/no decision — HasPermissionAsync can't supply
    // ExpiresAt, so this exposes the same "most-recently-created, non-expired grant" resolution
    // HasPermissionAsync uses internally, without duplicating that query in the controller.
    Task<(bool Granted, DateTime? ExpiresAt)> GetPermissionGrantStatusAsync(Guid userId, string permissionCode);

    // Batch forms: a page needing several permission flags or relation checks at once shares one
    // Casbin policy load / one set of calls instead of N separate round trips.
    Task<IReadOnlyDictionary<string, bool>> HasPermissionsAsync(Guid userId, params string[] permissionCodes);

    Task<IReadOnlyDictionary<(string Relation, string ResourceType, string ResourceId), bool>> CheckRelationsAsync(
        Guid userId, params (string Relation, string ResourceType, string ResourceId)[] checks);
}
