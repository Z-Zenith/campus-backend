using BackendApi.Contracts;
using BackendApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendApi.Controllers;

// SEK-04: backs the Shared Editor Kit's ImageSearch component (rendered as a child of
// NotesEditor). Gated the same as NotesController — any signed-in user, since notes are
// personal and not gated by a special permission the way plagiarism/AI-detection are.
[ApiController]
[Route("api/v1/image-search")]
[Authorize]
public class ImageSearchController(IImageSearchClient searchClient, IImageStorageService storage, ILogger<ImageSearchController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ImageSearchResponseDto>> Search(ImageSearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest(new { error = "query_required", message = "query must not be empty." });
        }

        var query = request.Query.Trim();
        try
        {
            var results = await searchClient.SearchAsync(query, ct);
            return Ok(new ImageSearchResponseDto(query, results.Select(ToDto).ToList(), Degraded: false));
        }
        // Matches ImageSearchResponse.degraded's contract ("true when the search service is
        // unavailable and the UI should degrade") — a provider outage surfaces as an empty,
        // degraded result set, not a 5xx that crashes the notes editor.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Image search provider unavailable for query {Query}", query);
            return Ok(new ImageSearchResponseDto(query, [], Degraded: true));
        }
    }

    [HttpPost("save")]
    public async Task<ActionResult<SaveImageResponse>> Save(SaveImageRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new { error = "invalid_url", message = "sourceUrl must be an absolute http:// or https:// address." });
        }

        var relativeUrl = await storage.SaveFromUrlAsync(request.SourceUrl, ct);
        return Ok(new SaveImageResponse($"{Request.Scheme}://{Request.Host}{relativeUrl}"));
    }

    private static ImageSearchResultDto ToDto(ImageSearchResultData r) =>
        new(r.Id, r.Title, r.SourceUrl, r.ThumbnailUrl, r.Width, r.Height, r.Attribution);
}
