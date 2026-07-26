using System.Net.Http.Json;
using System.Text.Json;

namespace BackendApi.Services;

public record ImageSearchResultData(
    string Id,
    string Title,
    string SourceUrl,
    string ThumbnailUrl,
    int Width,
    int Height,
    string Attribution);

// SEK-04: thin client for Openverse's public image-search API (openverse.org). Chosen over a
// paid provider (Copyleaks/Pangram-style) since this feature is Could-priority and Openverse
// is CC-licensed + keyless — its license/creator fields map directly onto
// ImageSearchResult.attribution, which the SEK contract already requires the embedder to
// render.
public interface IImageSearchClient
{
    Task<IReadOnlyList<ImageSearchResultData>> SearchAsync(string query, CancellationToken ct = default);
}

public class OpenverseImageSearchClient(HttpClient http) : IImageSearchClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record OpenverseImage(
        string Id,
        string Title,
        string Url,
        string Thumbnail,
        int? Width,
        int? Height,
        string? License,
        string? Creator);

    private sealed record OpenverseSearchResponse(List<OpenverseImage>? Results);

    public async Task<IReadOnlyList<ImageSearchResultData>> SearchAsync(string query, CancellationToken ct = default)
    {
        var response = await http.GetFromJsonAsync<OpenverseSearchResponse>(
            $"/v1/images/?q={Uri.EscapeDataString(query)}", JsonOptions, ct);

        if (response?.Results is null)
        {
            return [];
        }

        return response.Results
            .Where(r => !string.IsNullOrWhiteSpace(r.Url) && !string.IsNullOrWhiteSpace(r.Thumbnail))
            .Select(r => new ImageSearchResultData(
                r.Id,
                r.Title,
                r.Url,
                r.Thumbnail,
                r.Width ?? 0,
                r.Height ?? 0,
                BuildAttribution(r.License, r.Creator)))
            .ToList();
    }

    private static string BuildAttribution(string? license, string? creator) =>
        $"{(string.IsNullOrWhiteSpace(license) ? "Unknown license" : license.ToUpperInvariant())} / {(string.IsNullOrWhiteSpace(creator) ? "Unknown author" : creator)}";
}
