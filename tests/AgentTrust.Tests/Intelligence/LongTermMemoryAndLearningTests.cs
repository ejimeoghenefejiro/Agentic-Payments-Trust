using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Learning;
using AgentTrust.Intelligence.Risk;
using Xunit;

namespace AgentTrust.Tests.Intelligence;

public class LongTermMemoryAndLearningTests
{
    [Fact]
    public void ProfileHistoryStoreReturnsSnapshotsInChronologicalOrder()
    {
        var store = new InMemoryProfileHistoryStore();
        var early = new CustomerBehaviourProfile("C1", 30m, 400m, new[] { "Manchester" }, new[] { "D1" }, new[] { "M1" }, new[] { "B1" }, new TimeOnly(7, 0), new TimeOnly(23, 0), 40);
        var later = early with { TypicalMaxAmount = 4000m };

        store.RecordSnapshot("C1", later, DateTimeOffset.Parse("2027-06-01T00:00:00Z"));
        store.RecordSnapshot("C1", early, DateTimeOffset.Parse("2027-01-01T00:00:00Z"));

        var history = store.GetHistory("C1");

        Assert.Equal(2, history.Count);
        Assert.Equal(30m, history[0].Profile.TypicalMinAmount); // earliest snapshot first
        Assert.Equal(4000m, history[1].Profile.TypicalMaxAmount);
    }

    [Fact]
    public void BehaviouralChangeDetectionUsesLongTermMemoryToCompareAgainstAnOlderSnapshot()
    {
        var store = new InMemoryProfileHistoryStore();
        var sixMonthsAgo = new CustomerBehaviourProfile("C1", 30m, 400m, new[] { "Manchester" }, new[] { "D1" }, new[] { "M1" }, new[] { "B1" }, new TimeOnly(7, 0), new TimeOnly(23, 0), 40);
        store.RecordSnapshot("C1", sixMonthsAgo, DateTimeOffset.Parse("2027-01-01T00:00:00Z"));

        var today = sixMonthsAgo with { TypicalMaxAmount = 9000m, TypicalDevices = new[] { "BrandNewDevice" }, TypicalLocations = new[] { "Lagos" } };

        var baseline = store.GetSnapshotClosestTo("C1", DateTimeOffset.Parse("2027-01-01T00:00:00Z"))!;
        var deviations = BehaviourDeviationService.CompareCustomerProfiles(baseline, today);

        Assert.Contains(deviations, d => d.Aspect == "SPENDING_RANGE_SHIFT");
        Assert.Contains(deviations, d => d.Aspect == "DEVICE_SET_CHANGED");
        Assert.Contains(deviations, d => d.Aspect == "LOCATION_SET_CHANGED");
    }

    [Fact]
    public void ModelEvaluationComputesPrecisionRecallAndF1FromRecordedFeedback()
    {
        var store = new InMemoryOutcomeStore();
        // 2 true positives, 1 false positive, 1 false negative, 1 true negative.
        store.Record(new DecisionFeedback("tx1", IntelligenceRecommendation.Escalate, ActualOutcome.Suspicious, null, DateTimeOffset.UtcNow));
        store.Record(new DecisionFeedback("tx2", IntelligenceRecommendation.Escalate, ActualOutcome.Suspicious, null, DateTimeOffset.UtcNow));
        store.Record(new DecisionFeedback("tx3", IntelligenceRecommendation.Escalate, ActualOutcome.Legitimate, "false alarm", DateTimeOffset.UtcNow));
        store.Record(new DecisionFeedback("tx4", IntelligenceRecommendation.Approve, ActualOutcome.Suspicious, "missed it", DateTimeOffset.UtcNow));
        store.Record(new DecisionFeedback("tx5", IntelligenceRecommendation.Approve, ActualOutcome.Legitimate, null, DateTimeOffset.UtcNow));

        var result = ModelEvaluation.Evaluate(store.GetAll());

        Assert.Equal(5, result.TotalCases);
        Assert.Equal(2, result.TruePositives);
        Assert.Equal(1, result.FalsePositives);
        Assert.Equal(1, result.FalseNegatives);
        Assert.Equal(1, result.TrueNegatives);
        Assert.Equal(2.0 / 3, result.Precision, 3);
        Assert.Equal(2.0 / 3, result.Recall, 3);
        Assert.Equal(0.6, result.Accuracy, 3);
    }

    [Fact]
    public void ModelEvaluationHandlesNoFeedbackGracefully()
    {
        var result = ModelEvaluation.Evaluate(Array.Empty<DecisionFeedback>());
        Assert.Equal(0, result.TotalCases);
        Assert.Equal(0, result.Precision);
    }
}
