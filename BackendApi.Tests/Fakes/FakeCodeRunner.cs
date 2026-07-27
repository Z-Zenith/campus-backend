using BackendApi.Contracts;
using BackendApi.Services;

namespace BackendApi.Tests.Fakes;

// SEK-01: a real `docker run` isn't available/desirable in unit tests, so controller
// tests configure canned behavior here instead.
public class FakeCodeRunner : ICodeRunner
{
    public string? LastEntryFilePath { get; private set; }
    public IReadOnlyList<CodeFileDto>? LastFiles { get; private set; }
    public string? LastStdin { get; private set; }
    public CodeRunResultDto Result { get; set; } = new("", "", 0, 10, false, "accepted");
    public Exception? ThrowOnRun { get; set; }

    public Task<CodeRunResultDto> RunAsync(
        string entryFilePath, IReadOnlyList<CodeFileDto> files, string? stdin, CancellationToken ct = default)
    {
        if (ThrowOnRun is not null)
        {
            throw ThrowOnRun;
        }
        LastEntryFilePath = entryFilePath;
        LastFiles = files;
        LastStdin = stdin;
        return Task.FromResult(Result);
    }
}
