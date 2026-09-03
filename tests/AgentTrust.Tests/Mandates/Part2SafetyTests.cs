using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Payments;
using AgentTrust.Scheduling;
using Xunit;

namespace AgentTrust.Tests.Mandates;

public class Part2SafetyTests
{
    private static FinancialMandate Mandate(decimal? daily = null, decimal? weekly = 20m, decimal? monthly = 50m) =>
        new("M1", "P1", "A1", "Uber", "transport", "PM1", 25m, weekly, monthly, "GBP",
            new Dictionary<string, string> { ["route"] = "A-B" }, AboveLimitAction.RequireApproval,
            MandateStatus.Active, DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2028-01-01T00:00:00Z"))
        { DailyLimit = daily };

    [Fact]
    public void ConcurrentReservationsCannotSpendTheSameAllowanceTwice()
    {
        var usage = new InMemoryMandateUsageTracker();
        var mandate = Mandate(weekly: 20m);
        var successes = 0;

        Parallel.For(0, 2, i =>
        {
            if (usage.TryReserve(mandate, $"E{i}", 15m, DateTimeOffset.Parse("2027-06-01T10:00:00Z"), out _, out _))
                Interlocked.Increment(ref successes);
        });

        Assert.Equal(1, successes);
    }

    [Fact]
    public void FailedPaymentReservationCanBeReleasedAndReused()
    {
        var usage = new InMemoryMandateUsageTracker();
        var mandate = Mandate(weekly: 20m);
        Assert.True(usage.TryReserve(mandate, "E1", 15m, DateTimeOffset.UtcNow, out var first, out _));
        Assert.True(usage.Release(first!.ReservationId));
        Assert.True(usage.TryReserve(mandate, "E2", 15m, DateTimeOffset.UtcNow, out _, out _));
    }

    [Fact]
    public void OneOffAuthorisationIsExactAndSingleUse()
    {
        var store = new InMemoryOneOffAuthorisationStore();
        var fingerprint = TransactionFingerprint.Create(Mandate(), "E1", 31.40m, "GBP",
            new Dictionary<string, string> { ["route"] = "A-B" });
        var item = new OneOffAuthorisation("O1", "E1", "M1", 1, fingerprint, 31.40m, "GBP", "Uber",
            "PM1", "human-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            OneOffAuthorisationStatus.Active, null);
        store.Save(item);

        Assert.False(store.TryConsume("O1", "different-context-hash", DateTimeOffset.UtcNow, out _));
        Assert.True(store.TryConsume("O1", fingerprint, DateTimeOffset.UtcNow, out var consumed));
        Assert.Equal(OneOffAuthorisationStatus.Consumed, consumed!.Status);
        Assert.False(store.TryConsume("O1", fingerprint, DateTimeOffset.UtcNow, out _));
    }

    [Fact]
    public void NewMandateVersionPreservesAndSupersedesOldVersion()
    {
        var store = new InMemoryMandateStore();
        var v1 = Mandate() with { Version = 1 };
        var v2 = Mandate() with { Version = 2, PerTransactionLimit = 40m, SupersedesMandateId = "M1" };
        store.Save(v1);
        store.Save(v2);

        Assert.Equal(MandateStatus.Superseded, store.FindVersion("M1", 1)!.Status);
        Assert.Equal(40m, store.Find("M1")!.PerTransactionLimit);
        Assert.Equal(2, store.GetHistory("M1").Count);
    }

    [Fact]
    public void PaymentRetryWithSameIdempotencyKeySubmitsOnlyOnce()
    {
        var adapter = new MockPaymentAdapter();
        var coordinator = new PaymentExecutionCoordinator(adapter, new InMemoryPaymentAttemptStore());
        var intent = new TransactionIntent("TX1", "A1", "P1", "purchase:transport", "Uber", "transport",
            20m, "ride", Array.Empty<EvidenceItem>(), DateTimeOffset.UtcNow, "IDEMPOTENT-1");

        var first = coordinator.Submit(intent);
        var retry = coordinator.Submit(intent with { TransactionId = "TX1-retry" });

        Assert.Equal(first, retry);
        Assert.Single(adapter.SubmittedTransactionIds);
    }

    [Fact]
    public void ScheduledOccurrenceCanOnlyBeClaimedOnce()
    {
        var store = new InMemoryScheduledOccurrenceStore();
        var scheduled = DateTimeOffset.Parse("2027-06-07T07:30:00Z");
        Assert.True(store.TryClaim("TASK1", scheduled, scheduled, out _));
        Assert.False(store.TryClaim("TASK1", scheduled, scheduled.AddSeconds(1), out _));
    }

    [Fact]
    public void PerTransactionLimitDoesNotAccidentallyBecomeDailyLimit()
    {
        var withoutDailyLimit = Mandate(daily: null);
        var withDailyLimit = Mandate(daily: 60m);

        Assert.Null(MandateToAuthorityMapper.ToAuthority(withoutDailyLimit).DailyLimit);
        Assert.Equal(60m, MandateToAuthorityMapper.ToAuthority(withDailyLimit).DailyLimit);
    }
}
