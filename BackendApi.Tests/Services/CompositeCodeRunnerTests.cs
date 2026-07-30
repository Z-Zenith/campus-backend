using BackendApi.Contracts;
using BackendApi.Services;
using BackendApi.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Services;

// B1 remote-execution fallback (SDA/SEK plan): CompositeCodeRunner's fallback-trigger
// logic, tested with plain FakeCodeRunner instances for both primary and fallback — this
// is exactly why CompositeCodeRunner depends on ICodeRunner rather than the concrete
// ContainerCodeRunner/RemoteCodeRunner types directly (see its own doc comment).
public class CompositeCodeRunnerTests
{
    private static readonly List<CodeFileDto> Files = [new("main.py", "python", "print(1)")];

    private static CompositeCodeRunner NewRunner(FakeCodeRunner primary, FakeCodeRunner fallback) =>
        new(NullLogger<CompositeCodeRunner>.Instance, primary, fallback);

    [Fact]
    public async Task RunAsync_UsesThePrimaryResult_WhenThePrimarySucceeds()
    {
        var primary = new FakeCodeRunner { Result = new CodeRunResultDto("primary-output", "", 0, 5, false, "accepted") };
        var fallback = new FakeCodeRunner { Result = new CodeRunResultDto("fallback-output", "", 0, 5, false, "accepted") };
        var runner = NewRunner(primary, fallback);

        var result = await runner.RunAsync("main.py", Files, stdin: null);

        Assert.Equal("primary-output", result.Stdout);
    }

    // The core case this whole class exists for: the primary being unreachable (no local
    // container runtime, or the daemon down) must fall through to the remote runner.
    [Fact]
    public async Task RunAsync_FallsBackToRemote_WhenThePrimaryThrowsAnUnreachableException()
    {
        var primary = new FakeCodeRunner { ThrowOnRun = new HttpRequestException("The Code Execution Service is unreachable. Try again shortly.") };
        var fallback = new FakeCodeRunner { Result = new CodeRunResultDto("fallback-output", "", 0, 5, false, "accepted") };
        var runner = NewRunner(primary, fallback);

        var result = await runner.RunAsync("main.py", Files, stdin: null);

        Assert.Equal("fallback-output", result.Stdout);
    }

    // A real, non-zero-exit result from the primary (the student's code genuinely failed)
    // must NOT trigger fallback — re-running it remotely would be pointless (same bug)
    // and would blur "the runner is broken" with "the code is broken."
    [Fact]
    public async Task RunAsync_DoesNotFallBack_OnANormalNonZeroExitResult()
    {
        var primary = new FakeCodeRunner { Result = new CodeRunResultDto("", "Traceback...", 1, 5, false, "runtime_error") };
        var fallback = new FakeCodeRunner { Result = new CodeRunResultDto("should-not-be-used", "", 0, 5, false, "accepted") };
        var runner = NewRunner(primary, fallback);

        var result = await runner.RunAsync("main.py", Files, stdin: null);

        Assert.Equal("runtime_error", result.Status);
        Assert.Equal("", result.Stdout);
    }

    // An UnsupportedLanguageException is a real language error, not an infra failure —
    // must propagate, not trigger fallback (the fallback runner may not even support the
    // same language list, and this isn't the failure mode fallback exists for).
    [Fact]
    public async Task RunAsync_DoesNotFallBack_OnUnsupportedLanguageException_ItPropagates()
    {
        var primary = new FakeCodeRunner { ThrowOnRun = new UnsupportedLanguageException("brainfuck") };
        var fallback = new FakeCodeRunner { Result = new CodeRunResultDto("should-not-be-used", "", 0, 5, false, "accepted") };
        var runner = NewRunner(primary, fallback);

        await Assert.ThrowsAsync<UnsupportedLanguageException>(() => runner.RunAsync("main.py", Files, stdin: null));
    }

    // Only HttpRequestException (the infra-unreachable signal) triggers fallback — any
    // other unexpected exception type from the primary should propagate as-is rather than
    // being silently swallowed into a fallback attempt.
    [Fact]
    public async Task RunAsync_DoesNotFallBack_OnAnUnrelatedExceptionType()
    {
        var primary = new FakeCodeRunner { ThrowOnRun = new InvalidOperationException("something else entirely") };
        var fallback = new FakeCodeRunner { Result = new CodeRunResultDto("should-not-be-used", "", 0, 5, false, "accepted") };
        var runner = NewRunner(primary, fallback);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("main.py", Files, stdin: null));
    }
}
