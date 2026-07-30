using BackendApi.Contracts;

namespace BackendApi.Services;

// B1 remote-execution fallback (SDA/SEK plan): ICodeRunner backed by Piston (see
// PistonClient's doc comment for why Piston over Judge0). Only reached via
// CompositeCodeRunner when ContainerCodeRunner reports no local runtime available or an
// infra-level failure — never for a real compile/runtime error, which is a normal result,
// not a fallback trigger.
//
// Known limitation, worth flagging rather than hiding: the language versions available on
// a stock Piston instance don't all match ContainerCodeRunner's own images — notably
// .NET 5 here vs. the SDK 8.0 image ContainerCodeRunner uses, and Go 1.16 vs. 1.22. A
// submission that only compiles under a newer language version could succeed on the
// primary runner and fail on this fallback. Custom Piston packages could close this gap
// later; not attempted here.
public sealed class RemoteCodeRunner(ILogger<RemoteCodeRunner> logger, IPistonClient piston) : ICodeRunner
{
    // Confirmed directly against a real Piston instance's GET /api/v2/packages during the
    // 0.1/B1 spike, except "c"/"cpp" — see PistonClient's doc comment.
    private static readonly Dictionary<string, PistonLanguageSpec> Languages = new()
    {
        ["python"] = new("python", "3.12.0"),
        ["c"] = new("c", "10.2.0"),
        ["cpp"] = new("c++", "10.2.0"),
        ["java"] = new("java", "15.0.2"),
        ["javascript"] = new("node", "20.11.1"),
        ["nodejs"] = new("node", "20.11.1"),
        ["typescript"] = new("typescript", "5.0.3"),
        ["dotnet"] = new("dotnet", "5.0.201"),
        ["sql"] = new("sqlite3", "3.36.0"),
        ["go"] = new("go", "1.16.2"),
        ["rust"] = new("rust", "1.68.2"),
        ["ruby"] = new("ruby", "3.0.1"),
        ["php"] = new("php", "8.2.3"),
        ["kotlin"] = new("kotlin", "1.8.20"),
        ["shell"] = new("bash", "5.2.0"),
    };

    public async Task<CodeRunResultDto> RunAsync(
        string entryFilePath, IReadOnlyList<CodeFileDto> files, string? stdin, CancellationToken ct = default)
    {
        var entryFile = files.FirstOrDefault(f => f.Path == entryFilePath)
            ?? throw new InvalidOperationException($"Entry file '{entryFilePath}' is not one of the submitted files.");

        if (!Languages.TryGetValue(entryFile.Language, out var languageSpec))
        {
            throw new UnsupportedLanguageException(entryFile.Language);
        }

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await piston.ExecuteAsync(
                languageSpec, entryFilePath, files.Select(f => (f.Path, f.Content)).ToList(), stdin, ct);

            if (result.CompileExitCode is { } compileExitCode && compileExitCode != 0)
            {
                return new CodeRunResultDto(
                    "", result.CompileStderr ?? "", compileExitCode, (long)stopwatch.Elapsed.TotalMilliseconds,
                    TimedOut: false, Status: "compilation_error");
            }

            var exitCode = result.ExitCode ?? -1;
            var status = result.TimedOut ? "time_limit_exceeded" : exitCode == 0 ? "accepted" : "runtime_error";
            return new CodeRunResultDto(
                result.Stdout, result.Stderr, exitCode, (long)stopwatch.Elapsed.TotalMilliseconds, result.TimedOut, status);
        }
        catch (Exception ex) when (ex is not UnsupportedLanguageException)
        {
            logger.LogError(ex, "Remote (Piston) code execution failed for language {Language}", entryFile.Language);
            // Same exception type/message ContainerCodeRunner's own infra failures use, so
            // CompositeCodeRunner and CodeExecutionController's existing 503 mapping treat
            // this identically — but note: CompositeCodeRunner only falls back TO this
            // runner in the first place, it doesn't fall back AGAIN if this one also fails,
            // so this exception just propagates to the caller as the final answer.
            throw new HttpRequestException("The Code Execution Service is unreachable. Try again shortly.", ex);
        }
    }
}
