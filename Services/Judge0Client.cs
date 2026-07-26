using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BackendApi.Contracts;

namespace BackendApi.Services;

// Thrown for a SEK-01 Language value with no Judge0 runner — mirrors SEK-01's own
// "unsupported_language" acceptance criterion (a language outside the launch list shows
// a clear error, not a silent failure), just enforced backend-side here since this is the
// layer that owns the Language -> Judge0 language_id mapping.
public class UnsupportedLanguageException(string language) : Exception($"'{language}' is not a supported language.");

// SEK-01: proxies code-run requests to the self-hosted Code Execution Service (Judge0,
// see campus-platform/docker-compose.yml's judge0-* services). Submits asynchronously and
// polls rather than using Judge0's synchronous `wait=true` mode — the workers container
// processes jobs off a queue, so a blocking wait ties up this request for as long as the
// queue takes, whereas polling lets us bound the wait and fail closed with TimedOut=true
// instead of hanging indefinitely if the queue is backed up.
public interface IJudge0Client
{
    Task<CodeRunResultDto> RunAsync(string language, string content, string? stdin, CancellationToken ct = default);
}

public class Judge0Client(HttpClient http, IConfiguration configuration) : IJudge0Client
{
    // SEK-01's closed launch-list language -> Judge0 language_id, confirmed against a live
    // Judge0 1.13.1 CE instance's own /languages endpoint (not just guessed from public
    // docs). html/css/json/yaml have no real "run" semantics in a code sandbox — there's
    // nothing to execute — so they're mapped to Judge0's Plain Text runner (43), which is
    // a harmless no-op (echoes stdin, no compile/exec step) rather than a crash. That's a
    // real limitation of "run" for those four, not equivalent to actually executing them;
    // flagged here rather than silently pretended otherwise.
    private static readonly Dictionary<string, int> LanguageIds = new()
    {
        ["c"] = 50,
        ["cpp"] = 54,
        ["python"] = 71,
        ["java"] = 62,
        ["dotnet"] = 51, // C# (Mono 6.6.0.161) — this Judge0 instance has no separate .NET Core C# runtime
        ["javascript"] = 63,
        ["typescript"] = 74,
        ["nodejs"] = 63,
        ["sql"] = 82,
        ["html"] = 43,
        ["css"] = 43,
        ["json"] = 43,
        ["yaml"] = 43,
    };

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(20);

    private sealed record SubmitResponse(string Token);
    private sealed record StatusInfo(int Id, string Description);
    private sealed record SubmissionResponse(string? Stdout, string? Stderr, string? CompileOutput, StatusInfo? Status);

    public async Task<CodeRunResultDto> RunAsync(string language, string content, string? stdin, CancellationToken ct = default)
    {
        if (!LanguageIds.TryGetValue(language, out var languageId))
        {
            throw new UnsupportedLanguageException(language);
        }

        var authHeader = configuration["Judge0:AuthHeader"] ?? "X-Judge0-Auth";
        var authToken = configuration["Judge0:AuthToken"];

        var submitRequest = new HttpRequestMessage(HttpMethod.Post, "/submissions?base64_encoded=true")
        {
            Content = JsonContent.Create(new
            {
                source_code = ToBase64(content),
                language_id = languageId,
                stdin = stdin is null ? null : ToBase64(stdin),
            }),
        };
        AddAuth(submitRequest, authHeader, authToken);

        var submitResponse = await http.SendAsync(submitRequest, ct);
        submitResponse.EnsureSuccessStatusCode();
        var submission = await submitResponse.Content.ReadFromJsonAsync<SubmitResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Judge0 did not return a submission token.");

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"/submissions/{submission.Token}?base64_encoded=true");
            AddAuth(pollRequest, authHeader, authToken);
            var pollResponse = await http.SendAsync(pollRequest, ct);
            pollResponse.EnsureSuccessStatusCode();
            var result = await pollResponse.Content.ReadFromJsonAsync<SubmissionResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("Judge0 returned an empty submission result.");

            // Judge0 status IDs: 1=In Queue, 2=Processing — anything else is a terminal
            // state (3=Accepted, 5=Time Limit Exceeded, 6=Compilation Error, 7-12=various
            // Runtime Errors, 13=Internal Error, 14=Exec Format Error).
            var statusId = result.Status?.Id ?? 0;
            if (statusId is not (1 or 2))
            {
                var stdout = FromBase64(result.Stdout);
                var stderr = FromBase64(result.Stderr);
                var compileOutput = FromBase64(result.CompileOutput);
                if (!string.IsNullOrEmpty(compileOutput))
                {
                    stderr = string.IsNullOrEmpty(stderr) ? compileOutput : $"{stderr}\n{compileOutput}";
                }

                return new CodeRunResultDto(stdout, stderr, statusId == 3 ? 0 : 1,
                    (long)stopwatch.Elapsed.TotalMilliseconds, TimedOut: statusId == 5);
            }

            if (stopwatch.Elapsed >= PollTimeout)
            {
                return new CodeRunResultDto("", "The Code Execution Service did not finish in time.",
                    ExitCode: 1, (long)stopwatch.Elapsed.TotalMilliseconds, TimedOut: true);
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    private static void AddAuth(HttpRequestMessage request, string authHeader, string? authToken)
    {
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            request.Headers.Add(authHeader, authToken);
        }
    }

    private static string ToBase64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string FromBase64(string? value) =>
        string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
