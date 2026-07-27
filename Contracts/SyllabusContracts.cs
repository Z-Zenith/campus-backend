namespace BackendApi.Contracts;

// AIS-06: extracted fields returned for the caller (Admin/Teacher) to review. This is
// extraction only — there is deliberately no "confirm and save" step here; no
// course-management UI exists yet in campus-admin-web/campus-teacher-web to attach one to.
public record SyllabusExtractionResponseDto(
    string? CourseCode, string? CourseName, string? InstructorName, double? Credits, IReadOnlyList<string> ConfidenceNotes);
