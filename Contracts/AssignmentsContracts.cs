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

public record SubmitAssignmentRequest(string ContentUrl, AssignmentType SubmissionFormat);

public record SubmissionDto(
    Guid Id,
    Guid AssignmentId,
    Guid StudentId,
    string ContentUrl,
    DateTime SubmittedAt,
    bool IsLate,
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

// AIS-05: AI-content detection via Pangram. Unlike AIS-02's async Copyleaks flow, Pangram's
// classifier call is synchronous — one request returns the persisted report directly, no
// separate "accepted"/pending status DTO needed.
public record AiDetectionReportDto(
    Guid Id,
    Guid SubmissionId,
    decimal AiLikelihoodScore,
    string? PangramReportId,
    DateTime CheckedAt);
