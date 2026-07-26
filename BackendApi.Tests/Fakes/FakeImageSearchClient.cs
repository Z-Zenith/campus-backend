using BackendApi.Services;

namespace BackendApi.Tests.Fakes;

// SEK-04: a real call to Openverse isn't available in unit tests (no network dependency), so
// controller tests configure canned behavior here instead.
public class FakeImageSearchClient : IImageSearchClient
{
    public bool ShouldThrow { get; set; }
    public string? LastQuery { get; private set; }
    public IReadOnlyList<ImageSearchResultData> Results { get; set; } = [];

    public Task<IReadOnlyList<ImageSearchResultData>> SearchAsync(string query, CancellationToken ct = default)
    {
        LastQuery = query;
        if (ShouldThrow)
        {
            throw new HttpRequestException("Openverse is unreachable.");
        }
        return Task.FromResult(Results);
    }
}
