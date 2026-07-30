using BackendApi.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BackendApi.Services;

// B1 remote-execution fallback (SDA/SEK plan): ContainerCodeRunner (Docker or Podman,
// whichever 0.5's detection found — already-working, offline, this app's primary
// execution path) is tried first; RemoteCodeRunner (Piston) only runs when the primary
// throws the specific HttpRequestException ContainerCodeRunner wraps infra failures in
// (including "no local runtime detected at all," per IContainerCli.IsAvailable — see
// ContainerCodeRunner.RunAsync). A real compile/runtime error from the primary is a
// normal CodeRunResultDto, not an exception, and must never trigger fallback — this
// strategy only reacts to "the execution backend itself is broken," never to "the
// student's code is broken."
//
// Depends on ICodeRunner (via keyed DI, see Program.cs), not the concrete
// ContainerCodeRunner/RemoteCodeRunner types directly — both already implement
// ICodeRunner, and taking the interface here (rather than two concrete-class
// constructor params) is what makes this class testable with plain fakes instead of
// needing to construct real runners and their own dependencies in every test.
public sealed class CompositeCodeRunner(
    ILogger<CompositeCodeRunner> logger,
    [FromKeyedServices("primary")] ICodeRunner primary,
    [FromKeyedServices("fallback")] ICodeRunner fallback) : ICodeRunner
{
    public async Task<CodeRunResultDto> RunAsync(
        string entryFilePath, IReadOnlyList<CodeFileDto> files, string? stdin, CancellationToken ct = default)
    {
        try
        {
            return await primary.RunAsync(entryFilePath, files, stdin, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Primary (container) code runner unreachable — falling back to remote execution");
            return await fallback.RunAsync(entryFilePath, files, stdin, ct);
        }
    }
}
