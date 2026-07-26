using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace BackendApi.Services;

// Thrown when an external AI-vendor client is invoked without credentials configured for
// this deployment. Lets controllers respond with a clear 503 instead of letting an
// HttpRequestException (unauthorized login call) bubble up as a generic 500.
public class ExternalServiceNotConfiguredException(string serviceName)
    : Exception($"{serviceName} is not configured for this deployment (missing credentials).")
{
    public string ServiceName { get; } = serviceName;
}

public record PlagiarismScanResult(double SimilarityScore, IReadOnlyList<string> MatchedSourceUrls);

// AIS-02: thin client for Copyleaks' external plagiarism-detection API. Unlike
// IAiServicesClient's self-hosted models, Copyleaks scans asynchronously — SubmitScanAsync
// kicks off a scan and does not return a score; Copyleaks calls back a webhook once the
// scan completes (see WebhooksController.CopyleaksResult), which is why the caller
// supplies a webhook URL template rather than awaiting a result here.
//
// Wire format below (login/submit/webhook-payload shape) is best-effort against
// Copyleaks' public v3 API docs — this environment has no live Copyleaks sandbox or
// credentials to verify against (the same gap flagged when AIS-03/04/07 were wired:
// "AIS-02/05 are external-API-only and out of scope — no credentials available").
// Flagging per CLAUDE.md's manual-verification disclosure requirement rather than
// presenting this as tested against the real vendor.
public interface ICopyleaksClient
{
    Task SubmitScanAsync(string scanId, string content, string webhookUrlTemplate, CancellationToken ct = default);

    PlagiarismScanResult ParseWebhookResult(JsonElement payload);
}

public class CopyleaksClient(HttpClient http, IConfiguration configuration) : ICopyleaksClient
{
    private sealed record LoginResponseBody(string AccessToken);

    public async Task SubmitScanAsync(string scanId, string content, string webhookUrlTemplate, CancellationToken ct = default)
    {
        var email = configuration["Copyleaks:Email"];
        var apiKey = configuration["Copyleaks:ApiKey"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ExternalServiceNotConfiguredException("Copyleaks");
        }

        var loginResponse = await http.PostAsJsonAsync("/v3/account/login/api", new { email, key = apiKey }, ct);
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponseBody>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Copyleaks login returned an empty response.");

        // developerPayload is Copyleaks' documented webhook-authentication mechanism: this
        // secret is echoed back verbatim in the webhook body's top-level `developerPayload`
        // field, and WebhooksController compares it (constant-time) to Copyleaks:WebhookSecret.
        // Carrying the secret here (in the submit body) rather than in the webhook URL keeps it
        // out of access/proxy logs. Copyleaks caps developerPayload at 512 chars.
        // Docs: https://docs.copyleaks.com/concepts/security/webhooks
        var webhookSecret = configuration["Copyleaks:WebhookSecret"] ?? "";

        var submitRequest = new HttpRequestMessage(HttpMethod.Put, $"/v3/scans/submit/text/{scanId}")
        {
            Content = JsonContent.Create(new
            {
                @base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                filename = $"{scanId}.txt",
                properties = new
                {
                    webhooks = new { status = webhookUrlTemplate },
                    developerPayload = webhookSecret,
                },
            }),
        };
        submitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

        var submitResponse = await http.SendAsync(submitRequest, ct);
        submitResponse.EnsureSuccessStatusCode();
    }

    // Copyleaks' completion webhook body carries results.score.aggregatedScore as a
    // 0-100 int and results.internet[].url for matched sources, per their public docs.
    public PlagiarismScanResult ParseWebhookResult(JsonElement payload)
    {
        var results = payload.GetProperty("results");
        var aggregatedScore = results.GetProperty("score").GetProperty("aggregatedScore").GetDouble();

        var matchedUrls = new List<string>();
        if (results.TryGetProperty("internet", out var internetMatches) && internetMatches.ValueKind == JsonValueKind.Array)
        {
            foreach (var match in internetMatches.EnumerateArray())
            {
                if (match.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    matchedUrls.Add(url.GetString()!);
                }
            }
        }

        return new PlagiarismScanResult(aggregatedScore / 100.0, matchedUrls);
    }
}
