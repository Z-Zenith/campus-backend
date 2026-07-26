using BackendApi.Services;

namespace BackendApi.Tests.Fakes;

// AIS-05: a real call to Pangram isn't available in unit tests (no credentials/network
// dependency), so controller tests configure canned behavior here instead — mirrors
// FakeCopyleaksClient.cs.
public class FakePangramClient : IPangramClient
{
    public bool Configured { get; set; } = true;
    public string? LastContent { get; private set; }
    public AiDetectionResult ResultToReturn { get; set; } = new(0, null);

    public Task<AiDetectionResult> DetectAsync(string content, CancellationToken ct = default)
    {
        if (!Configured)
        {
            throw new ExternalServiceNotConfiguredException("Pangram");
        }

        LastContent = content;
        return Task.FromResult(ResultToReturn);
    }
}
