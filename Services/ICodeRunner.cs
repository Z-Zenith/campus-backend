using BackendApi.Contracts;

namespace BackendApi.Services;

// Thrown for a SEK-01 Language value with no runner — mirrors SEK-01's own
// "unsupported_language" acceptance criterion (a language outside the launch list shows
// a clear error, not a silent failure), just enforced backend-side here since this is the
// layer that owns the Language -> execution-image mapping.
public class UnsupportedLanguageException(string language) : Exception($"'{language}' is not a supported language.");

// SEK-01: executes a code-run request. Was Judge0-backed (see git history); replaced by
// ContainerCodeRunner because Judge0's isolate sandbox requires cgroup v1, which this
// environment's kernel doesn't provide — see ContainerCodeRunner's own doc comment for the
// full story. Interface kept minimal and Judge0-agnostic so it doesn't leak the
// implementation's mechanism.
public interface ICodeRunner
{
    Task<CodeRunResultDto> RunAsync(string entryFilePath, IReadOnlyList<CodeFileDto> files, string? stdin, CancellationToken ct = default);
}
