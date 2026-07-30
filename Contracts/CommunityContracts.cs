namespace BackendApi.Contracts;

// Clubs: opt-in student orgs, led by a faculty lead (teacher) + a student incharge
// (officer), alongside regular members. See db/init/01_schema.sql's comment on `clubs` for
// why leadership is a direct FK pair rather than routed through RoleBinding.
public record CreateClubRequest(string Name, string? Description, Guid? FacultyLeadUserId, Guid? StudentInchargeUserId);

public record UpdateClubRequest(string Name, string? Description, Guid? FacultyLeadUserId, Guid? StudentInchargeUserId);

// HomeSiteHtml is club-authored HTML/CSS/JS - callers MUST render it inside a sandboxed
// iframe (no allow-same-origin, strict CSP) and never inline it into the app's own DOM.
public record ClubDto(
    Guid Id,
    string Name,
    string? Description,
    Guid? FacultyLeadUserId,
    string? FacultyLeadFullName,
    Guid? StudentInchargeUserId,
    string? StudentInchargeFullName,
    string? HomeSiteHtml,
    int MemberCount);

public record UpdateClubHomeSiteRequest(string? HomeSiteHtml);

public record ClubMemberDto(Guid Id, Guid UserId, string UserFullName, DateTime JoinedAt);

public record AddClubMemberRequest(Guid UserId);

public record CreateClubPostRequest(string Content);

public record ClubPostDto(Guid Id, Guid ClubId, Guid AuthorId, string Content, DateTime CreatedAt);

// Classroom discussions: one per (Section, Subject) - auto-provisioned, no direct-create
// endpoint (mirrors the old Class group's "not through this endpoint" precedent).
public record ClassroomDiscussionDto(Guid Id, Guid SectionId, string SectionName, Guid SubjectId, string SubjectCode, string SubjectName);

public record ProvisionClassroomDiscussionsResponse(int DiscussionsCreated);

public record CreateClassroomDiscussionPostRequest(string Content);

public record ClassroomDiscussionPostDto(Guid Id, Guid ClassroomDiscussionId, Guid AuthorId, string Content, DateTime CreatedAt);

// Staff groups: teacher-only spaces - all that's left of the old flat "groups" concept.
public record CreateStaffGroupRequest(string Name);

public record StaffGroupDto(Guid Id, string Name);

public record MyStaffGroupsResponse(List<StaffGroupDto> StaffGroups);

public record StaffGroupMemberDto(Guid Id, Guid UserId, string UserFullName, DateTime JoinedAt);

public record AddStaffGroupMemberRequest(Guid UserId);

public record CreateStaffGroupPostRequest(string Content);

public record StaffGroupPostDto(Guid Id, Guid StaffGroupId, Guid AuthorId, string Content, DateTime CreatedAt);

// Materials attach to a subject and/or exactly one community space (club / classroom
// discussion / staff group).
public record CreateMaterialRequest(
    string Title, string FileUrl, Guid? SubjectId, Guid? ClubId, Guid? ClassroomDiscussionId, Guid? StaffGroupId);

public record MaterialDto(
    Guid Id,
    string Title,
    string FileUrl,
    Guid? SubjectId,
    Guid? ClubId,
    Guid? ClassroomDiscussionId,
    Guid? StaffGroupId,
    Guid UploadedBy,
    DateTime UploadedAt);
