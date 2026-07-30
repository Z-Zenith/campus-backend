namespace BackendApi.Services;

public interface IPermissionService
{
    Task<bool> HasPermissionAsync(Guid userId, string permissionCode);

    Task<Guid?> GetDepartmentScopeAsync(Guid userId);

    // class_teacher-style section-scoped oversight - mirrors GetDepartmentScopeAsync
    // exactly, but resolves the caller's Section-scoped role_binding instead. Kept as its
    // own method rather than folded into GetDepartmentScopeAsync, since every existing
    // caller of that method depends on its two-outcome (null | department Guid) contract.
    Task<Guid?> GetSectionOversightScopeAsync(Guid userId);
}
