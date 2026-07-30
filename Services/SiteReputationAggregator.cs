using BackendApi.Data;
using BackendApi.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BackendApi.Services;

// SDA-03 classification policy engine (SDA/SEK plan, Work Item A2): college-wide
// aggregation for the "historical usage" and "user feedback" hybrid-score signals.
// Deliberately college-wide, not scoped to the requesting student — see
// BrowsingController's POST /browser/classify doc comment for why a per-student query
// here would be a real bug (every student at a college must see the same decision for
// the same host).
public static class SiteReputationAggregator
{
    /// <summary>
    /// Fraction of this college's students (in [0, 1]) who have visited `host` at least
    /// once, per browsing_history. A simple "how well-trodden is this site at this
    /// college" signal — not incident-weighted, just presence.
    /// </summary>
    public static async Task<double> HistoricalUsageRatioAsync(
        AppDbContext db, Guid collegeId, string host, CancellationToken ct = default)
    {
        var totalStudents = await db.Users
            .CountAsync(u => u.CollegeId == collegeId && u.AccountType == AccountType.Student, ct);
        if (totalStudents == 0)
        {
            return 0.0;
        }

        // EF can't translate a Uri-parsing host extraction into SQL, so this pulls the raw
        // URLs for the college's students and matches host in-process. browsing_history
        // rows are per-visit (not aggregated), so this is bounded by realistic visit
        // volume per college, not unbounded — acceptable for a cache-miss-only path.
        var urls = await db.BrowsingHistories
            .Where(v => v.Student.CollegeId == collegeId)
            .Select(v => new { v.StudentId, v.Url })
            .ToListAsync(ct);

        var distinctStudentsWithVisit = urls
            .Where(v => TryGetHost(v.Url) == host)
            .Select(v => v.StudentId)
            .Distinct()
            .Count();

        return Math.Clamp((double)distinctStudentsWithVisit / totalStudents, 0.0, 1.0);
    }

    /// <summary>
    /// Fraction of this college's site_classification_feedback rows for `host` that say
    /// "should_allow" (in [0, 1]). Defaults to a neutral 0.5 when there's no feedback yet
    /// — absence of feedback should neither help nor hurt the score.
    /// </summary>
    public static async Task<double> FeedbackRatioAsync(
        AppDbContext db, Guid collegeId, string host, CancellationToken ct = default)
    {
        var feedback = await db.SiteClassificationFeedbacks
            .Where(f => f.CollegeId == collegeId && f.Host == host)
            .Select(f => f.Feedback)
            .ToListAsync(ct);

        if (feedback.Count == 0)
        {
            return 0.5;
        }

        var shouldAllowCount = feedback.Count(f => f == SiteClassificationFeedbackType.ShouldAllow);
        return (double)shouldAllowCount / feedback.Count;
    }

    private static string? TryGetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;
}
