using System.Net.Http.Json;
using System.Text.Json;

namespace BackendApi.Services;

public record RubricCriterion(string Name, IReadOnlyList<string> Keywords, double Weight);

public record AutogradeSuggestionResult(double SuggestedGrade, double MaxScore, double Confidence, IReadOnlyList<string> MatchedCriteria, IReadOnlyList<string> Feedback);

public record SimilarityMatchResult(string SubmissionAId, string SubmissionBId, double SimilarityScore);

public record TelemetryEventInput(string StudentId, string? ClassSessionId, string? AssignmentId, string EventType, object Metadata, DateTime RecordedAt);

public record SuspiciousFlagResult(string StudentId, string? ClassSessionId, string? AssignmentId, double ConfidenceScore, IReadOnlyList<string> Reasons);

public record BrowsingVisitInput(string Url, DateTime VisitedAt, int? DurationSeconds);

// AIS-01/03/04/07: thin HTTP client for the self-hosted AI Services container
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
}

public class AiServicesClient(HttpClient http) : IAiServicesClient
{
    // The ai-services container (Python/FastAPI) serializes and expects snake_case field
    // names verbatim from its Pydantic schemas (similarity_score, student_id, recorded_at,
    // max_score, matched_criteria, submission_a_id, event_type, confidence_score, …) with
    // no alias generator. The default System.Text.Json web options (camelCase out,
    // case-insensitive in) cannot bridge the underscore, so every call either 422'd
    // (/suspicious-behaviour, /browsing-summary) or deserialized to 0/null while silently
    // dropping request fields like max_score (/similarity, /autograde). SnakeCaseLower is
    // the single casing source of truth for this client; it must be passed EXPLICITLY to
    // every PostAsJsonAsync/ReadFromJsonAsync — the parameterless overloads ignore it. C#
    // records stay PascalCase so the same public result types can still be returned to
    // frontends in camelCase by MVC; the policy is scoped to this client only.
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record SimilarityRequestBody(IReadOnlyList<SubmissionItemBody> Submissions, double Threshold);
    private sealed record SubmissionItemBody(string Id, string Content);
    private sealed record SimilarityResponseBody(List<SimilarityMatchResult> Matches);

    public async Task<IReadOnlyList<SimilarityMatchResult>> CheckSimilarityAsync(
        IReadOnlyList<(string Id, string Content)> submissions, double threshold, CancellationToken ct = default)
    {
        var body = new SimilarityRequestBody(
            submissions.Select(s => new SubmissionItemBody(s.Id, s.Content)).ToList(), threshold);
        var response = await http.PostAsJsonAsync("/api/v1/similarity", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SimilarityResponseBody>(JsonOptions, ct);
        return result?.Matches ?? [];
    }

    private sealed record AutogradeRequestBody(string Content, IReadOnlyList<RubricCriterionBody> Rubric, double MaxScore);
    private sealed record RubricCriterionBody(string Name, IReadOnlyList<string> Keywords, double Weight);

    public async Task<AutogradeSuggestionResult> SuggestAutogradeAsync(
        string content, IReadOnlyList<RubricCriterion> rubric, double maxScore, CancellationToken ct = default)
    {
        var body = new AutogradeRequestBody(
            content, rubric.Select(r => new RubricCriterionBody(r.Name, r.Keywords, r.Weight)).ToList(), maxScore);
        var response = await http.PostAsJsonAsync("/api/v1/autograde", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AutogradeSuggestionResult>(JsonOptions, ct)
            ?? throw new InvalidOperationException("AI Services returned an empty autograde response.");
    }

    private sealed record SuspiciousBehaviourRequestBody(IReadOnlyList<TelemetryEventInput> Events, double MinConfidence);
    private sealed record SuspiciousBehaviourResponseBody(List<SuspiciousFlagResult> Flags);

    public async Task<IReadOnlyList<SuspiciousFlagResult>> CheckSuspiciousBehaviourAsync(
        IReadOnlyList<TelemetryEventInput> events, double minConfidence, CancellationToken ct = default)
    {
        var body = new SuspiciousBehaviourRequestBody(events, minConfidence);
        var response = await http.PostAsJsonAsync("/api/v1/suspicious-behaviour", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SuspiciousBehaviourResponseBody>(JsonOptions, ct);
        return result?.Flags ?? [];
    }

    private sealed record BrowsingSummaryRequestBody(IReadOnlyList<BrowsingVisitInput> Visits);
    private sealed record BrowsingSummaryResponseBody(string Summary);

    public async Task<string> SummarizeBrowsingAsync(IReadOnlyList<BrowsingVisitInput> visits, CancellationToken ct = default)
    {
        var body = new BrowsingSummaryRequestBody(visits);
        var response = await http.PostAsJsonAsync("/api/v1/browsing-summary", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BrowsingSummaryResponseBody>(JsonOptions, ct);
        return result?.Summary ?? "";
    }
}
