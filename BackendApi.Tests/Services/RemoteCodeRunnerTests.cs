using BackendApi.Contracts;
using BackendApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Services;

// B1 remote-execution fallback (SDA/SEK plan): RemoteCodeRunner's own mapping/
// classification logic, independent of a live Piston instance — mirrors
// ContainerCodeRunnerTests' pure-helper-only approach (the actual HTTP call is the
// integration seam, deliberately not unit-tested here).
public class RemoteCodeRunnerTests
{
    private sealed class FakePistonClient : IPistonClient
    {
        public PistonExecuteResult Result { get; set; } = new("", "", 0, false, null, null);
        public Exception? ThrowOnExecute { get; set; }
        public PistonLanguageSpec? LastLanguageSpec { get; private set; }

        public Task<PistonExecuteResult> ExecuteAsync(
            PistonLanguageSpec languageSpec, string entryFileName, IReadOnlyList<(string Name, string Content)> files, string? stdin, CancellationToken ct = default)
        {
            if (ThrowOnExecute is not null) throw ThrowOnExecute;
            LastLanguageSpec = languageSpec;
            return Task.FromResult(Result);
        }
    }

    private static readonly List<CodeFileDto> PythonFiles = [new("main.py", "python", "print(1)")];

    [Fact]
    public async Task RunAsync_MapsPythonToTheConfirmedPistonLanguageAndVersion()
    {
        var fakePiston = new FakePistonClient();
        var runner = new RemoteCodeRunner(NullLogger<RemoteCodeRunner>.Instance, fakePiston);

        await runner.RunAsync("main.py", PythonFiles, stdin: null);

        Assert.Equal("python", fakePiston.LastLanguageSpec!.PistonLanguage);
        Assert.Equal("3.12.0", fakePiston.LastLanguageSpec.Version);
    }

    [Fact]
    public async Task RunAsync_ThrowsUnsupportedLanguageException_ForALanguageOutsideTheMap()
    {
        var runner = new RemoteCodeRunner(NullLogger<RemoteCodeRunner>.Instance, new FakePistonClient());
        var files = new List<CodeFileDto> { new("main.bf", "brainfuck", "") };

        await Assert.ThrowsAsync<UnsupportedLanguageException>(() => runner.RunAsync("main.bf", files, stdin: null));
    }

    [Fact]
    public async Task RunAsync_ClassifiesASuccessfulRun_AsAccepted()
    {
        var fakePiston = new FakePistonClient { Result = new PistonExecuteResult("1\n", "", 0, false, null, null) };
        var runner = new RemoteCodeRunner(NullLogger<RemoteCodeRunner>.Instance, fakePiston);

        var result = await runner.RunAsync("main.py", PythonFiles, stdin: null);

        Assert.Equal("accepted", result.Status);
        Assert.Equal("1\n", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_ClassifiesANonZeroExit_AsRuntimeError()
    {
        var fakePiston = new FakePistonClient { Result = new PistonExecuteResult("", "boom", 1, false, null, null) };
        var runner = new RemoteCodeRunner(NullLogger<RemoteCodeRunner>.Instance, fakePiston);

        var result = await runner.RunAsync("main.py", PythonFiles, stdin: null);

        Assert.Equal("runtime_error", result.Status);
    }

    [Fact]
    public async Task RunAsync_ClassifiesANonZeroCompileExitCode_AsCompilationError_WithoutRunning()
    {
        var fakePiston = new FakePistonClient { Result = new PistonExecuteResult("", "", null, false, "syntax error", 1) };
        var runner = new RemoteCodeRunner(NullLogger<RemoteCodeRunner>.Instance, fakePiston);
        var files = new List<CodeFileDto> { new("main.c", "c", "not valid c") };

        var result = await runner.RunAsync("main.c", files, stdin: null);

        Assert.Equal("compilation_error", result.Status);
        Assert.Equal("syntax error", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_ClassifiesATimeout_AsTimeLimitExceeded()
    {
        var fakePiston = new FakePistonClient { Result = new PistonExecuteResult("", "", null, true, null, null) };
        var runner = new RemoteCodeRunner(NullLogger<RemoteCodeRunner>.Instance, fakePiston);

        var result = await runner.RunAsync("main.py", PythonFiles, stdin: null);

        Assert.Equal("time_limit_exceeded", result.Status);
        Assert.True(result.TimedOut);
    }

    // Any unexpected failure talking to Piston itself must surface as the same
    // "unreachable" signal ContainerCodeRunner's own infra failures use, so
    // CompositeCodeRunner/CodeExecutionController handle both identically.
    [Fact]
    public async Task RunAsync_WrapsAnUnexpectedFailure_AsTheStandardUnreachableException()
    {
        var fakePiston = new FakePistonClient { ThrowOnExecute = new InvalidOperationException("connection refused") };
        var runner = new RemoteCodeRunner(NullLogger<RemoteCodeRunner>.Instance, fakePiston);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => runner.RunAsync("main.py", PythonFiles, stdin: null));
        Assert.Equal("The Code Execution Service is unreachable. Try again shortly.", ex.Message);
    }
}
