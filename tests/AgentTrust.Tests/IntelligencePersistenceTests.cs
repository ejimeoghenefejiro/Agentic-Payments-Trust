using AgentTrust.Data;
using AgentTrust.Intelligence.Behaviour;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTrust.Tests;

/// <summary>
/// Exercises the EF-Core-backed AgentTrust.Intelligence stores (EfTransactionEventStore,
/// EfProfileHistoryStore) against a real relational database (SQLite, in-memory mode), the same
/// way PersistenceTests does for the trust-layer stores. Neither FinancialGraph nor
/// CustomerBehaviourProfile is itself persisted — both are rebuilt on demand from the raw
/// TransactionEvent rows these stores hold, which is what's actually being proven here.
/// </summary>
public class IntelligencePersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentTrustDbContext _db;

    public IntelligencePersistenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AgentTrustDbContext>().UseSqlite(_connection).Options;
        _db = new AgentTrustDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static TransactionEvent Event(string txId, string customerId, string merchantId, decimal amount, DateTimeOffset timestamp) =>
        new(txId, customerId, merchantId, amount, "GBP", timestamp, "D1", "1.2.3.4", "Manchester", "B1", null, false, 0);

    [Fact]
    public void TransactionEventSurvivesRoundTripAndIsQueryableByCustomerAndMerchant()
    {
        var store = new EfTransactionEventStore(_db);
        var e1 = Event("tx1", "C1", "M1", 100m, DateTimeOffset.Parse("2027-01-01T10:00:00Z"));
        var e2 = Event("tx2", "C1", "M2", 200m, DateTimeOffset.Parse("2027-01-02T10:00:00Z"));
        var e3 = Event("tx3", "C2", "M1", 300m, DateTimeOffset.Parse("2027-01-03T10:00:00Z"));

        store.Record(e1);
        store.Record(e2);
        store.Record(e3);

        var reloadedStore = new EfTransactionEventStore(_db);
        var customerHistory = reloadedStore.GetCustomerHistory("C1");
        var merchantHistory = reloadedStore.GetMerchantHistory("M1");

        Assert.Equal(2, customerHistory.Count);
        Assert.Equal(new[] { "tx1", "tx2" }, customerHistory.Select(e => e.TransactionId));
        Assert.Equal(2, merchantHistory.Count);
        Assert.Contains(merchantHistory, e => e.TransactionId == "tx1");
        Assert.Contains(merchantHistory, e => e.TransactionId == "tx3");
    }

    [Fact]
    public void RecordingTheSameTransactionIdTwiceUpdatesInPlaceRatherThanDuplicating()
    {
        var store = new EfTransactionEventStore(_db);
        store.Record(Event("tx1", "C1", "M1", 100m, DateTimeOffset.Parse("2027-01-01T10:00:00Z")));
        store.Record(Event("tx1", "C1", "M1", 999m, DateTimeOffset.Parse("2027-01-01T10:00:00Z")));

        var history = new EfTransactionEventStore(_db).GetCustomerHistory("C1");

        Assert.Single(history);
        Assert.Equal(999m, history[0].Amount);
    }

    [Fact]
    public void ProfileHistoryStoreRoundTripsSnapshotsAndFindsTheClosestOne()
    {
        var store = new EfProfileHistoryStore(_db);
        var earlyProfile = new CustomerBehaviourProfile("C1", 30m, 400m, new[] { "Manchester" }, new[] { "D1" }, new[] { "M1" }, new[] { "B1" }, new TimeOnly(7, 0), new TimeOnly(23, 0), 40);
        var laterProfile = earlyProfile with { TypicalMaxAmount = 9000m, TypicalDevices = new[] { "NewDevice" } };

        store.RecordSnapshot("C1", earlyProfile, DateTimeOffset.Parse("2027-01-01T00:00:00Z"));
        store.RecordSnapshot("C1", laterProfile, DateTimeOffset.Parse("2027-06-01T00:00:00Z"));

        var reloadedStore = new EfProfileHistoryStore(_db);
        var history = reloadedStore.GetHistory("C1");
        var closestToEarly = reloadedStore.GetSnapshotClosestTo("C1", DateTimeOffset.Parse("2027-01-15T00:00:00Z"));
        var closestToLater = reloadedStore.GetSnapshotClosestTo("C1", DateTimeOffset.Parse("2027-07-01T00:00:00Z"));

        Assert.Equal(2, history.Count);
        Assert.Equal(30m, history[0].Profile.TypicalMinAmount); // earliest first
        Assert.Equal(400m, closestToEarly!.TypicalMaxAmount);
        Assert.Equal(9000m, closestToLater!.TypicalMaxAmount);
    }

    [Fact]
    public void BehaviouralChangeDetectionWorksAgainstAPersistedSnapshot()
    {
        var eventStore = new EfTransactionEventStore(_db);
        var profileStore = new EfProfileHistoryStore(_db);

        var baseline = new CustomerBehaviourProfile("C1", 30m, 400m, new[] { "Manchester" }, new[] { "D1" }, new[] { "M1" }, new[] { "B1" }, new TimeOnly(7, 0), new TimeOnly(23, 0), 40);
        profileStore.RecordSnapshot("C1", baseline, DateTimeOffset.Parse("2027-01-01T00:00:00Z"));

        for (var i = 0; i < 10; i++)
        {
            eventStore.Record(Event($"tx_recent_{i}", "C1", "M1", 9000m, DateTimeOffset.Parse("2027-06-01T00:00:00Z").AddDays(i)) with
            {
                DeviceId = "BrandNewDevice",
                Location = "Lagos"
            });
        }

        var currentHistory = eventStore.GetCustomerHistory("C1");
        var currentProfile = BehaviourProfileBuilder.BuildCustomerProfile("C1", currentHistory);
        var persistedBaseline = profileStore.GetSnapshotClosestTo("C1", DateTimeOffset.Parse("2027-01-01T00:00:00Z"))!;

        var deviations = BehaviourDeviationService.CompareCustomerProfiles(persistedBaseline, currentProfile);

        Assert.Contains(deviations, d => d.Aspect == "SPENDING_RANGE_SHIFT");
        Assert.Contains(deviations, d => d.Aspect == "DEVICE_SET_CHANGED");
        Assert.Contains(deviations, d => d.Aspect == "LOCATION_SET_CHANGED");
    }
}
