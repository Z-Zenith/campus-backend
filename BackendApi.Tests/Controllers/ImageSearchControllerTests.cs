using BackendApi.Contracts;
using BackendApi.Controllers;
using BackendApi.Services;
using BackendApi.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackendApi.Tests.Controllers;

public class ImageSearchControllerTests
{
    private static ImageSearchController NewController(FakeImageSearchClient search, FakeImageStorageService storage) =>
        new(search, storage, NullLogger<ImageSearchController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

    [Fact]
    public async Task Search_RejectsEmptyQuery()
    {
        var controller = NewController(new FakeImageSearchClient(), new FakeImageStorageService());

        var result = await controller.Search(new ImageSearchRequest("   "), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_ReturnsResultsFromTheClient()
    {
        var search = new FakeImageSearchClient
        {
            Results = [new ImageSearchResultData("img-1", "A tree", "https://example.com/tree.jpg", "https://example.com/tree-thumb.jpg", 800, 600, "CC-BY / Jane Doe")],
        };
        var controller = NewController(search, new FakeImageStorageService());

        var result = await controller.Search(new ImageSearchRequest("tree"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ImageSearchResponseDto>(ok.Value);
        Assert.False(dto.Degraded);
        Assert.Single(dto.Results);
        Assert.Equal("img-1", dto.Results[0].Id);
        Assert.Equal("tree", search.LastQuery);
    }

    // SEK-04: ImageSearchResponse.degraded's contract is "true when the search service is
    // unavailable and the UI should degrade" — a provider outage must not surface as a 5xx
    // that crashes the notes editor's image search panel.
    [Fact]
    public async Task Search_DegradesInsteadOfThrowing_WhenTheProviderIsUnreachable()
    {
        var search = new FakeImageSearchClient { ShouldThrow = true };
        var controller = NewController(search, new FakeImageStorageService());

        var result = await controller.Search(new ImageSearchRequest("tree"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<ImageSearchResponseDto>(ok.Value);
        Assert.True(dto.Degraded);
        Assert.Empty(dto.Results);
    }

    [Fact]
    public async Task Save_RejectsANonHttpUrl()
    {
        var controller = NewController(new FakeImageSearchClient(), new FakeImageStorageService());

        var result = await controller.Save(new SaveImageRequest("ftp://example.com/x.jpg"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // SEK-04: the returned URL must be absolute (this API's own origin), not the relative
    // storage path — NotesEditor embeds it verbatim as `![alt](embeddedUrl)`, and a relative
    // path would resolve against whatever page renders the note, not this API.
    [Fact]
    public async Task Save_ReturnsAnAbsoluteUrlBuiltFromTheRequest()
    {
        var storage = new FakeImageStorageService { RelativeUrlToReturn = "/media/image-search/abc123.jpg" };
        var controller = NewController(new FakeImageSearchClient(), storage);
        controller.ControllerContext.HttpContext.Request.Scheme = "https";
        controller.ControllerContext.HttpContext.Request.Host = new HostString("backend.campus.local");

        var result = await controller.Save(new SaveImageRequest("https://example.com/tree.jpg"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SaveImageResponse>(ok.Value);
        Assert.Equal("https://backend.campus.local/media/image-search/abc123.jpg", dto.Url);
        Assert.Equal("https://example.com/tree.jpg", storage.LastSourceUrl);
    }
}
