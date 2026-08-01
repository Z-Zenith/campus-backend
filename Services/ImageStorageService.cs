namespace BackendApi.Services;

public record ImageStorageOptions(string Directory, string RequestPath);

public interface IImageStorageService
{
    /// <summary>Downloads the image at <paramref name="sourceUrl"/> and re-hosts the bytes
    /// locally, returning a request path (relative to this API's own origin) that survives
    /// the source going away.</summary>
    Task<string> SaveFromUrlAsync(string sourceUrl, CancellationToken ct = default);
}

// SEK-04: no cloud-storage client (GCS/S3) exists anywhere in this codebase yet —
// CommunityController's material upload only ever stores a caller-supplied FileUrl, it never
// fetches bytes itself — so standing up a brand-new object-storage integration just for this
// Could-priority feature isn't warranted. Persisting to a container-local volume instead
// mirrors the pattern DataProtection's key ring already uses (Program.cs) and is real
// re-hosting (survives the source URL disappearing), not a same-origin streaming proxy.
// Revisit if/when a real object-storage client is added for materials.
public class LocalImageStorageService(HttpClient http, ImageStorageOptions options) : IImageStorageService
{
    public async Task<string> SaveFromUrlAsync(string sourceUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("sourceUrl must be an absolute http:// or https:// address.", nameof(sourceUrl));
        }

        using var response = await http.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        var pathExtension = Path.GetExtension(uri.AbsolutePath);
        var extension = ExtensionFromContentType(response.Content.Headers.ContentType?.MediaType)
            ?? (pathExtension.Length > 0 ? pathExtension : ".img");
        var fileName = $"{Guid.NewGuid()}{extension}";

        Directory.CreateDirectory(options.Directory);
        await File.WriteAllBytesAsync(Path.Combine(options.Directory, fileName), bytes, ct);

        return $"{options.RequestPath}/{fileName}";
    }

    private static string? ExtensionFromContentType(string? mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => null,
    };
}
