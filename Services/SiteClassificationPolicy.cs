namespace BackendApi.Services;

// SDA-03 classification policy engine (SDA/SEK plan, Work Item A2): the pure hybrid-score
// math, kept separate from BrowsingController/SiteReputationAggregator's DB-dependent
// aggregation so it's directly unit-testable — mirrors this codebase's existing preference
// for pure, isolated logic (e.g. DockerCodeRunner.BuildContainerArgs).
public static class SiteClassificationPolicy
{
    // Weights sum to 1.0 by construction — see the SDA/SEK plan's A2 section for why these
    // specific weights (40% domain reputation, 30% content classification, 20% historical
    // usage, 10% user feedback).
    private const double DomainReputationWeight = 0.40;
    private const double ContentWeight = 0.30;
    private const double HistoricalUsageWeight = 0.20;
    private const double FeedbackWeight = 0.10;

    // A page scoring at or above this on any single "blocked.*" category is denied
    // outright, regardless of how favorable every other signal is — a strong red flag from
    // content classification must not be washed out by decent domain reputation. This is a
    // deliberate hard veto, not part of the weighted blend.
    private const double BlockedCategoryVetoThreshold = 0.5;

    // The weighted blend must clear this bar (once the veto above doesn't already apply)
    // for the page to be allowed.
    private const double AllowThreshold = 0.55;

    public readonly record struct Decision(double HybridScore, bool Allowed, string? MatchedCategory);

    /// <summary>
    /// Combines the four signals into a single allow/deny decision. `contentCategories` is
    /// classifier.py's raw cosine-similarity output (range [-1, 1] per category, one entry
    /// per CATEGORY_EXEMPLARS key, keys prefixed "blocked." for the blocked branch and
    /// anything else for the allow branch). `historicalUsageRatio`/`feedbackRatio` are
    /// already-aggregated college-wide signals in [0, 1] (see SiteReputationAggregator) —
    /// this method has no DB dependency of its own.
    /// </summary>
    public static Decision ComputeHybridScore(
        double domainReputationScore,
        IReadOnlyDictionary<string, double> contentCategories,
        double historicalUsageRatio,
        double feedbackRatio)
    {
        var blocked = TopCategory(contentCategories, c => c.StartsWith("blocked.", StringComparison.Ordinal));
        var allowed = TopCategory(contentCategories, c => !c.StartsWith("blocked.", StringComparison.Ordinal));

        if (blocked is { Score: >= BlockedCategoryVetoThreshold })
        {
            // Hybrid score still computed/returned for observability (what did the other
            // signals say?), but the veto overrides it — Allowed is false regardless of
            // the blended number.
            var vetoedScore = Blend(domainReputationScore, allowed?.Score ?? 0, historicalUsageRatio, feedbackRatio);
            return new Decision(vetoedScore, Allowed: false, MatchedCategory: blocked.Value.Category);
        }

        // Cosine similarity is in [-1, 1]; normalize the content signal to [0, 1] before
        // blending with the other three signals, which are already in [0, 1].
        var normalizedContentScore = allowed is null ? 0.0 : (allowed.Value.Score + 1.0) / 2.0;
        var hybridScore = Blend(domainReputationScore, normalizedContentScore, historicalUsageRatio, feedbackRatio);

        return new Decision(hybridScore, Allowed: hybridScore >= AllowThreshold, MatchedCategory: allowed?.Category);
    }

    private static double Blend(double domainReputation, double normalizedContent, double historicalUsage, double feedback) =>
        DomainReputationWeight * domainReputation
        + ContentWeight * normalizedContent
        + HistoricalUsageWeight * historicalUsage
        + FeedbackWeight * feedback;

    private static (string Category, double Score)? TopCategory(
        IReadOnlyDictionary<string, double> categories, Func<string, bool> predicate)
    {
        var matching = categories.Where(kv => predicate(kv.Key)).ToList();
        if (matching.Count == 0)
        {
            return null;
        }
        var top = matching.MaxBy(kv => kv.Value);
        return (top.Key, top.Value);
    }
}
