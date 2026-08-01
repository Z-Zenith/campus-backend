namespace BackendApi.Contracts;

// SEK-04
public record ImageSearchRequest(string Query);

public record ImageSearchResultDto(string Id, string Title, string SourceUrl, string ThumbnailUrl, int Width, int Height, string Attribution);

public record ImageSearchResponseDto(string Query, List<ImageSearchResultDto> Results, bool Degraded);

public record SaveImageRequest(string SourceUrl);

public record SaveImageResponse(string Url);
