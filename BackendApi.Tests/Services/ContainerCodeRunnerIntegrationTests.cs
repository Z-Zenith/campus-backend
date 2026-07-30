using BackendApi.Contracts;
using BackendApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Services;

// SEK-01: real end-to-end coverage for ContainerCodeRunner.RunAsync — every other test in this
// suite (ContainerCodeRunnerTests, CodeExecutionControllerTests) deliberately stays off the live
// `docker run` path, so nothing previously exercised any language's actual Compile/Run command
// against a real Docker daemon. These tests do exactly that: one real submission per language,
// asserting it actually compiles and executes and produces the expected output — not just that
// the container-args shape looks right.
//
// Gated behind RUN_DOCKER_INTEGRATION_TESTS=1 (see DockerTheoryAttribute below) so a normal
// `dotnet test` without a Docker daemon still passes/skips cleanly, matching this suite's
// existing choice not to require one by default. Runs specifically against Docker (not the
// runtime-detection path) since that's what CI/dev machines running this suite are assumed to
// have — Podman-specific behavior is 0.5's own port-forwarding/networking spike's concern, not
// this suite's. Run with:
//   RUN_DOCKER_INTEGRATION_TESTS=1 dotnet test --filter FullyQualifiedName~ContainerCodeRunnerIntegrationTests
// after `docker/setup-code-images.sh` has pulled/built every image these languages need.
public class ContainerCodeRunnerIntegrationTests
{
    private static ContainerCodeRunner NewRunner() => new(NullLogger<ContainerCodeRunner>.Instance, new DockerCli());

    [DockerTheory]
    [MemberData(nameof(ExecutableLanguageCases))]
    public async Task RunAsync_CompilesAndExecutes_ForEveryExecutableLanguage(
        string language, string fileName, string content, string expectedStdout)
    {
        var runner = NewRunner();
        var files = new List<CodeFileDto> { new(fileName, language, content) };

        var result = await runner.RunAsync(fileName, files, stdin: null);

        Assert.Equal("accepted", result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(expectedStdout, result.Stdout);
    }

    public static IEnumerable<object[]> ExecutableLanguageCases()
    {
        yield return ["python", "main.py", "print(\"hello-python\")", "hello-python"];
        yield return ["c", "main.c", "#include <stdio.h>\nint main(){printf(\"hello-c\\n\");return 0;}", "hello-c"];
        yield return ["cpp", "main.cpp", "#include <iostream>\nint main(){std::cout<<\"hello-cpp\"<<std::endl;return 0;}", "hello-cpp"];
        yield return ["java", "Main.java", "public class Main{public static void main(String[] a){System.out.println(\"hello-java\");}}", "hello-java"];
        yield return ["javascript", "main.js", "console.log(\"hello-javascript\")", "hello-javascript"];
        yield return ["nodejs", "main.js", "console.log(\"hello-nodejs\")", "hello-nodejs"];
        yield return ["typescript", "main.ts", "const msg: string = \"hello-typescript\";\nconsole.log(msg);", "hello-typescript"];
        yield return ["dotnet", "Program.cs", "Console.WriteLine(\"hello-dotnet\");", "hello-dotnet"];
        yield return ["sql", "main.sql", "SELECT 'hello-sql';", "hello-sql"];
        yield return ["go", "main.go", "package main\nimport \"fmt\"\nfunc main(){fmt.Println(\"hello-go\")}", "hello-go"];
        yield return ["rust", "main.rs", "fn main(){println!(\"hello-rust\");}", "hello-rust"];
        yield return ["ruby", "main.rb", "puts \"hello-ruby\"", "hello-ruby"];
        yield return ["php", "main.php", "<?php echo \"hello-php\\n\";", "hello-php"];
        yield return ["kotlin", "main.kt", "fun main(){println(\"hello-kotlin\")}", "hello-kotlin"];
        yield return ["shell", "main.sh", "echo hello-shell", "hello-shell"];
    }

    // dotnet/kotlin have no separate compile step (see DockerCodeRunner.Languages' comments) —
    // a real build failure must still classify as compilation_error, not runtime_error, via
    // each compiler's own stderr marker.
    [DockerFact]
    public async Task RunAsync_ClassifiesADotnetBuildFailure_AsCompilationError()
    {
        var runner = NewRunner();
        var files = new List<CodeFileDto> { new("Program.cs", "dotnet", "this is not valid C#;;;") };

        var result = await runner.RunAsync("Program.cs", files, stdin: null);

        Assert.Equal("compilation_error", result.Status);
        Assert.NotEqual(0, result.ExitCode);
    }

    [DockerFact]
    public async Task RunAsync_ClassifiesAKotlinBuildFailure_AsCompilationError()
    {
        var runner = NewRunner();
        var files = new List<CodeFileDto> { new("main.kt", "kotlin", "this is not valid kotlin @@@") };

        var result = await runner.RunAsync("main.kt", files, stdin: null);

        Assert.Equal("compilation_error", result.Status);
        Assert.NotEqual(0, result.ExitCode);
    }

    // A genuine runtime exception (not a build failure) must classify as runtime_error.
    [DockerFact]
    public async Task RunAsync_ClassifiesAPythonRuntimeException_AsRuntimeError()
    {
        var runner = NewRunner();
        var files = new List<CodeFileDto> { new("main.py", "python", "raise ValueError('boom')") };

        var result = await runner.RunAsync("main.py", files, stdin: null);

        Assert.Equal("runtime_error", result.Status);
        Assert.NotEqual(0, result.ExitCode);
    }

    // A genuine compile error (real Compile step, not the dotnet/kotlin no-compile-step
    // special case) must classify as compilation_error too.
    [DockerFact]
    public async Task RunAsync_ClassifiesACCompileFailure_AsCompilationError()
    {
        var runner = NewRunner();
        var files = new List<CodeFileDto> { new("main.c", "c", "this is not valid C") };

        var result = await runner.RunAsync("main.c", files, stdin: null);

        Assert.Equal("compilation_error", result.Status);
    }

    [DockerFact]
    public async Task RunAsync_PipesStdinThroughToTheProgram()
    {
        var runner = NewRunner();
        var files = new List<CodeFileDto> { new("main.py", "python", "print(input())") };

        var result = await runner.RunAsync("main.py", files, stdin: "hello-stdin");

        Assert.Equal("accepted", result.Status);
        Assert.Contains("hello-stdin", result.Stdout);
    }

    // No-exec languages (SEK-01's HTML/CSS/JSON/YAML) never touch Docker at all — verified
    // here alongside the real Docker-backed cases since this file is the natural home for
    // "every language SEK can produce actually behaves as expected end-to-end."
    [DockerTheory]
    [InlineData("html")]
    [InlineData("css")]
    [InlineData("json")]
    [InlineData("yaml")]
    public async Task RunAsync_NoExecLanguages_EchoStdinBackAsAcceptedWithNoContainer(string language)
    {
        var runner = NewRunner();
        var files = new List<CodeFileDto> { new("main." + language, language, "irrelevant content") };

        var result = await runner.RunAsync("main." + language, files, stdin: "echoed-back");

        Assert.Equal("accepted", result.Status);
        Assert.Equal("echoed-back", result.Stdout);
        Assert.Equal(0, result.ExitCode);
    }
}

// Runtime-conditional [Fact]/[Theory]: xUnit has no built-in env-var-gated skip and this repo
// avoids adding a new test-framework dependency for a one-off need (matching its existing
// dependency-free-fakes-over-a-mocking-library preference) — a Skip-setting subclass is the
// standard lightweight way to do this in plain xUnit.
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_DOCKER_INTEGRATION_TESTS") != "1")
        {
            Skip = "Set RUN_DOCKER_INTEGRATION_TESTS=1 to run (needs a local Docker daemon and " +
                   "every SEK-01 image built via docker/setup-code-images.sh).";
        }
    }
}

public sealed class DockerTheoryAttribute : TheoryAttribute
{
    public DockerTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_DOCKER_INTEGRATION_TESTS") != "1")
        {
            Skip = "Set RUN_DOCKER_INTEGRATION_TESTS=1 to run (needs a local Docker daemon and " +
                   "every SEK-01 image built via docker/setup-code-images.sh).";
        }
    }
}
