using BackendApi.Services;

namespace BackendApi.Tests.Fakes;

public class FakeImageStorageService : IImageStorageService
{
    public string? LastSourceUrl { get; private set; }
    public string RelativeUrlToReturn { get; set; } = "/media/image-search/fake.jpg";

    public Task<string> SaveFromUrlAsync(string sourceUrl, CancellationToken ct = default)
    {
        LastSourceUrl = sourceUrl;
        return Task.FromResult(RelativeUrlToReturn);
    }
}
