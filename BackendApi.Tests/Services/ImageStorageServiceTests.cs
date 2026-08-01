using BackendApi.Services;

namespace BackendApi.Tests.Services;

// SEK-04
public class ImageStorageServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"sek04-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class StubHandler(byte[] bytes, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task SaveFromUrlAsync_WritesTheBytesAndPicksExtensionFromContentType()
    {
        var handler = new StubHandler([1, 2, 3, 4], "image/png");
        var http = new HttpClient(handler);
        var service = new LocalImageStorageService(http, new ImageStorageOptions(_tempDir, "/media/image-search"));

        var relativeUrl = await service.SaveFromUrlAsync("https://example.com/tree", CancellationToken.None);

        Assert.StartsWith("/media/image-search/", relativeUrl);
        Assert.EndsWith(".png", relativeUrl);
        var fileName = relativeUrl.Split('/').Last();
        var writtenBytes = await File.ReadAllBytesAsync(Path.Combine(_tempDir, fileName));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, writtenBytes);
    }

    [Fact]
    public async Task SaveFromUrlAsync_RejectsANonHttpUrl()
    {
        var service = new LocalImageStorageService(new HttpClient(new StubHandler([], "image/png")), new ImageStorageOptions(_tempDir, "/media/image-search"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveFromUrlAsync("not-a-url", CancellationToken.None));
    }
}
