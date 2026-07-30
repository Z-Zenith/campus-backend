namespace BackendApi.Contracts;

// SEK-01: student writes/runs a multi-file project in the Coding app. SDA's CodeBridge just
// forwards SEK's CodeProject (see campus-shared-editor-kit's types.ts); the backend's only
// job is mapping it onto ContainerCodeRunner (a per-submission `docker run`, see that class's
// doc comment for why it replaced the earlier Judge0-backed runner) and back.
public record CodeFileDto(string Path, string Language, string Content);

// EntryFilePath must name a file present in Files — its Language selects the Docker image/
// compile-run commands (see ContainerCodeRunner.Languages). Every other file is written to the
// submission's workdir at its real relative path (subdirectories preserved), not flattened.
public record RunCodeProjectRequest(string EntryFilePath, IReadOnlyList<CodeFileDto> Files, string? Stdin);

// Status is one of ContainerCodeRunner.ClassifyRunResult's four buckets ("accepted" |
// "compilation_error" | "runtime_error" | "time_limit_exceeded"), matching the Problems panel
// distinction (see campus-shared-editor-kit's CodeRunResult.status doc comment) — null when
// the runner didn't reach a classifiable terminal state.
public record CodeRunResultDto(string Stdout, string Stderr, int ExitCode, long DurationMs, bool TimedOut, string? Status);

// SEK-01 integrated terminal: request/response command execution against a persistent,
// workspace-mounted container (see TerminalSessionService's doc comment for why this is
// deliberately not a full pty-backed live shell).
public record TerminalStartRequest(IReadOnlyList<CodeFileDto> Files);

public record TerminalStartResponse(Guid SessionId);

public record TerminalExecRequest(string Command);

public record TerminalExecResultDto(string Stdout, string Stderr, int ExitCode);
