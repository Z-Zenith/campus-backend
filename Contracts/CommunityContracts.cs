using BackendApi.Data.Entities;

namespace BackendApi.Contracts;

public record CreateGroupRequest(string Name, GroupType Type, Guid? SectionId);

public record GroupDto(Guid Id, string Name, string Type, Guid? SectionId);

public record MyGroupsResponse(List<GroupDto> Groups);

public record CreatePostRequest(string Content);

public record GroupPostDto(Guid Id, Guid GroupId, Guid AuthorId, string Content, DateTime CreatedAt);

public record CreateMaterialRequest(string Title, string FileUrl, Guid? SubjectId, Guid? GroupId);

public record MaterialDto(Guid Id, string Title, string FileUrl, Guid? SubjectId, Guid? GroupId, Guid UploadedBy, DateTime UploadedAt);

// API-02
public record ProvisionClassGroupsResponse(int GroupsCreated, int MembershipsAdded);

// Phase 6 - group membership management. Genuinely unscoped before this: AWA-06/AWA-12
// covered create + read-only list, but nothing let a caller see, add to, or remove from a
// group's membership after creation.
public record GroupMemberDto(Guid Id, Guid UserId, string UserFullName, DateTime JoinedAt);

public record AddGroupMemberRequest(Guid UserId);
