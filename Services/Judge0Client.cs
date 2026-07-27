using System.Diagnostics;
using System.IO.Compression;
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
    Task<CodeRunResultDto> RunAsync(string entryFilePath, IReadOnlyList<CodeFileDto> files, string? stdin, CancellationToken ct = default);
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

    public async Task<CodeRunResultDto> RunAsync(
        string entryFilePath, IReadOnlyList<CodeFileDto> files, string? stdin, CancellationToken ct = default)
    {
        var entryFile = files.FirstOrDefault(f => f.Path == entryFilePath)
            ?? throw new InvalidOperationException($"Entry file '{entryFilePath}' is not one of the submitted files.");

        // Defense in depth: SEK's validateProject already rejects an unsupported language
        // client-side, but every file's language is re-validated here too since this is the
        // layer that actually owns the Language -> Judge0 mapping.
        foreach (var file in files)
        {
            if (!LanguageIds.ContainsKey(file.Language))
            {
                throw new UnsupportedLanguageException(file.Language);
            }
        }

        var languageId = LanguageIds[entryFile.Language];
        var additionalFilesZip = BuildAdditionalFilesZip(files.Where(f => f.Path != entryFilePath).ToList());

        var authHeader = configuration["Judge0:AuthHeader"] ?? "X-Judge0-Auth";
        var authToken = configuration["Judge0:AuthToken"];

        var submitRequest = new HttpRequestMessage(HttpMethod.Post, "/submissions?base64_encoded=true")
        {
            Content = JsonContent.Create(new
            {
                source_code = ToBase64(entryFile.Content),
                language_id = languageId,
                stdin = stdin is null ? null : ToBase64(stdin),
                additional_files = additionalFilesZip,
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
                    (long)stopwatch.Elapsed.TotalMilliseconds, TimedOut: statusId == 5, Status: MapStatus(statusId));
            }

            if (stopwatch.Elapsed >= PollTimeout)
            {
                return new CodeRunResultDto("", "The Code Execution Service did not finish in time.",
                    ExitCode: 1, (long)stopwatch.Elapsed.TotalMilliseconds, TimedOut: true, Status: "time_limit_exceeded");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    // Coarsens Judge0's own status id into the four buckets CodeRunResult.status
    // distinguishes for the Problems panel (see campus-shared-editor-kit's types.ts).
    // Anything outside the known ids maps to null (unknown), not a guess.
    private static string? MapStatus(int statusId) => statusId switch
    {
        3 => "accepted",
        5 => "time_limit_exceeded",
        6 => "compilation_error",
        >= 7 and <= 12 => "runtime_error",
        13 or 14 => "internal_error",
        _ => null,
    };

    // Zips every non-entry file for Judge0's `additional_files` field (a base64 zip Judge0
    // extracts alongside source_code before compiling/running). Judge0 extracts into one
    // flat sandbox directory — it has no concept of a nested folder tree at run time — so
    // every path is flattened to its basename before zipping. This means cross-file
    // references only work when a language's toolchain auto-discovers same-directory files
    // by name (C/C++ #include, Java sibling classes), not package-relative imports (e.g.
    // Python "from pkg.sub import x" won't resolve once "pkg/sub.py" is flattened to
    // "sub.py"). A real, permanent constraint of reusing Judge0 CE unmodified — not
    // something to "fix" here. Returns null when there are no additional files, so the
    // submission body omits the field entirely rather than sending an empty zip.
    public static string? BuildAdditionalFilesZip(IReadOnlyList<CodeFileDto> nonEntryFiles)
    {
        if (nonEntryFiles.Count == 0)
        {
            return null;
        }

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedNames = new HashSet<string>();
            foreach (var file in nonEntryFiles)
            {
                var basename = Path.GetFileName(file.Path);
                // Two files with different folders but the same basename would otherwise
                // silently collide once flattened — disambiguate deterministically rather
                // than letting the second entry overwrite the first inside the zip.
                var entryName = basename;
                var suffix = 1;
                while (!usedNames.Add(entryName))
                {
                    entryName = $"{Path.GetFileNameWithoutExtension(basename)}_{suffix}{Path.GetExtension(basename)}";
                    suffix++;
                }

                var entry = archive.CreateEntry(entryName);
                using var entryStream = entry.Open();
                using var writer = new StreamWriter(entryStream, Encoding.UTF8);
                writer.Write(file.Content);
            }
        }

        return Convert.ToBase64String(zipStream.ToArray());
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
