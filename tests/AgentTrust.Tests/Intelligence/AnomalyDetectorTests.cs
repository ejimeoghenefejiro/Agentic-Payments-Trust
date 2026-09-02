using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using Xunit;

namespace AgentTrust.Tests.Intelligence;

public class AnomalyDetectorTests
{
    private static CustomerBehaviourProfile NormalProfile() => new(
        "C10391", 30m, 400m,
        new[] { "Manchester", "Salford" },
        new[] { "D44", "D71" },
        new[] { "M14", "M18", "M33" },
        new[] { "B101", "B201" },
        new TimeOnly(7, 0), new TimeOnly(23, 0),
        40);

    [Fact]
    public void TransactionAnomalyDetectorFlagsEveryDeviationInDocNightTimeScenario()
    {
        // The doc's worked example: 03:41, £8,700, new beneficiary, new device, new IP, new
        // country, beneficiary added 2 minutes ago, 3 failed attempts beforehand.
        var candidate = new TransactionEvent(
            "tx_night", "C10391", "M14", 8700m, "GBP",
            new DateTimeOffset(2027, 6, 7, 3, 41, 0, TimeSpan.Zero),
            "D999-unknown", "203.0.113.9", "Lagos",
            "B999-new", new DateTimeOffset(2027, 6, 7, 3, 39, 0, TimeSpan.Zero),
            false, 3);

        var detector = new TransactionAnomalyDetector();
        var factors = detector.Detect(candidate, NormalProfile(), Array.Empty<TransactionEvent>());

        Assert.Contains(factors, f => f.Factor == "NEW_DEVICE");
        Assert.Contains(factors, f => f.Factor == "UNUSUAL_LOCATION");
        Assert.Contains(factors, f => f.Factor == "NEW_BENEFICIARY");
        Assert.Contains(factors, f => f.Factor == "RECENTLY_ADDED_BENEFICIARY");
        Assert.Contains(factors, f => f.Factor == "UNUSUAL_TIME");
        Assert.Contains(factors, f => f.Factor == "PRIOR_FAILED_ATTEMPTS");
    }

    [Fact]
    public void TransactionAnomalyDetectorFlagsNothingForOrdinaryTransaction()
    {
        var candidate = new TransactionEvent(
            "tx_ordinary", "C10391", "M14", 120m, "GBP",
            new DateTimeOffset(2027, 6, 7, 14, 0, 0, TimeSpan.Zero),
            "D44", "1.2.3.4", "Manchester", "B101",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), false, 0);

        var detector = new TransactionAnomalyDetector();
        var factors = detector.Detect(candidate, NormalProfile(), Array.Empty<TransactionEvent>());

        Assert.Empty(factors);
    }

    [Fact]
    public void AmountAnomalyDetectorFlagsMaterialDeviationOnly()
    {
        var detector = new AmountAnomalyDetector();
        var profile = NormalProfile();

        var normal = new TransactionEvent("tx1", "C10391", "M14", 200m, "GBP", DateTimeOffset.UtcNow, "D44", "1.2.3.4", "Manchester", null, null, false, 0);
        var anomalous = normal with { TransactionId = "tx2", Amount = 8700m };

        Assert.Empty(detector.Detect(normal, profile, Array.Empty<TransactionEvent>()));
        var factors = detector.Detect(anomalous, profile, Array.Empty<TransactionEvent>());
        Assert.Contains(factors, f => f.Factor == "TRANSACTION_AMOUNT_ANOMALY");
    }

    [Fact]
    public void VelocityDetectorFlagsHighFrequencyBurst()
    {
        var detector = new VelocityDetector(TimeSpan.FromMinutes(30), countThreshold: 3, amountThreshold: 5000m);
        var now = new DateTimeOffset(2027, 6, 7, 12, 0, 0, TimeSpan.Zero);
        var history = Enumerable.Range(0, 4)
            .Select(i => new TransactionEvent($"tx_prior_{i}", "C10391", "M14", 100m, "GBP", now.AddMinutes(-i * 5), "D44", "1.2.3.4", "Manchester", null, null, false, 0))
            .ToList();
        var candidate = new TransactionEvent("tx_candidate", "C10391", "M14", 50m, "GBP", now, "D44", "1.2.3.4", "Manchester", null, null, false, 0);

        var factors = detector.Detect(candidate, null, history);

        Assert.Contains(factors, f => f.Factor == "HIGH_TRANSACTION_VELOCITY");
    }

    [Fact]
    public void VelocityDetectorFindsNothingForIsolatedTransaction()
    {
        var detector = new VelocityDetector();
        var candidate = new TransactionEvent("tx1", "C10391", "M14", 50m, "GBP", DateTimeOffset.UtcNow, "D44", "1.2.3.4", "Manchester", null, null, false, 0);

        var factors = detector.Detect(candidate, null, Array.Empty<TransactionEvent>());

        Assert.Empty(factors);
    }
}
