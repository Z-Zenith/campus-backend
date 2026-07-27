using BackendApi.Services;

namespace BackendApi.Tests.Services;

// SEK-01: focused unit tests for DockerCodeRunner's pure helpers, independent of a live
// Docker daemon (see RunAsync's own remarks on why the actual `docker run` invocation
// isn't unit-tested — same rationale Judge0ClientTests used for Judge0's live HTTP calls).
public class DockerCodeRunnerTests
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
        Assert.True(DockerCodeRunner.IsKnownLanguage(language), language);
    }

    public static IEnumerable<object[]> AllFrontendLanguages() =>
        FrontendLanguages.Select(l => new object[] { l });

    [Fact]
    public void IsKnownLanguage_RejectsAForeignLanguage()
    {
        Assert.False(DockerCodeRunner.IsKnownLanguage("brainfuck"));
    }

    [Fact]
    public void BuildDockerArgs_DisablesNetworkAccess()
    {
        var args = DockerCodeRunner.BuildDockerArgs("python:3.12-slim", @"C:\tmp\x", "python main.py", 10);

        Assert.Contains("--network", args);
        Assert.Equal("none", args[Array.IndexOf(args, "--network") + 1]);
    }

    [Fact]
    public void BuildDockerArgs_SetsResourceCeilings()
    {
        var args = DockerCodeRunner.BuildDockerArgs("python:3.12-slim", @"C:\tmp\x", "python main.py", 10);

        Assert.Contains("--memory", args);
        Assert.Contains("--pids-limit", args);
        Assert.Contains("--cpus", args);
    }

    [Fact]
    public void BuildDockerArgs_AutoRemovesContainerAndMountsWorkDir()
    {
        var args = DockerCodeRunner.BuildDockerArgs("python:3.12-slim", @"C:\tmp\workdir", "python main.py", 10);

        Assert.Contains("--rm", args);
        Assert.Contains("-v", args);
        Assert.Equal(@"C:\tmp\workdir:/box", args[Array.IndexOf(args, "-v") + 1]);
        Assert.Equal("python:3.12-slim", args[Array.IndexOf(args, "-w") + 2]);
    }

    [Fact]
    public void BuildDockerArgs_WrapsCommandInAnInContainerTimeout()
    {
        var args = DockerCodeRunner.BuildDockerArgs("python:3.12-slim", @"C:\tmp\x", "python main.py", 7);

        Assert.Equal("timeout 7s python main.py", args[^1]);
    }

    [Fact]
    public void StemFromEntryPath_StripsExtension()
    {
        Assert.Equal("main", DockerCodeRunner.StemFromEntryPath("main.py"));
    }

    [Fact]
    public void StemFromEntryPath_UsesBasenameForNestedPaths()
    {
        Assert.Equal("Main", DockerCodeRunner.StemFromEntryPath("src/Main.java"));
    }

    [Fact]
    public void ClassNameFromEntryPath_MatchesJavaFilenameConvention()
    {
        // Main.java is the Coding app's own starter-picker default for Java (see
        // campus-shared-editor-kit's defaultFilenameForLanguage) — this must derive
        // "Main" from it since `java Main` is what actually runs the compiled class.
        Assert.Equal("Main", DockerCodeRunner.ClassNameFromEntryPath("Main.java"));
    }
}
