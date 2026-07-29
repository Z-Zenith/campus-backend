using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

namespace BackendApi.Services;

// #79: no rate limiter existed anywhere in Program.cs. Parent login (PRT-01) authenticates
// on roll number + DOB — far weaker than password+MFA — and the main login endpoint had no
// lockout either. Extracted from Program.cs as a named policy (rather than an inline lambda)
// so the partitioning behavior itself can be unit tested without booting the full app/DB.
public static class RateLimiterPolicies
{
    public const string Auth = "auth";

    public static RateLimitPartition<string> AuthPartitioner(HttpContext httpContext) =>
        CreatePartition(httpContext, permitLimit: 5);

    // Dev-only: the 5/min production limit above is easy to exhaust while actively testing
    // login locally (repeated manual attempts, verification scripts, etc. all sharing the same
    // loopback IP), turning an otherwise-correct password/TOTP attempt into an opaque 429 that
    // looks identical to a wrong code. Never used outside Development — see Program.cs.
    public static RateLimitPartition<string> RelaxedAuthPartitioner(HttpContext httpContext) =>
        CreatePartition(httpContext, permitLimit: 100);

    private static RateLimitPartition<string> CreatePartition(HttpContext httpContext, int permitLimit) =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueLimit = 0,
            });

    public static void ConfigureAuth(RateLimiterOptions options, bool relaxed = false)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy<string>(Auth, relaxed ? RelaxedAuthPartitioner : AuthPartitioner);
    }
}
