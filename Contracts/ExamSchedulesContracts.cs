using BackendApi.Data.Entities;

namespace BackendApi.Contracts;

// Admin-facing exam scheduling (Academic Calendar work) - a scheduling record only
// (date/time/room), not a marks-recording one; internal_marks/external_marks already own
// the actual score once the exam happens. ExamType mirrors that same internal/external split.
public record ExamScheduleDto(
    Guid Id,
    Guid SectionId,
    Guid SubjectId,
    string SubjectCode,
    string SubjectName,
    ExamType ExamType,
    DateOnly ExamDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Room);

public record CreateExamScheduleRequest(
    Guid SubjectId,
    ExamType ExamType,
    DateOnly ExamDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Room);

public record UpdateExamScheduleRequest(
    DateOnly ExamDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string? Room);
