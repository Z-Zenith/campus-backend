using BackendApi.Data.Entities;

namespace BackendApi.Contracts;

public record CreateUserRequest(
    Guid CollegeId,
    AccountType AccountType,
    string Identifier,
    string InitialPassword,
    string FullName,
    Guid? DepartmentId);

public record CreateUserResponse(Guid UserId, string TotpProvisioningUri, string TotpSecret);

public record ResetPasswordRequest(string NewPassword);

// GET /users/search — deliberately minimal (no account type, no department, no
// sensitive fields): just enough for an admin to pick the right person by name/
// identifier before an action like reset-password/assign-HoD/role-binding, which each
// re-check their own specific permission independently at the point of the write.
public record UserSearchResultDto(Guid Id, string FullName, string Identifier);

// AWA-07 — a teacher-submitted remark. TeacherName is resolved via the FK join
// regardless of whether that teacher is still active (see acceptance criterion:
// "record includes remarks... even if the submitting teacher is no longer active").
public record TeacherRemarkDto(Guid Id, Guid TeacherId, string TeacherName, string Content, DateTime SubmittedAt);

// AWA-07 — system-generated report rows (AIS-01 browsing summary, AIS-07 suspicious
// behaviour flag). Both are already-populated tables; this surfaces them, it doesn't
// generate them.
public record BrowsingSummaryReportDto(Guid Id, string SummaryText, DateTime GeneratedAt);

public record SuspiciousFlagReportDto(
    Guid Id,
    decimal ConfidenceScore,
    DateTime FlaggedAt,
    Guid? AssignmentId,
    Guid? ClassSessionId);

// AWA-08: marks sections appended to the AWA-07 DTO. Reusing the same InternalMarkDto /
// ExternalMarkDto shapes that SDA-15's MyMarksResponse uses (and that PRT-02's
// WardRecordResponse uses) is the cleanest way to satisfy the "data matches what the
// student sees in SDA-15, not a separate copy" acceptance criterion — one record shape
// on the server, one query path, the Admin view and the student view cannot drift.
public record StudentRecordDto(
    Guid Id,
    string FullName,
    string Identifier,
    string AccountType,
    Guid CollegeId,
    Guid? DepartmentId,
    bool IsActive,
    List<TeacherRemarkDto> Remarks,
    List<BrowsingSummaryReportDto> BrowsingSummaries,
    List<SuspiciousFlagReportDto> SuspiciousFlags,
    List<InternalMarkDto> InternalMarks,
    List<ExternalMarkDto> ExternalMarks);
