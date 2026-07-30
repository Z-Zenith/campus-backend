using System.Diagnostics;
using BackendApi.Contracts;

namespace BackendApi.Services;

// SEK-01: executes each submission inside its own throwaway container instead of proxying to
// Judge0 (see git history for Judge0Client, removed). Judge0's isolate sandbox requires
// cgroup v1 — this dev environment's Docker Desktop/WSL2 kernel refuses to provide it
// (confirmed: forcing systemd.unified_cgroup_hierarchy=0 gets accepted onto the kernel command
// line but /sys/fs/cgroup still mounts as cgroup2), and Judge0's own upstream docs confirm
// cgroup v1 is a hard requirement of isolate itself, not a version-specific bug — so no Judge0
// image swap would have fixed it either. Docker/containerd's own container isolation already
// works fine under cgroup v2 on this machine (every other container in this stack proves that),
// so this runner uses `<runtime> run` as the sandbox instead of isolate: shells out to the CLI
// directly rather than adding an HTTP client or an engine API NuGet dependency, matching this
// codebase's existing preference for simple, dependency-free approaches (e.g. bespoke test
// fakes over a mocking library).
//
// Runtime-agnostic (was DockerCodeRunner, hardcoded to `docker` — renamed once it also runs
// Podman, since keeping the old name would actively mislead the next reader): which CLI to
// shell out to is decided once at startup by ContainerRuntimeDetector and injected as
// IContainerCli, since Podman is drop-in compatible with every flag used here. See
// IContainerCli's doc comment for what happens when neither runtime is available.
//
// Known limitation: this assumes the container CLI is reachable from wherever backend-api's
// process runs (true for the bare `dotnet run` dev setup this app currently runs in). If
// backend-api is ever deployed as its own container again (docker-compose.yml still has that
// service), it would need the host's runtime socket bind-mounted in to keep working — a real
// Docker/Podman-outside-of-itself tradeoff (host-root-equivalent access) worth a deliberate
// decision when that day comes, not something to silently wire up here.
public sealed class ContainerCodeRunner(ILogger<ContainerCodeRunner> logger, IContainerCli containerCli) : ICodeRunner
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
            Run: entry => $"node {Quote(Path.ChangeExtension(entry, ".js"))}"),
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
            // The original entry file must also be removed before scaffolding: when it's
            // named anything other than Program.cs, it otherwise survives alongside the
            // freshly-written Program.cs, and the SDK-style project's implicit `**/*.cs`
            // glob compiles both — two files with the same top-level-statement entry point,
            // a duplicate-entry-point build failure (CS8802-style).
            Run: entry => $"cp {Quote(entry)} /tmp/__entry.cs && rm -f {Quote(entry)} && dotnet new console -o . --force >/dev/null 2>&1 && cp /tmp/__entry.cs Program.cs && dotnet run",
            RunTimeoutSeconds: 30),
        // `.read` (a sqlite3 dot-command passed as a single arg) rather than `< entry` shell
        // redirection: redirection would occupy sqlite3's own stdin, silently discarding the
        // caller-supplied `stdin` that RunContainerAsync pipes in for every other language.
        ["sql"] = new("keinos/sqlite3:latest", Compile: null, Run: entry => $"sqlite3 :memory: {Quote(".read " + entry)}"),
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

    // B2 persistent-server live preview (SDA/SEK plan): only Node.js and Python have a
    // real "start a long-lived server" mode — every other language in Languages above is
    // a one-shot batch Run. Per 0.4's spike, the host's published port must reach a real
    // (non-`--network none`) network, so this mode has genuine outbound network access
    // unlike every other execution path here — a deliberate, flagged exception (see
    // StartPersistentAsync's own doc comment), not a silent gap.
    private static readonly Dictionary<string, string> PersistentServerImages = new()
    {
        ["nodejs"] = "node:20-slim",
        ["javascript"] = "node:20-slim",
        ["python"] = "python:3.12-slim",
    };

    // Heroku-style convention: the student's server code is expected to read the PORT
    // env var and bind 0.0.0.0:$PORT (e.g. Express's `app.listen(process.env.PORT)`,
    // Flask's `app.run(host='0.0.0.0', port=int(os.environ['PORT']))`). A fixed,
    // well-known container-side port rather than trying to detect whatever port the
    // student's code happens to bind — code that hardcodes a different port won't be
    // reachable through this preview; documented as a real, known limitation of this
    // convention, not silently swallowed.
    private const int PersistentServerContainerPort = 3000;

    public sealed record PersistentRunHandle(string ContainerId, int HostPort, bool IsReady);

    public async Task<PersistentRunHandle> StartPersistentAsync(
        string entryFilePath, IReadOnlyList<CodeFileDto> files, int hostPort, CancellationToken ct = default)
    {
        var entryFile = files.FirstOrDefault(f => f.Path == entryFilePath)
            ?? throw new InvalidOperationException($"Entry file '{entryFilePath}' is not one of the submitted files.");
        if (!PersistentServerImages.TryGetValue(entryFile.Language, out var image))
        {
            throw new UnsupportedLanguageException(entryFile.Language);
        }
        if (!containerCli.IsAvailable)
        {
            throw new HttpRequestException("The Code Execution Service is unreachable. Try again shortly.");
        }

        var runCommand = entryFile.Language switch
        {
            "python" => $"python3 {Quote(entryFilePath)}",
            _ => $"node {Quote(entryFilePath)}",
        };

        var workDir = CreateWorkDir();
        WriteFiles(workDir, files);

        var psi = new ProcessStartInfo(containerCli.ExecutableName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Deliberately NOT --network none (see the class-level doc comment on
        // PersistentServerImages) — this container needs real network I/O to be
        // reachable via -p, which --network none/an --internal bridge cannot support
        // (confirmed empirically in the 0.4 spike). Otherwise the same resource ceilings
        // every other execution path here uses.
        foreach (var arg in new[]
        {
            "run", "-d",
            "--memory", "256m", "--memory-swap", "256m",
            "--pids-limit", "256", "--cpus", "1.0",
            "-e", $"PORT={PersistentServerContainerPort}",
            "-p", $"127.0.0.1:{hostPort}:{PersistentServerContainerPort}",
            "-v", $"{ToHostVisiblePath(workDir)}:/box",
            "-w", "/box",
            image, "sh", "-c", runCommand,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {containerCli.ExecutableName} process.");
        var containerId = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            TryDeleteWorkDir(workDir);
            throw new HttpRequestException($"The Code Execution Service is unreachable. Try again shortly. ({stderr.Trim()})");
        }

        var isReady = await WaitForPortReadyAsync(hostPort, TimeSpan.FromSeconds(15), ct);
        return new PersistentRunHandle(containerId, hostPort, isReady);
    }

    public async Task StopPersistentAsync(string containerId, string workDir, CancellationToken ct = default)
    {
        if (!containerCli.IsAvailable)
        {
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(containerCli.ExecutableName) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("rm");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(containerId);
            using var process = Process.Start(psi);
            if (process is not null)
            {
                await process.WaitForExitAsync(ct);
            }
        }
        catch (InvalidOperationException)
        {
            // Best-effort — an already-gone container isn't worth failing the stop over,
            // matching TerminalSessionService.CloseSessionAsync's own convention.
        }
        TryDeleteWorkDir(workDir);
    }

    // Polls with a real TCP connect attempt rather than trusting "container is running" —
    // a Node/Flask server can be up (process started) seconds before it actually binds
    // its port, and this is what StartPersistentAsync's caller actually needs to know
    // before handing a previewUrl to a student.
    private static async Task<bool> WaitForPortReadyAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);
                return true;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
            {
                if (cts.IsCancellationRequested)
                {
                    return false;
                }
                await Task.Delay(300, ct);
            }
        }
        return false;
    }

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

        // Fail fast, before touching disk, when no local runtime is available at all —
        // same exception type/message a genuine mid-run failure below produces, so
        // CodeExecutionController's existing mapping handles both identically.
        if (!containerCli.IsAvailable)
        {
            throw new HttpRequestException("The Code Execution Service is unreachable. Try again shortly.");
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
            logger.LogError(ex, "Container code execution failed for language {Language}", entryFile.Language);
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
        // error marker ("error CS..." / "error:"). `dotnet build`/`dotnet run` print
        // compiler diagnostics (including "error CS...") to stdout, not stderr — stderr
        // only gets the generic "The build failed." summary line — so this must check
        // Stdout, confirmed by reproducing a real build failure against the actual image
        // (kotlinc, by contrast, does print its "error:" marker to stderr).
        if (language == "dotnet" && spec.Compile is null && result.Stdout.Contains("error CS", StringComparison.Ordinal))
        {
            return "compilation_error";
        }
        if (language == "kotlin" && spec.Compile is null && result.Stderr.Contains("error:", StringComparison.Ordinal))
        {
            return "compilation_error";
        }
        return "runtime_error";
    }

    // Docker/Podman-outside-of-itself: when backend-api itself runs as a container (see
    // docker-compose.yml's backend-api service), `<runtime> run`/`<runtime> exec` here talk to
    // the HOST's daemon over its bind-mounted socket — which resolves `-v` bind-mount sources
    // against the HOST's filesystem, not this process's own container filesystem. So the
    // workdir this process writes files into (container-local) and the path string handed to
    // `<runtime> run -v` (must be host-visible) are two different things whenever this env var
    // is set. Unset in the bare `dotnet run` dev setup (the default today), where this
    // process's filesystem IS the host filesystem and no translation is needed. See
    // docker-compose.yml's CodeRunner__HostTempDir for the operator-supplied host-side path
    // this pairs with, and ContainerCodeRunRoot below for the fixed container-side mount point
    // it corresponds to.
    private static readonly string? HostTempDirRoot = Environment.GetEnvironmentVariable("CodeRunner__HostTempDir");

    // Fixed container-side mount point for docker-compose.yml's coderun-tmp bind mount — only
    // meaningful (and only where CreateWorkDir writes) when HostTempDirRoot is set.
    private const string ContainerCodeRunRoot = "/coderun-tmp";

    // internal rather than private: TerminalSessionService reuses these three for the
    // same "materialize a CodeProject onto disk for a container mount" need, rather than
    // duplicating them.
    internal static string CreateWorkDir()
    {
        var root = HostTempDirRoot is not null ? ContainerCodeRunRoot : Path.GetTempPath();
        var dir = Path.Combine(root, "campus-coderun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Translates a CreateWorkDir()-produced path to whatever string `<runtime> run -v` needs to
    // see it correctly on the host — itself unchanged when HostTempDirRoot isn't set (see
    // CreateWorkDir's comment). internal rather than private: TerminalSessionService builds
    // its own `<runtime> run -v` args directly (not through BuildContainerArgs) and needs the
    // same translation for its workspace mount.
    internal static string ToHostVisiblePath(string workDir) =>
        HostTempDirRoot is null ? workDir : $"{HostTempDirRoot.TrimEnd('/', '\\')}/{Path.GetFileName(workDir)}";

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

    private async Task<ContainerResult> RunContainerAsync(
        string image, string workDir, string command, string? stdin, int timeoutSeconds, int memoryMb, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(containerCli.ExecutableName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildContainerArgs(image, ToHostVisiblePath(workDir), command, timeoutSeconds, memoryMb))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {containerCli.ExecutableName} process.");

        if (!string.IsNullOrEmpty(stdin))
        {
            await process.StandardInput.WriteAsync(stdin);
        }
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // Outer safety-margin timeout on top of the in-container `timeout` command (the
        // primary enforcement — it runs inside the sandboxed process tree and reliably kills
        // runaway submissions). This backstop only fires if the container CLI itself hangs
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

    // Pure and unit-testable on their own — the actual container invocation above is the
    // integration seam these deliberately stay separate from, mirroring how
    // Judge0ClientTests only ever unit-tested BuildAdditionalFilesZip (a pure helper), never
    // the live HTTP calls. Runtime-agnostic: identical args work for both `docker run` and
    // `podman run` (see IContainerCli's doc comment) — only the executable name differs, and
    // that's supplied separately by the caller, not part of this pure arg list.
    public static string[] BuildContainerArgs(string image, string hostWorkDir, string command, int timeoutSeconds, int memoryMb = 256) =>
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
