using System.Collections.Concurrent;
using System.Diagnostics;
using BackendApi.Contracts;

namespace BackendApi.Services;

// SEK-01 integrated terminal. Deliberately request/response command execution against a
// persistent, workspace-mounted container — not a full pty-backed live shell. A true
// streaming terminal (arrow-key line editing, colors from interactive readline,
// full-screen apps like vim/htop) needs real pty allocation and a continuous
// bidirectional transport, which this app's WebView-never-talks-to-the-network-directly
// architecture (everything proxies through CodeBridge/ApiClient via postMessage) makes a
// much larger, riskier build than the actual value a student needs here: running
// compilers, git, ls/cd, one-off scripts. Matches OutputPanel.tsx's own "NOT a live
// interactive terminal" scope line for Run, applied the same way here.
//
// Also --network none, same as DockerCodeRunner's Run: a persistent, network-enabled
// shell is a materially bigger security surface (arbitrary outbound connections,
// git clone/pip install/curl all becoming exfiltration or supply-chain vectors) than a
// throwaway one-shot sandboxed Run. Network access would be a separate, explicit,
// reviewed decision, not something to bundle in silently.
public sealed class TerminalSessionService(ILogger<TerminalSessionService> logger)
{
    private const string Image = "campus-dev-terminal:local";
    private const int ExecTimeoutSeconds = 30;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(20);

    private sealed class Session
    {
        public required string ContainerId { get; init; }
        public required string WorkspaceDir { get; init; }
        public DateTime LastActiveUtc { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public async Task<Guid> StartAsync(IReadOnlyList<CodeFileDto> files, CancellationToken ct = default)
    {
        var workDir = DockerCodeRunner.CreateWorkDir();
        DockerCodeRunner.WriteFiles(workDir, files);

        var containerId = await RunAsync(
            [
                "run", "-d",
                "--network", "none",
                "--memory", "512m",
                "--memory-swap", "512m",
                "--pids-limit", "128",
                "--cpus", "1.0",
                "-v", $"{workDir}:/box",
                "-w", "/box",
                Image,
                "tail", "-f", "/dev/null",
            ], ct);

        var sessionId = Guid.NewGuid();
        _sessions[sessionId] = new Session
        {
            ContainerId = containerId.Stdout.Trim(),
            WorkspaceDir = workDir,
            LastActiveUtc = DateTime.UtcNow,
        };
        return sessionId;
    }

    public async Task<TerminalExecResultDto> ExecAsync(Guid sessionId, string command, CancellationToken ct = default)
    {
        var session = _sessions.GetValueOrDefault(sessionId) ?? throw new TerminalSessionNotFoundException(sessionId);
        session.LastActiveUtc = DateTime.UtcNow;

        // Same in-container timeout wrapper Run already uses — a runaway command (e.g. an
        // accidental infinite loop) can't hang the session forever, only that one exec call.
        var result = await RunAsync(
            ["exec", session.ContainerId, "sh", "-c", $"timeout {ExecTimeoutSeconds}s {command}"], ct);
        return new TerminalExecResultDto(result.Stdout, result.Stderr, result.ExitCode);
    }

    public async Task CloseAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }
        await CloseSessionAsync(session, ct);
    }

    // Called by TerminalSessionReaperHostedService — sweeps sessions idle past
    // IdleTimeout so an abandoned Coding tab doesn't leak containers/workspace dirs
    // forever.
    public async Task ReapIdleSessionsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        var idleSessionIds = _sessions.Where(kv => kv.Value.LastActiveUtc < cutoff).Select(kv => kv.Key).ToList();

        foreach (var sessionId in idleSessionIds)
        {
            if (!_sessions.TryRemove(sessionId, out var session))
            {
                continue;
            }
            logger.LogInformation("Reaping idle terminal session {SessionId}", sessionId);
            await CloseSessionAsync(session, ct);
        }
    }

    private async Task CloseSessionAsync(Session session, CancellationToken ct)
    {
        try
        {
            await RunAsync(["rm", "-f", session.ContainerId], ct);
        }
        catch (Exception ex)
        {
            // Best-effort: an already-gone container (e.g. OOM-killed by the daemon) isn't
            // worth failing the close/reap over.
            logger.LogWarning(ex, "Failed to remove terminal container {ContainerId}", session.ContainerId);
        }
        DockerCodeRunner.TryDeleteWorkDir(session.WorkspaceDir);
    }

    private sealed record ProcessResult(string Stdout, string Stderr, int ExitCode);

    private static async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start docker process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new ProcessResult(await stdoutTask, await stderrTask, process.ExitCode);
    }
}

public sealed class TerminalSessionNotFoundException(Guid sessionId)
    : Exception($"Terminal session '{sessionId}' does not exist or has expired.");
