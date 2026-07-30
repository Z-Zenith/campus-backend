using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BackendApi.Services;

// B1 remote-execution fallback (SDA/SEK plan): thin HTTP client for a self-hosted Piston
// instance (https://github.com/engineer-man/piston, ghcr.io/engineer-man/piston). Chosen
// over Judge0 after 0.1's spike — Judge0's isolate sandbox needs cgroup v1, which this
// class of host doesn't provide (see judge0.conf's own investigation log); Piston does not
// depend on isolate and was confirmed working end-to-end on the same cgroup v2 host during
// that spike (real package install + real execution, correct stdout).
//
// Language/version mapping confirmed against a real Piston instance's GET /api/v2/packages
// during the spike, EXCEPT "c"/"cpp": Piston bundles both under one "gcc" package, and the
// exact alias names ("c"/"c++" vs. something else) the execute API expects for that package
// were not empirically confirmed in the spike (the install call didn't complete in the
// available time) — verify against a real deployment's GET /api/v2/runtimes (its `aliases`
// field) before relying on those two specifically; every other language below was
// confirmed directly from the packages list.
public sealed record PistonLanguageSpec(string PistonLanguage, string Version);

public interface IPistonClient
{
    Task<PistonExecuteResult> ExecuteAsync(
        PistonLanguageSpec languageSpec, string entryFileName, IReadOnlyList<(string Name, string Content)> files, string? stdin, CancellationToken ct = default);
}

public sealed record PistonExecuteResult(string Stdout, string Stderr, int? ExitCode, bool TimedOut, string? CompileStderr, int? CompileExitCode);

public sealed class PistonClient(HttpClient http) : IPistonClient
{
    private sealed record ExecuteRequestBody(
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("files")] IReadOnlyList<FileBody> Files,
        [property: JsonPropertyName("stdin")] string Stdin);

    private sealed record FileBody(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("content")] string Content);

    private sealed record RunResultBody(
        [property: JsonPropertyName("stdout")] string Stdout,
        [property: JsonPropertyName("stderr")] string Stderr,
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("signal")] string? Signal);

    private sealed record ExecuteResponseBody(
        [property: JsonPropertyName("run")] RunResultBody Run,
        [property: JsonPropertyName("compile")] RunResultBody? Compile);

    public async Task<PistonExecuteResult> ExecuteAsync(
        PistonLanguageSpec languageSpec, string entryFileName, IReadOnlyList<(string Name, string Content)> files, string? stdin, CancellationToken ct = default)
    {
        // Piston runs whichever file is listed FIRST as the entry point — reorder so the
        // real entry file is always files[0], regardless of submission order.
        var orderedFiles = files
            .OrderByDescending(f => f.Name == entryFileName)
            .Select(f => new FileBody(f.Name, f.Content))
            .ToList();

        var body = new ExecuteRequestBody(languageSpec.PistonLanguage, languageSpec.Version, orderedFiles, stdin ?? "");
        var response = await http.PostAsJsonAsync("/api/v2/execute", body, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ExecuteResponseBody>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Piston returned an empty execute response.");

        // Piston has no separate "timed out" flag — a killed-by-timeout process reports
        // signal "SIGKILL" with a null exit code, the same shape a plain crash could
        // produce, so this is a best-effort inference, not a guarantee. Piston's own
        // default per-request timeout is what actually enforces the bound.
        var timedOut = result.Run.Signal == "SIGKILL" && result.Run.Code is null;

        return new PistonExecuteResult(
            result.Run.Stdout,
            result.Run.Stderr,
            result.Run.Code,
            timedOut,
            result.Compile?.Stderr,
            result.Compile?.Code);
    }
}
