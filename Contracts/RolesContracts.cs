using BackendApi.Data.Entities;

namespace BackendApi.Contracts;

// AWA-14
public record CreateDepartmentRequest(Guid CollegeId, string Name);

public record DepartmentDto(Guid Id, Guid CollegeId, string Name, Guid? HodRoleBindingId, Guid? HodUserId);

public record AssignHodRequest(Guid UserId);

// SectionId is populated for Section-scoped bindings (e.g. class_teacher) - mirrors
// DepartmentId's existing nullable-pair pattern, see role_bindings' 3-way CHECK constraint.
public record CreateRoleBindingRequest(Guid UserId, string RoleCode, ScopeKind ScopeType, Guid? DepartmentId, Guid? SectionId = null);

public record RoleBindingDto(
    Guid Id,
    Guid UserId,
    string UserFullName,
    string RoleCode,
    ScopeKind ScopeType,
    Guid? DepartmentId,
    Guid? SectionId,
    DateTime GrantedAt);

public record CreatePermissionGrantRequest(Guid UserId, string PermissionCode, bool Granted, DateTime? ExpiresAt);

public record PermissionGrantDto(
    Guid Id,
    Guid UserId,
    string UserFullName,
    string PermissionCode,
    bool Granted,
    DateTime? ExpiresAt,
    Guid GrantedBy,
    DateTime CreatedAt);
