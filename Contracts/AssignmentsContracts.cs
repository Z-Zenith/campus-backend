using BackendApi.Data.Entities;

namespace BackendApi.Contracts;

public record CreateAssignmentRequest(
    Guid SubjectId,
    string Title,
    string? Description,
    AssignmentType Type,
    DateTime DueDate,
    DateTime SubmissionWindowStart,
    DateTime SubmissionWindowEnd,
    string? TypeSpecificSettings);

public record AssignmentDto(
    Guid Id,
    Guid SubjectId,
    string Title,
    string? Description,
    string Type,
    DateTime DueDate,
    DateTime SubmissionWindowStart,
    DateTime SubmissionWindowEnd,
    string? TypeSpecificSettings);

// Backs the assignment list page (PR 8's "grading in the assignment tab" IA) — one row per
// assignment the teacher owns, with enough summary data (subject name, submission count) to
// render a table without a follow-up request per row.
public record AssignmentSummaryDto(
    Guid Id,
    Guid SubjectId,
    string SubjectName,
    string Title,
    string Type,
    DateTime DueDate,
    int SubmissionCount);

public record SubmitAssignmentRequest(string ContentUrl, AssignmentType SubmissionFormat);

public record SubmissionDto(
    Guid Id,
    Guid AssignmentId,
    Guid StudentId,
    string ContentUrl,
    DateTime SubmittedAt,
    bool IsLate,
    bool IsAutosubmitted);

// Backs the Submissions tab — one row per student enrolled in a section this assignment's
// subject is taught to (same scoping as IsEnrolledInAssignmentSubjectAsync), cross-referenced
// against that student's Submission row if one exists. Status is "Missing" (no row),
// "Late" (row exists, past due date), or "Submitted".
public record AssignmentSubmissionStatusDto(
    Guid StudentId,
    string StudentName,
    string Status,
    Guid? SubmissionId,
    DateTime? SubmittedAt,
    bool IsAutosubmitted);

// AIS-03: cross-class copy-check among one assignment's submissions, via the
// self-hosted embedding-similarity model (services/ai-services).
public record CopyCheckMatchDto(Guid SubmissionAId, Guid SubmissionBId, decimal SimilarityScore);

// AIS-04: advisory autograde suggestion. MaxScoreUsed/Confidence/MatchedCriteria/Feedback
// come straight from the AI Services response and aren't persisted — only SuggestedGrade
// and the confirm bookkeeping live in autograde_suggestions.
public record RubricCriterionInput(string Name, List<string> Keywords, double Weight);

public record RequestAutogradeSuggestion(List<RubricCriterionInput> Rubric, double MaxScore);

public record AutogradeSuggestionDto(
    Guid Id,
    Guid SubmissionId,
    decimal SuggestedGrade,
    double MaxScoreUsed,
    double Confidence,
    IReadOnlyList<string> MatchedCriteria,
    IReadOnlyList<string> Feedback);

public record ConfirmGradeRequest(Guid SuggestionId);

public record ConfirmedGradeDto(Guid Id, Guid SubmissionId, decimal SuggestedGrade, bool ConfirmedByTeacher, DateTime? ConfirmedAt);

// AIS-02: internet plagiarism check via Copyleaks. Copyleaks scans asynchronously, so
// requesting a check only accepts the request — the score arrives later via
// WebhooksController.CopyleaksResult, hence the separate "status" DTOs for the pending
// state vs. the eventual persisted report.
public record PlagiarismCheckAcceptedDto(Guid SubmissionId, string ScanId, string Status);

public record PlagiarismReportStatusDto(Guid SubmissionId, string Status);

public record PlagiarismReportDto(
    Guid Id,
    Guid SubmissionId,
    decimal SimilarityScore,
    string? CopyleaksScanId,
    IReadOnlyList<string> MatchedSources,
    DateTime CheckedAt);
