using BackendApi.Contracts;
using BackendApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Services;

// SEK-01: focused unit tests for ContainerCodeRunner's pure helpers, independent of a live
// container daemon (see RunAsync's own remarks on why the actual `<runtime> run` invocation
// isn't unit-tested — same rationale Judge0ClientTests used for Judge0's live HTTP calls).
public class ContainerCodeRunnerTests
{
    // Every SEK-01 Language the frontend can produce (campus-shared-editor-kit's
    // types.ts Language union) — kept as a literal list here (not imported, there's no
    // shared assembly) so a language added to one side without the other fails a test
    // instead of silently becoming "unsupported" at runtime.
    private static readonly string[] FrontendLanguages =
    [
        "c", "cpp", "python", "java", "dotnet",
        "html", "css", "javascript", "typescript", "nodejs",
        "sql", "json", "yaml",
        "go", "rust", "ruby", "php", "kotlin", "shell",
    ];

    [Theory]
    [MemberData(nameof(AllFrontendLanguages))]
    public void IsKnownLanguage_CoversEveryFrontendLanguage(string language)
    {
        Assert.True(ContainerCodeRunner.IsKnownLanguage(language), language);
    }

    public static IEnumerable<object[]> AllFrontendLanguages() =>
        FrontendLanguages.Select(l => new object[] { l });

    [Fact]
    public void IsKnownLanguage_RejectsAForeignLanguage()
    {
        Assert.False(ContainerCodeRunner.IsKnownLanguage("brainfuck"));
    }

    [Fact]
    public void BuildContainerArgs_DisablesNetworkAccess()
    {
        var args = ContainerCodeRunner.BuildContainerArgs("python:3.12-slim", @"C:\tmp\x", "python main.py", 10);

        Assert.Contains("--network", args);
        Assert.Equal("none", args[Array.IndexOf(args, "--network") + 1]);
    }

    [Fact]
    public void BuildContainerArgs_SetsResourceCeilings()
    {
        var args = ContainerCodeRunner.BuildContainerArgs("python:3.12-slim", @"C:\tmp\x", "python main.py", 10);

        Assert.Contains("--memory", args);
        Assert.Contains("--pids-limit", args);
        Assert.Contains("--cpus", args);
    }

    [Fact]
    public void BuildContainerArgs_AutoRemovesContainerAndMountsWorkDir()
    {
        var args = ContainerCodeRunner.BuildContainerArgs("python:3.12-slim", @"C:\tmp\workdir", "python main.py", 10);

        Assert.Contains("--rm", args);
        Assert.Contains("-v", args);
        Assert.Equal(@"C:\tmp\workdir:/box", args[Array.IndexOf(args, "-v") + 1]);
        Assert.Equal("python:3.12-slim", args[Array.IndexOf(args, "-w") + 2]);
    }

    [Fact]
    public void BuildContainerArgs_WrapsCommandInAnInContainerTimeout()
    {
        var args = ContainerCodeRunner.BuildContainerArgs("python:3.12-slim", @"C:\tmp\x", "python main.py", 7);

        Assert.Equal("timeout 7s python main.py", args[^1]);
    }

    [Fact]
    public void StemFromEntryPath_StripsExtension()
    {
        Assert.Equal("main", ContainerCodeRunner.StemFromEntryPath("main.py"));
    }

    [Fact]
    public void StemFromEntryPath_UsesBasenameForNestedPaths()
    {
        Assert.Equal("Main", ContainerCodeRunner.StemFromEntryPath("src/Main.java"));
    }

    [Fact]
    public void ClassNameFromEntryPath_MatchesJavaFilenameConvention()
    {
        // Main.java is the Coding app's own starter-picker default for Java (see
        // campus-shared-editor-kit's defaultFilenameForLanguage) — this must derive
        // "Main" from it since `java Main` is what actually runs the compiled class.
        Assert.Equal("Main", ContainerCodeRunner.ClassNameFromEntryPath("Main.java"));
    }

    // 0.5: on a machine with neither Docker nor Podman reachable, RunAsync must fail fast
    // with the same signal a genuine mid-run failure produces (CodeExecutionController's
    // existing 503 mapping), rather than trying to spawn a nonexistent process and surfacing
    // a confusing Win32Exception instead.
    [Fact]
    public async Task RunAsync_FailsFastWithoutSpawningAProcess_WhenNoContainerRuntimeIsAvailable()
    {
        var runner = new ContainerCodeRunner(NullLogger<ContainerCodeRunner>.Instance, new UnavailableContainerCli());
        var files = new List<CodeFileDto> { new("main.py", "python", "print('hi')") };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => runner.RunAsync("main.py", files, stdin: null));

        Assert.Equal("The Code Execution Service is unreachable. Try again shortly.", ex.Message);
    }

    // A NoExecLanguages entry (html/css/json/yaml) never touches the container runtime at
    // all, so it must succeed even when no runtime is available — this is the one Run path
    // that genuinely doesn't need one.
    [Fact]
    public async Task RunAsync_NoExecLanguage_SucceedsEvenWithoutAContainerRuntime()
    {
        var runner = new ContainerCodeRunner(NullLogger<ContainerCodeRunner>.Instance, new UnavailableContainerCli());
        var files = new List<CodeFileDto> { new("main.html", "html", "<p>hi</p>") };

        var result = await runner.RunAsync("main.html", files, stdin: "echoed");

        Assert.Equal("accepted", result.Status);
        Assert.Equal("echoed", result.Stdout);
    }
}

// 0.5: DockerCli/PodmanCli/UnavailableContainerCli are trivial but worth pinning down —
// ContainerCodeRunner/TerminalSessionService trust ExecutableName completely, and a wrong
// value here would silently shell out to the wrong binary.
public class ContainerCliTests
{
    [Fact]
    public void DockerCli_IsAvailableAndNamesTheDockerExecutable()
    {
        IContainerCli cli = new DockerCli();

        Assert.True(cli.IsAvailable);
        Assert.Equal("docker", cli.ExecutableName);
    }

    [Fact]
    public void PodmanCli_IsAvailableAndNamesThePodmanExecutable()
    {
        IContainerCli cli = new PodmanCli();

        Assert.True(cli.IsAvailable);
        Assert.Equal("podman", cli.ExecutableName);
    }

    [Fact]
    public void UnavailableContainerCli_IsNotAvailable()
    {
        IContainerCli cli = new UnavailableContainerCli();

        Assert.False(cli.IsAvailable);
    }

    [Fact]
    public void UnavailableContainerCli_ThrowsRatherThanReturningAUselessExecutableName()
    {
        IContainerCli cli = new UnavailableContainerCli();

        Assert.Throws<InvalidOperationException>(() => cli.ExecutableName);
    }
}
