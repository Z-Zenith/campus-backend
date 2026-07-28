namespace BackendApi.Contracts;

public record WhitelistSiteDto(Guid Id, string Url, DateTime ApprovedAt);

public record WhitelistResponse(List<WhitelistSiteDto> Sites);

public record CreateWhitelistRequestRequest(string Url);

public record WhitelistRequestDto(Guid Id, string Url, Guid RequestedBy, string Status, Guid? ReviewedBy);

public record ApproveWhitelistRequestResponse(Guid RequestId, string Status, WhitelistSiteDto Site);

// SDA-03: teacher/admin bulk-approve of the curated engineering/CS reference site list,
// skipping the one-at-a-time student-request flow for these pre-vetted sites.
public record AddDefaultSitesResponse(List<WhitelistSiteDto> Added, List<string> AlreadyWhitelisted);

// AIS-01: a single recorded page visit, logged by the whitelisted browser (SDA-03/04) on
// each navigation. Feeds the raw input generate_browsing_summary needs.
public record LogBrowsingVisitRequest(string Url, int? DurationSeconds);
