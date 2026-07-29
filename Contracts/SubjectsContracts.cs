namespace BackendApi.Contracts;

// SDA-18. TeacherId/TeacherName are guaranteed present (not nullable) — every row here
// comes from a TeacherSectionAssignment, which by definition always names a teacher, so
// "every enrolled subject has a non-empty ... teacher-info entry" holds by construction.
public record MySubjectDto(Guid SubjectId, string SubjectCode, string SubjectName, Guid TeacherId, string TeacherName);

// Admin-facing subject management (no feature ID — genuinely unscoped before this; there
// was no application-level way to create/edit/delete a subject at all, only Mine() above).
public record SubjectDto(
    Guid Id,
    Guid DepartmentId,
    string DepartmentName,
    string Code,
    string Name,
    Guid? TeacherId,
    string? TeacherName);

public record CreateSubjectRequest(Guid DepartmentId, string Code, string Name, Guid? TeacherId);

public record UpdateSubjectRequest(string Code, string Name, Guid? TeacherId);
