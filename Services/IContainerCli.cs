namespace BackendApi.Services;

// Abstracts which container CLI ContainerCodeRunner/TerminalSessionService shell out to.
// Podman is drop-in CLI-compatible with every flag either of those uses (`run`, `--rm`, `-i`,
// `-d`, `--network`, `--memory`, `--memory-swap`, `--pids-limit`, `--cpus`, `-v`, `-w`, `exec`,
// `rm`), so this stays a thin marker rather than a full command-builder — the only thing that
// varies between runtimes here is the executable name. See ContainerRuntimeDetector for why
// this exists at all: some student/dev machines only have Podman installed, not Docker.
public interface IContainerCli
{
    // False when neither Docker nor Podman was detected reachable at startup. Callers must
    // check this before use — ExecutableName throws rather than returning a name that would
    // just fail with a confusing "file not found" from Process.Start.
    bool IsAvailable { get; }

    string ExecutableName { get; }
}

public sealed class DockerCli : IContainerCli
{
    public bool IsAvailable => true;
    public string ExecutableName => "docker";
}

public sealed class PodmanCli : IContainerCli
{
    public bool IsAvailable => true;
    public string ExecutableName => "podman";
}

// Registered when ContainerRuntimeDetector found neither runtime reachable at startup.
// ContainerCodeRunner/TerminalSessionService check IsAvailable and fail fast with the same
// "Code Execution Service is unreachable" signal a real Docker-unreachable failure already
// produces, instead of trying to spawn a process that isn't there. Work Item B1 (remote
// execution fallback) is what turns "no local runtime" into an actual fallback to a remote
// service instead of a dead end — this type alone just makes that state clean and detectable.
public sealed class UnavailableContainerCli : IContainerCli
{
    public bool IsAvailable => false;

    public string ExecutableName =>
        throw new InvalidOperationException("No local container runtime (Docker or Podman) is available.");
}
