namespace BackendApi.Contracts;

public record GenerateTimetableRequest(Guid? DepartmentId);

// Admin CRUD for teacher_section_assignments - the per-semester "teacher X teaches subject
// Y in section Z" rotation. Previously had no production endpoint at all (only ever
// seeded directly in tests) - this is the missing counterpart to the stable
// subject_teachers roster (see SubjectsContracts.cs's SubjectTeacherDto): the roster
// decides who's fixed to teach a subject; this assigns one of those roster teachers to an
// actual section for the current rotation.
public record TeacherSectionAssignmentDto(
    Guid Id, Guid SectionId, Guid SubjectId, string SubjectCode, string SubjectName, Guid TeacherId, string TeacherName);

public record CreateTeacherSectionAssignmentRequest(Guid SubjectId, Guid TeacherId);

public record TimetableSlotDto(
    Guid Id,
    int DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    Guid SectionId,
    string SectionName,
    Guid SubjectId,
    string SubjectName,
    Guid TeacherId,
    string TeacherName,
    string? Room,
    bool ManuallyEdited);

public record PatchSlotRequest(Guid? TeacherId, int? DayOfWeek, TimeOnly? StartTime, TimeOnly? EndTime, string? Room);

public record CreateChangeRequestRequest(string Description);

public record ChangeRequestDto(Guid Id, string Description, string Status, DateTime RequestedAt);

// TWA-08
public record RosterStudentDto(Guid StudentId, string FullName);

public record AttendanceEntryRequest(Guid StudentId, string Status);

// SessionDate defaults to today (server time) when omitted — covers the common "mark
// today's session" case. The (TimetableSlotId, SessionDate) pair identifies the session;
// the underlying ClassSession row is created on first mark if it doesn't exist yet.
public record MarkAttendanceRequest(Guid TimetableSlotId, DateOnly? SessionDate, List<AttendanceEntryRequest> Entries);

public record MarkedAttendanceDto(Guid StudentId, string StudentName, string Status);

public record MarkAttendanceResponse(Guid ClassSessionId, DateOnly SessionDate, Guid SectionId, List<MarkedAttendanceDto> Records);

// TWA-12
public record SubmitSectionFeedbackRequest(int Rating, string? Comments);

public record SectionFeedbackDto(Guid Id, Guid SectionId, string SectionName, int Rating, string? Comments, DateTime SubmittedAt);

// TWA-04
public record StudentAttendanceDto(Guid StudentId, string StudentName, decimal? AttendancePercentage);

public record SubjectMarksSummaryDto(Guid SubjectId, string SubjectName, decimal? AverageMarks, int StudentsGraded);

public record SectionPerformanceSummaryDto(
    Guid SectionId,
    string SectionName,
    decimal? OverallAttendancePercentage,
    List<StudentAttendanceDto> StudentAttendance,
    List<SubjectMarksSummaryDto> MarksBySubject);

// TWA-09
public record AttendanceAlertDto(Guid StudentId, string StudentName, Guid SectionId, string SectionName, decimal AttendancePercentage);

// SDA-12
public record ExitPingResponse(bool Notified, Guid? ClassSessionId, Guid? NotifiedTeacherId);
