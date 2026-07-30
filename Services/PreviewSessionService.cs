using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BackendApi.Contracts;

namespace BackendApi.Services;

// B2 live preview (SDA/SEK plan): backs POST /api/v1/code/run-preview. Two modes, one
// mechanism the desktop client treats identically — a URL it opens as a new tab in its
// own built-in browser (see BrowserViewModel's loopback/reserved-port-range classifier
// exemption, which this service's port range must stay in sync with):
//   - "static": HTML/CSS/JS-only projects. No container at all — it's not executing
//     arbitrary code, just serving real files at their real relative paths (so multi-file
//     projects with relative asset references work, unlike a `srcdoc` iframe). A plain
//     HttpListener, not a second ASP.NET Core host — simpler for "serve a directory,"
//     matching this codebase's preference for dependency-free approaches.
//   - "persistent": Node.js/Python projects that start a real server (ContainerCodeRunner.
//     StartPersistentAsync) — genuinely executes arbitrary code, so it needs the sandboxed
//     container, unlike the static path.
//
// Both session kinds share one reaper (idle timeout) and one reserved host-port range —
// deliberately narrow (not the OS's whole ephemeral range) so the desktop client's
// loopback exemption in the classifier can't be tricked into allowing an arbitrary
// localhost service unrelated to this feature. Keep PortRangeStart/PortRangeEnd here in
// sync with BrowserViewModel's exemption check if either ever changes.
public sealed class PreviewSessionService(ILogger<PreviewSessionService> logger, ContainerCodeRunner containerRunner)
{
    public const int PortRangeStart = 45000;
    public const int PortRangeEnd = 45100;

    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(20);

    public enum PreviewMode { Static, Persistent }

    private abstract class Session
    {
        public required Guid Id { get; init; }
        public required int Port { get; init; }
        public required string WorkDir { get; init; }
        public DateTime LastActiveUtc { get; set; }
    }

    private sealed class StaticSession : Session
    {
        public required HttpListener Listener { get; init; }
    }

    private sealed class PersistentSession : Session
    {
        public required string ContainerId { get; init; }
    }

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly object _portLock = new();

    public async Task<(Guid SessionId, int Port, string Mode, bool IsReady)> StartAsync(
        string entryFilePath, IReadOnlyList<CodeFileDto> files, CancellationToken ct = default)
    {
        var entryFile = files.FirstOrDefault(f => f.Path == entryFilePath)
            ?? throw new InvalidOperationException($"Entry file '{entryFilePath}' is not one of the submitted files.");

        var mode = entryFile.Language switch
        {
            "html" or "css" or "javascript" => PreviewMode.Static,
            "nodejs" or "python" => PreviewMode.Persistent,
            _ => throw new UnsupportedLanguageException(entryFile.Language),
        };

        return mode == PreviewMode.Static
            ? await StartStaticAsync(files, ct)
            : await StartPersistentAsync(entryFilePath, files, ct);
    }

    private async Task<(Guid, int, string, bool)> StartStaticAsync(IReadOnlyList<CodeFileDto> files, CancellationToken ct)
    {
        var workDir = ContainerCodeRunner.CreateWorkDir();
        ContainerCodeRunner.WriteFiles(workDir, files);

        var port = AllocatePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var sessionId = Guid.NewGuid();
        _sessions[sessionId] = new StaticSession
        {
            Id = sessionId,
            Port = port,
            WorkDir = workDir,
            Listener = listener,
            LastActiveUtc = DateTime.UtcNow,
        };

        _ = ServeStaticRequestsAsync(sessionId, listener, workDir);

        await Task.CompletedTask;
        return (sessionId, port, "static", true);
    }

    private async Task<(Guid, int, string, bool)> StartPersistentAsync(string entryFilePath, IReadOnlyList<CodeFileDto> files, CancellationToken ct)
    {
        var port = AllocatePort();
        var handle = await containerRunner.StartPersistentAsync(entryFilePath, files, port, ct);

        var sessionId = Guid.NewGuid();
        _sessions[sessionId] = new PersistentSession
        {
            Id = sessionId,
            Port = port,
            // StartPersistentAsync materializes its own workdir internally and only exposes
            // the container id — this session tracks the container, cleanup happens via
            // StopPersistentAsync(containerId, ...) which needs a workDir string too, but
            // ContainerCodeRunner owns deleting its own temp dir on stop; pass an empty
            // marker here since PreviewSessionService doesn't (and shouldn't) reach into
            // ContainerCodeRunner's internal workdir path.
            WorkDir = "",
            ContainerId = handle.ContainerId,
            LastActiveUtc = DateTime.UtcNow,
        };

        return (sessionId, port, "persistent", handle.IsReady);
    }

    public async Task StopAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }
        await CloseSessionAsync(session, ct);
    }

    // Mirrors TerminalSessionReaperHostedService's pattern exactly — a forgotten preview
    // tab is the same container/resource leak risk as a forgotten terminal session.
    public async Task ReapIdleSessionsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        var idleIds = _sessions.Where(kv => kv.Value.LastActiveUtc < cutoff).Select(kv => kv.Key).ToList();
        foreach (var id in idleIds)
        {
            if (_sessions.TryRemove(id, out var session))
            {
                logger.LogInformation("Reaping idle preview session {SessionId}", id);
                await CloseSessionAsync(session, ct);
            }
        }
    }

    private async Task CloseSessionAsync(Session session, CancellationToken ct)
    {
        switch (session)
        {
            case StaticSession s:
                try { s.Listener.Stop(); s.Listener.Close(); }
                catch (ObjectDisposedException) { /* already gone */ }
                ContainerCodeRunner.TryDeleteWorkDir(s.WorkDir);
                break;
            case PersistentSession p:
                await containerRunner.StopPersistentAsync(p.ContainerId, p.WorkDir, ct);
                break;
        }
    }

    private async Task ServeStaticRequestsAsync(Guid sessionId, HttpListener listener, string rootDir)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return; // listener was stopped (session closed/reaped)
            }

            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.LastActiveUtc = DateTime.UtcNow;
            }

            _ = HandleStaticRequestAsync(context, rootDir);
        }
    }

    private static async Task HandleStaticRequestAsync(HttpListenerContext context, string rootDir)
    {
        try
        {
            var requestedPath = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/");
            var relativePath = requestedPath.TrimStart('/');
            if (string.IsNullOrEmpty(relativePath))
            {
                relativePath = "index.html";
            }

            // Same path-traversal defense as ContainerCodeRunner.WriteFiles — this is the
            // layer that actually touches the host filesystem to read a response back.
            if (relativePath.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            var fullPath = Path.Combine(rootDir, relativePath);
            if (!File.Exists(fullPath))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            context.Response.ContentType = ContentTypeFor(fullPath);
            var bytes = await File.ReadAllBytesAsync(fullPath);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception)
        {
            try { context.Response.StatusCode = 500; context.Response.Close(); }
            catch (ObjectDisposedException) { /* response already closed */ }
        }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };

    // Sequential search within the reserved range rather than letting the OS pick an
    // arbitrary ephemeral port — see the class-level doc comment on why this range must
    // stay narrow and in sync with the desktop client's classifier exemption.
    private int AllocatePort()
    {
        lock (_portLock)
        {
            var usedPorts = _sessions.Values.Select(s => s.Port).ToHashSet();
            for (var port = PortRangeStart; port <= PortRangeEnd; port++)
            {
                if (usedPorts.Contains(port))
                {
                    continue;
                }
                if (IsPortFree(port))
                {
                    return port;
                }
            }
            throw new InvalidOperationException(
                $"No free preview port available in the reserved range {PortRangeStart}-{PortRangeEnd}.");
        }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
