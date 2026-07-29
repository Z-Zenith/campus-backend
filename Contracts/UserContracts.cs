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

// Bulk account creation via CSV upload — every row is validated and created with the
// exact same rules as a single Create call (password policy, department-college match),
// but a bad row skips that row rather than rolling back the whole file (a 500-row upload
// with 3 bad rows should create the other 497). CollegeId is never read from the file —
// same "never take college id as input from the caller" rule as the single-create form —
// every row is created in the caller's own college.
public record ImportUsersRowResult(int RowNumber, bool Success, string? Identifier, string? Error);

public record ImportUsersResponse(int TotalRows, int SuccessCount, int FailureCount, List<ImportUsersRowResult> Results);

// CsvHelper binds this per row; fields are plain strings (not AccountType/Guid) since a
// malformed value must produce a per-row error, not an unhandled parse exception during
// GetRecords<T>() that would fail the entire file.
public class ImportUserRow
{
    public string AccountType { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string InitialPassword { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? DepartmentId { get; set; }
}

public record ResetPasswordRequest(string NewPassword);

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
