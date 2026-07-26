using System.Text.Json;
using BackendApi.Services;

namespace BackendApi.Tests.Services;

// AIS-01/03/05 — regression guard for the snake_case JSON contract break between the C#
// AiServicesClient and the Python/FastAPI ai-services container. Before the fix the client
// used default web options (camelCase out, case-insensitive in), which cannot bridge the
// underscore in the service's snake_case Pydantic fields: /suspicious-behaviour and
// /browsing-summary 422'd, while /similarity and /autograde returned 200 but deserialized
// to 0/null and silently dropped max_score. These tests pin the exact wire field names by
// serializing the real request record types with AiServicesClient.JsonOptions, and by
// deserializing each response record FROM a hardcoded snake_case JSON sample (the literal
// bytes ai-services sends). They intentionally assert literal field names / populated
// values rather than a self-consistent round-trip, which would pass under the old buggy
// options too.
public class AiServicesJsonContractTests
{
    private static readonly JsonSerializerOptions Options = AiServicesClient.JsonOptions;

    [Fact]
    public void SnakeCaseFix_TelemetryEventInput_SerializesToSnakeCase()
    {
        var evt = new TelemetryEventInput(
            StudentId: "s-1",
            ClassSessionId: "cs-1",
            AssignmentId: "a-1",
            EventType: "paste",
            Metadata: new Dictionary<string, object> { ["char_count"] = 200 },
            RecordedAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var json = JsonSerializer.Serialize(evt, Options);

        Assert.Contains("\"student_id\"", json);
        Assert.Contains("\"class_session_id\"", json);
        Assert.Contains("\"assignment_id\"", json);
        Assert.Contains("\"event_type\"", json);
        Assert.Contains("\"recorded_at\"", json);
        // The old default web options would have emitted "studentId"/"eventType"/etc.
        Assert.DoesNotContain("\"studentId\"", json);
        Assert.DoesNotContain("\"eventType\"", json);
        Assert.DoesNotContain("\"recordedAt\"", json);
    }

    [Fact]
    public void SnakeCaseFix_BrowsingVisitInput_SerializesToSnakeCase()
    {
        var visit = new BrowsingVisitInput(
            Url: "https://example.edu",
            VisitedAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            DurationSeconds: 42);

        var json = JsonSerializer.Serialize(visit, Options);

        Assert.Contains("\"url\"", json);
        Assert.Contains("\"visited_at\"", json);
        Assert.Contains("\"duration_seconds\"", json);
        Assert.DoesNotContain("\"visitedAt\"", json);
        Assert.DoesNotContain("\"durationSeconds\"", json);
    }

    [Fact]
    public void SnakeCaseFix_RubricCriterion_SerializesToSnakeCase()
    {
        var criterion = new RubricCriterion(Name: "clarity", Keywords: ["clear", "concise"], Weight: 0.5);

        var json = JsonSerializer.Serialize(criterion, Options);

        Assert.Contains("\"name\"", json);
        Assert.Contains("\"keywords\"", json);
        Assert.Contains("\"weight\"", json);
    }

    // The autograde REQUEST record (AutogradeRequestBody) is private to AiServicesClient, but
    // it is serialized with the very same AiServicesClient.JsonOptions and carries a MaxScore
    // property — the field the task flags as silently dropped. AutogradeSuggestionResult also
    // exposes MaxScore, so serializing it under the shared options proves the policy emits
    // "max_score" (not "maxScore") for that property.
    [Fact]
    public void SnakeCaseFix_MaxScore_SerializesToSnakeCase()
    {
        var result = new AutogradeSuggestionResult(
            SuggestedGrade: 8, MaxScore: 10, Confidence: 0.9, MatchedCriteria: ["clarity"], Feedback: ["good"]);

        var json = JsonSerializer.Serialize(result, Options);

        Assert.Contains("\"max_score\"", json);
        Assert.Contains("\"suggested_grade\"", json);
        Assert.Contains("\"matched_criteria\"", json);
        Assert.Contains("\"confidence\"", json);
        Assert.DoesNotContain("\"maxScore\"", json);
        Assert.DoesNotContain("\"suggestedGrade\"", json);
    }

    [Fact]
    public void SnakeCaseFix_SimilarityMatchResult_DeserializesFromSnakeCase()
    {
        // Exact bytes ai-services (/similarity) returns for one match.
        const string wire = """
        { "submission_a_id": "sub-a", "submission_b_id": "sub-b", "similarity_score": 0.87 }
        """;

        var match = JsonSerializer.Deserialize<SimilarityMatchResult>(wire, Options);

        Assert.NotNull(match);
        Assert.Equal("sub-a", match!.SubmissionAId);
        Assert.Equal("sub-b", match.SubmissionBId);
        // Pre-fix this deserialized to 0 (the silent-zero symptom).
        Assert.Equal(0.87, match.SimilarityScore);
    }

    [Fact]
    public void SnakeCaseFix_AutogradeSuggestionResult_DeserializesFromSnakeCase_IncludingMaxScore()
    {
        // Exact bytes ai-services (/autograde) returns.
        const string wire = """
        {
          "suggested_grade": 7.5,
          "max_score": 10,
          "confidence": 0.66,
          "matched_criteria": ["clarity", "depth"],
          "feedback": ["expand section 2"]
        }
        """;

        var result = JsonSerializer.Deserialize<AutogradeSuggestionResult>(wire, Options);

        Assert.NotNull(result);
        // Pre-fix all of these deserialized to 0/empty and max_score was dropped.
        Assert.Equal(7.5, result!.SuggestedGrade);
        Assert.Equal(10, result.MaxScore);
        Assert.Equal(0.66, result.Confidence);
        Assert.Equal(["clarity", "depth"], result.MatchedCriteria);
        Assert.Equal(["expand section 2"], result.Feedback);
    }

    [Fact]
    public void SnakeCaseFix_SuspiciousFlagResult_DeserializesFromSnakeCase()
    {
        // Exact bytes ai-services (/suspicious-behaviour) returns for one flag.
        const string wire = """
        {
          "student_id": "stu-1",
          "class_session_id": "cs-1",
          "assignment_id": null,
          "confidence_score": 0.91,
          "reasons": ["uniform_event_timing"]
        }
        """;

        var flag = JsonSerializer.Deserialize<SuspiciousFlagResult>(wire, Options);

        Assert.NotNull(flag);
        Assert.Equal("stu-1", flag!.StudentId);
        Assert.Equal("cs-1", flag.ClassSessionId);
        Assert.Null(flag.AssignmentId);
        // Pre-fix confidence_score never mapped to ConfidenceScore -> stayed 0.
        Assert.Equal(0.91, flag.ConfidenceScore);
        Assert.Equal(["uniform_event_timing"], flag.Reasons);
    }
}
