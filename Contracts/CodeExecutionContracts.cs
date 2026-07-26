namespace BackendApi.Contracts;

// SEK-01: student writes/runs code in the Coding app. Content is passed through verbatim
// (SDA's CodeBridge just forwards SEK's CodeSource); the backend's only job is mapping to
// the self-hosted Code Execution Service (Judge0) and back.
public record RunCodeRequest(string Language, string Content, string? Stdin, string? Filename);

public record CodeRunResultDto(string Stdout, string Stderr, int ExitCode, long DurationMs, bool TimedOut);
