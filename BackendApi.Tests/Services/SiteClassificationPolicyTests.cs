using BackendApi.Services;

namespace BackendApi.Tests.Services;

// SDA-03 classification policy engine (SDA/SEK plan, Work Item A2): pure hybrid-score
// unit tests, independent of any DB/HTTP dependency — see SiteClassificationPolicy's own
// doc comment for why this is kept separate from SiteReputationAggregator.
public class SiteClassificationPolicyTests
{
    [Fact]
    public void ComputeHybridScore_AllowsAHighScoringSiteAcrossEverySignal()
    {
        var categories = new Dictionary<string, double> { ["technology.programming"] = 0.9 };

        var decision = SiteClassificationPolicy.ComputeHybridScore(
            domainReputationScore: 1.0, categories, historicalUsageRatio: 1.0, feedbackRatio: 1.0);

        Assert.True(decision.Allowed);
        Assert.Equal("technology.programming", decision.MatchedCategory);
    }

    [Fact]
    public void ComputeHybridScore_DeniesASiteWithNoPositiveSignalsAtAll()
    {
        var categories = new Dictionary<string, double> { ["technology.programming"] = -1.0 };

        var decision = SiteClassificationPolicy.ComputeHybridScore(
            domainReputationScore: 0.0, categories, historicalUsageRatio: 0.0, feedbackRatio: 0.0);

        Assert.False(decision.Allowed);
    }

    // The blocked-category veto must win regardless of how favorable every other signal
    // is — a strong "this is gambling" content signal shouldn't be washed out by good
    // domain reputation/historical usage/feedback.
    [Fact]
    public void ComputeHybridScore_BlockedCategoryVetoesEvenWithPerfectOtherSignals()
    {
        var categories = new Dictionary<string, double>
        {
            ["blocked.gambling"] = 0.9,
            ["productivity.design"] = 0.8,
        };

        var decision = SiteClassificationPolicy.ComputeHybridScore(
            domainReputationScore: 1.0, categories, historicalUsageRatio: 1.0, feedbackRatio: 1.0);

        Assert.False(decision.Allowed);
        Assert.Equal("blocked.gambling", decision.MatchedCategory);
    }

    [Fact]
    public void ComputeHybridScore_DoesNotVeto_WhenBlockedCategoryScoreIsBelowThreshold()
    {
        var categories = new Dictionary<string, double>
        {
            ["blocked.gambling"] = 0.1, // below the veto threshold
            ["technology.programming"] = 0.9,
        };

        var decision = SiteClassificationPolicy.ComputeHybridScore(
            domainReputationScore: 1.0, categories, historicalUsageRatio: 1.0, feedbackRatio: 1.0);

        Assert.True(decision.Allowed);
        Assert.Equal("technology.programming", decision.MatchedCategory);
    }

    [Fact]
    public void ComputeHybridScore_HandlesAnEmptyCategoryMap()
    {
        var decision = SiteClassificationPolicy.ComputeHybridScore(
            domainReputationScore: 0.9, new Dictionary<string, double>(), historicalUsageRatio: 0.9, feedbackRatio: 0.9);

        Assert.Null(decision.MatchedCategory);
        // Domain reputation (0.4*0.9) + historical usage (0.2*0.9) + feedback (0.1*0.9) =
        // 0.63, comfortably over the 0.55 allow threshold even with zero content signal.
        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData(1.0, 1.0, 1.0, 1.0, true)]
    [InlineData(0.0, 0.0, 0.0, 0.0, false)]
    public void ComputeHybridScore_HybridScoreStaysWithinZeroToOne(
        double domainReputation, double contentScore, double historicalUsage, double feedback, bool expectedAllowed)
    {
        var categories = new Dictionary<string, double> { ["technology.programming"] = 2 * contentScore - 1 }; // map [0,1] -> [-1,1]

        var decision = SiteClassificationPolicy.ComputeHybridScore(domainReputation, categories, historicalUsage, feedback);

        Assert.InRange(decision.HybridScore, 0.0, 1.0);
        Assert.Equal(expectedAllowed, decision.Allowed);
    }
}
