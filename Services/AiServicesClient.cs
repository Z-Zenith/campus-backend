using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BackendApi.Services;

public record RubricCriterion(string Name, IReadOnlyList<string> Keywords, double Weight);

public record AutogradeSuggestionResult(double SuggestedGrade, double MaxScore, double Confidence, IReadOnlyList<string> MatchedCriteria, IReadOnlyList<string> Feedback);

public record SimilarityMatchResult(string SubmissionAId, string SubmissionBId, double SimilarityScore);

public record TelemetryEventInput(string StudentId, string? ClassSessionId, string? AssignmentId, string EventType, object Metadata, DateTime RecordedAt);

public record SuspiciousFlagResult(string StudentId, string? ClassSessionId, string? AssignmentId, double ConfidenceScore, IReadOnlyList<string> Reasons);

public record BrowsingVisitInput(string Url, DateTime VisitedAt, int? DurationSeconds);

// AIS-06: extracted fields from a syllabus PDF, straight off campus-ai-services'
// POST /api/v1/syllabus-extraction response. Every field but confidence_notes is nullable
// because the extractor may not confidently find them; confidence_notes is always an array
// (possibly empty) explaining which fields it couldn't confidently find.
public record SyllabusExtractionResult(
    string? CourseCode, string? CourseName, string? InstructorName, double? Credits, IReadOnlyList<string> ConfidenceNotes);

// AIS-06: thrown when campus-ai-services rejects the PDF as malformed/undecodable (HTTP 400
// per its locked contract). Lets the controller respond with its own 400 rather than letting
// EnsureSuccessStatusCode's generic HttpRequestException bubble up as a 500.
public class SyllabusExtractionInvalidPdfException : Exception;

// AIS-01/03/04/06/07: thin HTTP client for the self-hosted AI Services container
// (services/ai-services — FastAPI, no external credentials). AIS-02/05 (Copyleaks/
// Pangram) are external-API-only and out of scope — no client credentials are
// available in this environment.
public interface IAiServicesClient
{
    Task<IReadOnlyList<SimilarityMatchResult>> CheckSimilarityAsync(
        IReadOnlyList<(string Id, string Content)> submissions, double threshold, CancellationToken ct = default);

    Task<AutogradeSuggestionResult> SuggestAutogradeAsync(
        string content, IReadOnlyList<RubricCriterion> rubric, double maxScore, CancellationToken ct = default);

    Task<IReadOnlyList<SuspiciousFlagResult>> CheckSuspiciousBehaviourAsync(
        IReadOnlyList<TelemetryEventInput> events, double minConfidence, CancellationToken ct = default);

    Task<string> SummarizeBrowsingAsync(IReadOnlyList<BrowsingVisitInput> visits, CancellationToken ct = default);

    // AIS-06: POSTs the raw PDF bytes (base64-encoded, per the locked contract) to
    // /api/v1/syllabus-extraction and returns the extracted fields. Throws
    // SyllabusExtractionInvalidPdfException when the PDF is malformed/undecodable (HTTP 400).
    Task<SyllabusExtractionResult> ExtractSyllabusAsync(byte[] pdfBytes, CancellationToken ct = default);
}

public class AiServicesClient(HttpClient http) : IAiServicesClient
{
    private sealed record SimilarityRequestBody(IReadOnlyList<SubmissionItemBody> Submissions, double Threshold);
    private sealed record SubmissionItemBody(string Id, string Content);
    private sealed record SimilarityResponseBody(List<SimilarityMatchResult> Matches);

    public async Task<IReadOnlyList<SimilarityMatchResult>> CheckSimilarityAsync(
        IReadOnlyList<(string Id, string Content)> submissions, double threshold, CancellationToken ct = default)
    {
        var body = new SimilarityRequestBody(
            submissions.Select(s => new SubmissionItemBody(s.Id, s.Content)).ToList(), threshold);
        var response = await http.PostAsJsonAsync("/api/v1/similarity", body, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SimilarityResponseBody>(cancellationToken: ct);
        return result?.Matches ?? [];
    }

    private sealed record AutogradeRequestBody(string Content, IReadOnlyList<RubricCriterionBody> Rubric, double MaxScore);
    private sealed record RubricCriterionBody(string Name, IReadOnlyList<string> Keywords, double Weight);

    public async Task<AutogradeSuggestionResult> SuggestAutogradeAsync(
        string content, IReadOnlyList<RubricCriterion> rubric, double maxScore, CancellationToken ct = default)
    {
        var body = new AutogradeRequestBody(
            content, rubric.Select(r => new RubricCriterionBody(r.Name, r.Keywords, r.Weight)).ToList(), maxScore);
        var response = await http.PostAsJsonAsync("/api/v1/autograde", body, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AutogradeSuggestionResult>(cancellationToken: ct)
            ?? throw new InvalidOperationException("AI Services returned an empty autograde response.");
    }

    private sealed record SuspiciousBehaviourRequestBody(IReadOnlyList<TelemetryEventInput> Events, double MinConfidence);
    private sealed record SuspiciousBehaviourResponseBody(List<SuspiciousFlagResult> Flags);

    public async Task<IReadOnlyList<SuspiciousFlagResult>> CheckSuspiciousBehaviourAsync(
        IReadOnlyList<TelemetryEventInput> events, double minConfidence, CancellationToken ct = default)
    {
        var body = new SuspiciousBehaviourRequestBody(events, minConfidence);
        var response = await http.PostAsJsonAsync("/api/v1/suspicious-behaviour", body, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SuspiciousBehaviourResponseBody>(cancellationToken: ct);
        return result?.Flags ?? [];
    }

    private sealed record BrowsingSummaryRequestBody(IReadOnlyList<BrowsingVisitInput> Visits);
    private sealed record BrowsingSummaryResponseBody(string Summary);

    public async Task<string> SummarizeBrowsingAsync(IReadOnlyList<BrowsingVisitInput> visits, CancellationToken ct = default)
    {
        var body = new BrowsingSummaryRequestBody(visits);
        var response = await http.PostAsJsonAsync("/api/v1/browsing-summary", body, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BrowsingSummaryResponseBody>(cancellationToken: ct);
        return result?.Summary ?? "";
    }

    // AIS-06's contract is locked to exactly these snake_case field names, so both request
    // and response bodies use explicit JsonPropertyName attributes rather than relying on
    // whatever naming policy the caller's JsonSerializerOptions happens to apply.
    private sealed record SyllabusExtractionRequestBody([property: JsonPropertyName("pdf_base64")] string PdfBase64);

    private sealed record SyllabusExtractionResponseBody(
        [property: JsonPropertyName("course_code")] string? CourseCode,
        [property: JsonPropertyName("course_name")] string? CourseName,
        [property: JsonPropertyName("instructor_name")] string? InstructorName,
        [property: JsonPropertyName("credits")] double? Credits,
        [property: JsonPropertyName("confidence_notes")] List<string>? ConfidenceNotes);

    public async Task<SyllabusExtractionResult> ExtractSyllabusAsync(byte[] pdfBytes, CancellationToken ct = default)
    {
        var body = new SyllabusExtractionRequestBody(Convert.ToBase64String(pdfBytes));
        var response = await http.PostAsJsonAsync("/api/v1/syllabus-extraction", body, ct);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new SyllabusExtractionInvalidPdfException();
        }
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SyllabusExtractionResponseBody>(cancellationToken: ct)
            ?? throw new InvalidOperationException("AI Services returned an empty syllabus extraction response.");
        return new SyllabusExtractionResult(
            result.CourseCode, result.CourseName, result.InstructorName, result.Credits, result.ConfidenceNotes ?? []);
    }
}
