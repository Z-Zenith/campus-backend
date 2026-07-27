namespace BackendApi.Contracts;

// SEK-01: student writes/runs a multi-file project in the Coding app. SDA's CodeBridge just
// forwards SEK's CodeProject (see campus-shared-editor-kit's types.ts); the backend's only
// job is mapping it onto the self-hosted Code Execution Service (Judge0) and back.
public record CodeFileDto(string Path, string Language, string Content);

// EntryFilePath must name a file present in Files — its Language selects Judge0's
// language_id. Every other file is submitted as an "additional file" (see Judge0Client),
// flattened to its basename since Judge0 extracts additional_files into one flat sandbox
// directory, not a nested tree.
public record RunCodeProjectRequest(string EntryFilePath, IReadOnlyList<CodeFileDto> Files, string? Stdin);

// Status mirrors Judge0's own status id, coarsened to the four buckets the Problems panel
// distinguishes (see campus-shared-editor-kit's CodeRunResult.status doc comment) — null
// when the runner didn't reach a classifiable terminal state.
public record CodeRunResultDto(string Stdout, string Stderr, int ExitCode, long DurationMs, bool TimedOut, string? Status);
