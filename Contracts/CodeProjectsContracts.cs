namespace BackendApi.Contracts;

// SEK-01: persistence for the Coding app's multi-file projects — see
// CodeExecutionContracts.cs's CodeFileDto (shared across both the run and persistence
// paths) and campus-shared-editor-kit's types.ts for the CodeProject shape this mirrors.
public record CreateCodeProjectRequest(
    string Name, IReadOnlyList<CodeFileDto> Files, string EntryFilePath, string ActiveFilePath, string? Stdin, Guid? Id = null);

public record UpdateCodeProjectRequest(
    string Name, IReadOnlyList<CodeFileDto> Files, string EntryFilePath, string ActiveFilePath, string? Stdin);

public record CodeProjectDto(
    Guid Id, string Name, IReadOnlyList<CodeFileDto> Files, string EntryFilePath, string ActiveFilePath,
    string? Stdin, DateTime CreatedAt, DateTime UpdatedAt);

public record CodeProjectSummaryDto(Guid Id, string Name, DateTime UpdatedAt);
