using BackendApi.Data.Entities;

namespace BackendApi.Contracts;

public record CreateGroupRequest(string Name, GroupType Type, Guid? SectionId);

public record GroupDto(Guid Id, string Name, string Type, Guid? SectionId);

public record MyGroupsResponse(List<GroupDto> Groups);

public record CreatePostRequest(string Content);

public record GroupPostDto(Guid Id, Guid GroupId, Guid AuthorId, string Content, DateTime CreatedAt);

public record CreateMaterialRequest(string Title, string FileUrl, Guid? SubjectId, Guid? GroupId);

// Plain class, not a record: IFormFile-bearing multipart form bodies bind most reliably via
// [FromForm] onto settable properties rather than a record's primary-constructor shape.
public class UploadMaterialFileRequest
{
    public string Title { get; set; } = "";
    public Guid? SubjectId { get; set; }
    public Guid? GroupId { get; set; }
    public Microsoft.AspNetCore.Http.IFormFile? File { get; set; }
}

public record MaterialDto(Guid Id, string Title, string FileUrl, Guid? SubjectId, Guid? GroupId, Guid UploadedBy, DateTime UploadedAt);

// API-02
public record ProvisionClassGroupsResponse(int GroupsCreated, int MembershipsAdded);
