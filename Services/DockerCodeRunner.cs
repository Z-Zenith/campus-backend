using System.Diagnostics;
using BackendApi.Contracts;

namespace BackendApi.Services;

// SEK-01: executes each submission inside its own throwaway Docker container instead of
// proxying to Judge0 (see git history for Judge0Client, removed). Judge0's isolate sandbox
// requires cgroup v1 — this dev environment's Docker Desktop/WSL2 kernel refuses to provide
// it (confirmed: forcing systemd.unified_cgroup_hierarchy=0 gets accepted onto the kernel
// command line but /sys/fs/cgroup still mounts as cgroup2), and Judge0's own upstream docs
// confirm cgroup v1 is a hard requirement of isolate itself, not a version-specific bug — so
// no Judge0 image swap would have fixed it either. Docker/containerd's own container
// isolation already works fine under cgroup v2 on this machine (every other container in
// this stack proves that), so this runner uses `docker run` as the sandbox instead of
// isolate: shells out to the `docker` CLI directly rather than adding an HTTP client or a
// Docker Engine API NuGet dependency, matching this codebase's existing preference for
// simple, dependency-free approaches (e.g. bespoke test fakes over a mocking library).
//
// Known limitation: this assumes `docker` is reachable from wherever backend-api's process
// runs (true for the bare `dotnet run` dev setup this app currently runs in). If
// backend-api is ever deployed as its own container again (docker-compose.yml still has
// that service), it would need the host's Docker socket bind-mounted in to keep working —
// a real Docker-outside-of-Docker tradeoff (host-root-equivalent access) worth a deliberate
// decision when that day comes, not something to silently wire up here.
public sealed class DockerCodeRunner(ILogger<DockerCodeRunner> logger) : ICodeRunner
{
    private const string RunFileName = "__run";

    // Languages with no "execute a program" semantics — never actually executed by Judge0
    // either (mapped to its Plain Text no-op runner, which echoes stdin with no compile/exec
    // step). Kept as the exact same limitation here rather than inventing new scope.
    private static readonly HashSet<string> NoExecLanguages = ["html", "css", "json", "yaml"];

    private sealed record LanguageSpec(
        string Image,
        Func<string, string>? Compile,
        Func<string, string> Run,
        int RunTimeoutSeconds = 10,
        int CompileTimeoutSeconds = 15,
        int MemoryMb = 256);

    private static readonly Dictionary<string, LanguageSpec> Languages = new()
    {
        ["python"] = new("python:3.12-slim", Compile: null, Run: entry => $"python3 {Quote(entry)}"),
        ["c"] = new("gcc:13",
            Compile: entry => $"gcc -O2 -o {RunFileName} {Quote(entry)}",
            Run: _ => $"./{RunFileName}"),
        ["cpp"] = new("gcc:13",
            Compile: entry => $"g++ -O2 -o {RunFileName} {Quote(entry)}",
            Run: _ => $"./{RunFileName}"),
        ["java"] = new("eclipse-temurin:21-jdk",
            // Flat single-directory compile, matching the picker's own Main.java starter
            // template — same simple-submission assumption Judge0's flattened sandbox made.
            Compile: _ => "javac *.java",
            Run: entry => $"java {ClassNameFromEntryPath(entry)}"),
        ["javascript"] = new("node:20-slim", Compile: null, Run: entry => $"node {Quote(entry)}"),
        ["nodejs"] = new("node:20-slim", Compile: null, Run: entry => $"node {Quote(entry)}"),
        // Needs typescript pre-installed since submissions run with --network none (no npm
        // install at run time) — see docker/ts-runner.Dockerfile, built once during setup.
        ["typescript"] = new("campus-ts-runner:local",
            Compile: entry => $"tsc {Quote(entry)}",
            Run: entry => $"node {Quote(StemFromEntryPath(entry) + ".js")}"),
        // No separate compile step: `dotnet run` builds and runs in one command, so a build
        // failure and a runtime exception both surface as a non-zero exit here. Classified
        // via the "error CS" marker .NET's compiler prints on build errors (see
        // ClassifyDotnetFailure) rather than a real compile/run split, given this is the
        // lowest-priority/slowest language in the launch list (SDK restore overhead) — a
        // deliberate simplification, not an oversight.
        ["dotnet"] = new("mcr.microsoft.com/dotnet/sdk:8.0",
            Compile: null,
            // Back up the entry file's content before scaffolding: `dotnet new console
            // --force` overwrites whatever's at ./Program.cs with its own template, which
            // silently clobbers the student's actual code when the entry file is *already*
            // named Program.cs — the picker's own default filename for dotnet (see
            // defaultFilenameForLanguage), so this is the common case, not an edge case.
            Run: entry => $"cp {Quote(entry)} /tmp/__entry.cs && dotnet new console -o . --force >/dev/null 2>&1 && cp /tmp/__entry.cs Program.cs && dotnet run",
            RunTimeoutSeconds: 30),
        ["sql"] = new("keinos/sqlite3:latest", Compile: null, Run: entry => $"sqlite3 :memory: < {Quote(entry)}"),
        ["go"] = new("golang:1.22-alpine", Compile: null, Run: entry => $"go run {Quote(entry)}"),
        ["rust"] = new("rust:1-slim",
            Compile: entry => $"rustc -O -o {RunFileName} {Quote(entry)}",
            Run: _ => $"./{RunFileName}",
            RunTimeoutSeconds: 15),
        ["ruby"] = new("ruby:3-slim", Compile: null, Run: entry => $"ruby {Quote(entry)}"),
        ["php"] = new("php:8-cli", Compile: null, Run: entry => $"php {Quote(entry)}"),
        // Needs kotlinc pre-installed, same rationale as typescript above — see
        // docker/kotlin-runner.Dockerfile. No separate compile step, same reason as
        // dotnet above (see its comment) plus one more: `kotlinc -include-runtime`
        // merging the Kotlin stdlib into the output jar hits a severe Docker-Desktop-
        // on-Windows bind-mount I/O pathology when the jar is written to the
        // workspace mount (/box) — confirmed by direct measurement to hang past 120s
        // there vs. ~3.7s writing to the container's own /tmp. So compile+run both
        // happen in /tmp, inside one container's lifetime, never touching /box for
        // the jar. Classified via the "error:" marker kotlinc prints on compile
        // failures (see ClassifyRunResult), same idea as dotnet's "error CS".
        // kotlinc is itself a JVM app compiling+linking against another JVM's stdlib jar, on
        // top of the java runtime actually executing the result — measured hitting a SIGKILL
        // (exit 137, OOM) under the other languages' shared 256m ceiling on a trivial
        // hello-world, so it gets a higher one here rather than raising it for everyone.
        ["kotlin"] = new("campus-kotlin-runner:local",
            Compile: null,
            Run: entry => $"cp {Quote(entry)} /tmp/main.kt && kotlinc /tmp/main.kt -include-runtime -d /tmp/{RunFileName}.jar && java -jar /tmp/{RunFileName}.jar",
            RunTimeoutSeconds: 30,
            CompileTimeoutSeconds: 45,
            MemoryMb: 768),
        ["shell"] = new("bash:5", Compile: null, Run: entry => $"bash {Quote(entry)}"),
    };

    public async Task<CodeRunResultDto> RunAsync(
        string entryFilePath, IReadOnlyList<CodeFileDto> files, string? stdin, CancellationToken ct = default)
    {
        var entryFile = files.FirstOrDefault(f => f.Path == entryFilePath)
            ?? throw new InvalidOperationException($"Entry file '{entryFilePath}' is not one of the submitted files.");

        // Defense in depth: SEK's validateProject already rejects an unsupported language
        // client-side, but every file's language is re-validated here too since this is the
        // layer that actually owns the Language -> execution-image mapping.
        foreach (var file in files)
        {
            if (!NoExecLanguages.Contains(file.Language) && !Languages.ContainsKey(file.Language))
            {
                throw new UnsupportedLanguageException(file.Language);
            }
        }

        if (NoExecLanguages.Contains(entryFile.Language))
        {
            return new CodeRunResultDto(stdin ?? "", "", 0, 0, TimedOut: false, Status: "accepted");
        }

        if (!Languages.TryGetValue(entryFile.Language, out var spec))
        {
            throw new UnsupportedLanguageException(entryFile.Language);
        }

        var workDir = CreateWorkDir();
        try
        {
            WriteFiles(workDir, files);
            var stopwatch = Stopwatch.StartNew();

            if (spec.Compile is not null)
            {
                var compileResult = await RunContainerAsync(spec.Image, workDir, spec.Compile(entryFilePath), stdin: null, spec.CompileTimeoutSeconds, spec.MemoryMb, ct);
                if (compileResult.ExitCode != 0 || compileResult.TimedOut)
                {
                    return new CodeRunResultDto("", compileResult.Stderr, compileResult.ExitCode,
                        (long)stopwatch.Elapsed.TotalMilliseconds, compileResult.TimedOut,
                        compileResult.TimedOut ? "time_limit_exceeded" : "compilation_error");
                }
            }

            var runResult = await RunContainerAsync(spec.Image, workDir, spec.Run(entryFilePath), stdin, spec.RunTimeoutSeconds, spec.MemoryMb, ct);
            var status = ClassifyRunResult(entryFile.Language, spec, runResult);
            return new CodeRunResultDto(runResult.Stdout, runResult.Stderr, runResult.ExitCode,
                (long)stopwatch.Elapsed.TotalMilliseconds, runResult.TimedOut, status);
        }
        catch (Exception ex) when (ex is not UnsupportedLanguageException)
        {
            logger.LogError(ex, "Docker code execution failed for language {Language}", entryFile.Language);
            // Wrapped as HttpRequestException so CodeExecutionController's existing 503
            // mapping (originally written for a Judge0-unreachable failure) keeps handling
            // "the execution backend itself is broken" without needing a new catch clause.
            throw new HttpRequestException("The Code Execution Service is unreachable. Try again shortly.", ex);
        }
        finally
        {
            TryDeleteWorkDir(workDir);
        }
    }

    private static string? ClassifyRunResult(string language, LanguageSpec spec, ContainerResult result)
    {
        if (result.TimedOut)
        {
            return "time_limit_exceeded";
        }
        if (result.ExitCode == 0)
        {
            return "accepted";
        }
        // dotnet and kotlin have no separate compile step (see Languages table comments) —
        // distinguish a build failure from a runtime exception via each compiler's own
        // error marker ("error CS..." / "error:").
        if (language == "dotnet" && spec.Compile is null && result.Stderr.Contains("error CS", StringComparison.Ordinal))
        {
            return "compilation_error";
        }
        if (language == "kotlin" && spec.Compile is null && result.Stderr.Contains("error:", StringComparison.Ordinal))
        {
            return "compilation_error";
        }
        return "runtime_error";
    }

    // internal rather than private: TerminalSessionService reuses these three for the
    // same "materialize a CodeProject onto disk for a container mount" need, rather than
    // duplicating them.
    internal static string CreateWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "campus-coderun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void TryDeleteWorkDir(string workDir)
    {
        try
        {
            Directory.Delete(workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup — a lingering temp dir from a rare failure to delete isn't
            // worth failing the request over.
        }
    }

    // Writes every file preserving its real relative path (creating subdirectories as
    // needed) rather than flattening to basenames the way Judge0's zip-upload API required —
    // simpler and more correct: C's #include and Java sibling classes work the same way they
    // would on a student's own machine. Rejects path traversal / absolute paths defensively;
    // SEK's own CodeFile.path is embedder-controlled today, but this is the layer that
    // actually touches the host filesystem.
    internal static void WriteFiles(string workDir, IReadOnlyList<CodeFileDto> files)
    {
        foreach (var file in files)
        {
            if (file.Path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(file.Path))
            {
                throw new InvalidOperationException($"Invalid file path '{file.Path}'.");
            }

            var fullPath = Path.Combine(workDir, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, file.Content);
        }
    }

    private sealed record ContainerResult(string Stdout, string Stderr, int ExitCode, bool TimedOut);

    private static async Task<ContainerResult> RunContainerAsync(
        string image, string workDir, string command, string? stdin, int timeoutSeconds, int memoryMb, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildDockerArgs(image, workDir, command, timeoutSeconds, memoryMb))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start docker process.");

        if (!string.IsNullOrEmpty(stdin))
        {
            await process.StandardInput.WriteAsync(stdin);
        }
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // Outer safety-margin timeout on top of the in-container `timeout` command (the
        // primary enforcement — it runs inside the sandboxed process tree and reliably kills
        // runaway submissions). This backstop only fires if the docker CLI itself hangs
        // (e.g. daemon unresponsive), not for a normal submission timeout.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds + 10));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new ContainerResult("", "", -1, TimedOut: true);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        // The in-container `timeout` wrapper exits 124 when it had to kill the command.
        var timedOut = process.ExitCode == 124;
        return new ContainerResult(stdout, stderr, process.ExitCode, timedOut);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the timeout firing and Kill being called.
        }
    }

    // Every SEK-01 Language the frontend can produce (see campus-shared-editor-kit's
    // types.ts) must resolve one way or another — either a real Languages entry or
    // NoExecLanguages — so a newly-added frontend language can't silently become
    // "unsupported" backend-side without a test catching it.
    public static bool IsKnownLanguage(string language) => Languages.ContainsKey(language) || NoExecLanguages.Contains(language);

    // Pure and unit-testable on their own — the actual `docker` invocation above is the
    // integration seam these deliberately stay separate from, mirroring how
    // Judge0ClientTests only ever unit-tested BuildAdditionalFilesZip (a pure helper), never
    // the live HTTP calls.
    public static string[] BuildDockerArgs(string image, string hostWorkDir, string command, int timeoutSeconds, int memoryMb = 256) =>
    [
        "run", "--rm", "-i",
        "--network", "none",
        "--memory", $"{memoryMb}m",
        "--memory-swap", $"{memoryMb}m",
        // 64 was enough for every original language, but Go's toolchain forks several
        // multi-threaded build/link/cache subprocesses even for `go run` on a trivial
        // program — each OS thread counts against the cgroup pids controller too, so
        // 64 was observed hitting "failed to create new OS thread (errno=11)" on a
        // plain hello-world. 256 gives real headroom without meaningfully loosening
        // the fork-bomb ceiling this limit exists for.
        "--pids-limit", "256",
        "--cpus", "1.0",
        "-v", $"{hostWorkDir}:/box",
        "-w", "/box",
        image,
        "sh", "-c", $"timeout {timeoutSeconds}s {command}",
    ];

    public static string ClassNameFromEntryPath(string entryPath) => StemFromEntryPath(entryPath);

    public static string StemFromEntryPath(string entryPath) =>
        Path.GetFileNameWithoutExtension(entryPath.Split('/').Last());

    // Minimal shell-safe single-quoting for filenames embedded in a `sh -c "..."` command —
    // filenames come from the picker/student input, not literal shell code, but this is the
    // layer that builds the command string, so it defends against spaces/metacharacters in a
    // typed filename regardless of what the frontend already constrains.
    private static string Quote(string path) => $"'{path.Replace("'", "'\\''")}'";
}
