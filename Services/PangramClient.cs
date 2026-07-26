using System.Net.Http.Json;

namespace BackendApi.Services;

public record AiDetectionResult(double AiLikelihoodScore, string? ReportId);

// AIS-05: thin client for Pangram's external AI-content-detection API. Unlike Copyleaks
// (AIS-02), which crawls the internet and calls back asynchronously via webhook, Pangram's
// classifier is a same-request scoring call — there's no reason to believe it needs
// crawl-style async processing, so this is a plain synchronous request/response client.
//
// Wire format below (endpoint path, request/response field names) is best-effort guesswork,
// not verified against a live Pangram sandbox — this environment has no real Pangram
// credentials, same disclosure as CopyleaksClient.cs. Confirm/fix once real API access exists.
public interface IPangramClient
{
    Task<AiDetectionResult> DetectAsync(string content, CancellationToken ct = default);
}

public class PangramClient(HttpClient http, IConfiguration configuration) : IPangramClient
{
    private sealed record PangramDetectResponseBody(double AiLikelihoodScore, string? ReportId);

    public async Task<AiDetectionResult> DetectAsync(string content, CancellationToken ct = default)
    {
        var apiKey = configuration["Pangram:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ExternalServiceNotConfiguredException("Pangram");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/detect")
        {
            Content = JsonContent.Create(new { text = content }),
        };
        request.Headers.Add("x-api-key", apiKey);

        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PangramDetectResponseBody>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Pangram returned an empty response.");

        return new AiDetectionResult(body.AiLikelihoodScore, body.ReportId);
    }
}
