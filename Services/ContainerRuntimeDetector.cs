using System.Diagnostics;

namespace BackendApi.Services;

// Probes for a usable local container runtime once at startup (see Program.cs), preferring
// Docker — this codebase's original, most-exercised runtime — then falling back to Podman, so
// ContainerCodeRunner/TerminalSessionService work unmodified on a machine that only ever had
// Podman installed. `<cli> info` (not just checking the executable is on PATH) is used
// deliberately: it also fails when the CLI is installed but its daemon/backend isn't actually
// reachable (e.g. Docker Desktop not running), which is the same "unusable" state as not being
// installed at all from this app's point of view.
public static class ContainerRuntimeDetector
{
    public static async Task<IContainerCli> DetectAsync(ILogger logger, CancellationToken ct = default)
    {
        if (await ProbeAsync("docker", ct))
        {
            logger.LogInformation("Detected Docker as the local container runtime.");
            return new DockerCli();
        }
        if (await ProbeAsync("podman", ct))
        {
            logger.LogInformation("Detected Podman as the local container runtime.");
            return new PodmanCli();
        }
        logger.LogWarning(
            "No local container runtime (Docker or Podman) detected. Code execution will fail " +
            "until one is available or a remote execution fallback is configured.");
        return new UnavailableContainerCli();
    }

    private static async Task<bool> ProbeAsync(string executable, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(executable, "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Executable not found on PATH.
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Probe itself hung (e.g. daemon unresponsive) — treat as "not usable," same as
            // not being installed.
            return false;
        }
    }
}
