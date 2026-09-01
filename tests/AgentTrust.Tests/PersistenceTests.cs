using AgentTrust.Core.Models;
using AgentTrust.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTrust.Tests;

/// <summary>
/// Exercises the EF-Core-backed stores against a real relational database (SQLite, in-memory
/// mode) rather than the domain in-memory dictionaries. Production wiring targets PostgreSQL
/// (Npgsql) — the DbContext and value converters are provider-agnostic, so these tests
/// validate the same mapping code that runs against Postgres.
/// </summary>
public class PersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentTrustDbContext _db;

    public PersistenceTests()
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

    [Fact]
    public void AgentSurvivesRoundTripThroughDatabase()
    {
        var store = new EfAgentRegistry(_db);
        var identity = new AgentIdentity("agt_1", "org_1", "procurement", "production",
            CredentialStatus.Active, DateTimeOffset.Parse("2027-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2027-12-31T00:00:00Z"), "ca");

        store.Register(identity);

        var reloaded = new EfAgentRegistry(new AgentTrustDbContext(
            new DbContextOptionsBuilder<AgentTrustDbContext>().UseSqlite(_connection).Options)).Find("agt_1");

        Assert.NotNull(reloaded);
        Assert.Equal(identity, reloaded);
    }

    [Fact]
    public void DelegatedAuthorityPersistsCollectionsAsJson()
    {
        var store = new EfDelegatedAuthorityStore(_db);
        var authority = new DelegatedAuthority(
            "auth_1", "agt_1", new[] { "purchase:fuel", "purchase:utilities" }, 50000, 200000,
            new[] { "ABC Energy", "XYZ Fuel" }, new[] { "fuel" }, "NG", null, null, 40000,
            DateOnly.Parse("2027-12-31"), false);

        store.Grant(authority);

        var reloaded = new EfDelegatedAuthorityStore(_db).FindByAgent("agt_1");

        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Permissions.Count);
        Assert.Contains("purchase:utilities", reloaded.Permissions);
        Assert.Contains("XYZ Fuel", reloaded.ApprovedMerchants);
    }

    [Fact]
    public void RevokingAuthorityPersists()
    {
        var store = new EfDelegatedAuthorityStore(_db);
        store.Grant(new DelegatedAuthority("auth_2", "agt_2", new[] { "purchase:fuel" }, 1000, 5000,
            Array.Empty<string>(), Array.Empty<string>(), "NG", null, null, 900, DateOnly.Parse("2027-12-31"), false));

        store.Revoke("auth_2");

        var reloaded = new EfDelegatedAuthorityStore(_db).FindByAgent("agt_2");
        Assert.True(reloaded!.Revoked);
    }

    [Fact]
    public void TransactionIntentAndEvidenceManifestRoundTrip()
    {
        var intentStore = new EfTransactionIntentStore(_db);
        var evidenceStore = new EfEvidenceManifestStore(_db);
        var evidence = new List<EvidenceItem> { new("ev_1", "sensor_reading", "reading", true) };
        var intent = new TransactionIntent("tx_1", "agt_1", "org_1", "purchase:fuel", "ABC Energy",
            "fuel", 20000, "reason", evidence, DateTimeOffset.Parse("2027-06-01T10:00:00Z"), "idem_1");
        var manifest = new EvidenceManifest("tx_1", evidence, new[] { "sensor_reading" });

        intentStore.Save(intent);
        evidenceStore.Save(manifest);

        var reloadedIntent = new EfTransactionIntentStore(_db).Find("tx_1");
        var reloadedManifest = new EfEvidenceManifestStore(_db).Find("tx_1");

        Assert.NotNull(reloadedIntent);
        Assert.Equal(intent.TransactionId, reloadedIntent!.TransactionId);
        Assert.Equal(intent.Amount, reloadedIntent.Amount);
        Assert.Equal(intent.Merchant, reloadedIntent.Merchant);
        Assert.Equal(intent.IdempotencyKey, reloadedIntent.IdempotencyKey);
        Assert.Single(reloadedIntent.Evidence);
        Assert.Equal("ev_1", reloadedIntent.Evidence.First().EvidenceId);
        Assert.Equal(1.0, reloadedManifest!.Precision);
    }

    [Fact]
    public void PolicyDecisionAndPaymentOutcomeRoundTrip()
    {
        var policyStore = new EfPolicyDecisionStore(_db);
        var paymentStore = new EfPaymentOutcomeStore(_db);

        var decision = new PolicyDecisionResult("tx_2", Decision.Approve,
            new[] { new PolicyCheck("IdentityValid", true, "ok") }, "policy-v1", Array.Empty<string>());
        policyStore.Save(decision);

        var payment = new PaymentResult("tx_2", PaymentStatus.Success, "psp_ref", null);
        paymentStore.Save(payment);

        Assert.Equal(Decision.Approve, new EfPolicyDecisionStore(_db).Find("tx_2")!.Decision);
        Assert.Equal(PaymentStatus.Success, new EfPaymentOutcomeStore(_db).Find("tx_2")!.Status);
    }

    [Fact]
    public void ApprovalRequestRoundTripsAndUpdatesInPlace()
    {
        var store = new EfApprovalStore(_db);
        var pending = new ApprovalRequest("appr_1", "tx_3", ApprovalStatus.Pending,
            DateTimeOffset.Parse("2027-06-01T10:00:00Z"), Decision.Escalate, null, null, null, null);
        store.Save(pending);

        var resolved = pending with
        {
            Status = ApprovalStatus.Approved,
            Approver = "supervisor@example.com",
            DecidedAt = DateTimeOffset.Parse("2027-06-01T11:00:00Z"),
            Reason = "confirmed with finance",
            FinalOutcome = Decision.Approve
        };
        store.Save(resolved);

        var reloaded = new EfApprovalStore(_db).Find("tx_3");
        Assert.Equal(ApprovalStatus.Approved, reloaded!.Status);
        Assert.Equal("supervisor@example.com", reloaded.Approver);
    }

    [Fact]
    public void TransactionLedgerAmountSpentTodayTranslatesToSql()
    {
        // Regression test: the original implementation used DateOnly.FromDateTime(...) inside
        // the LINQ query, which SQL Server's EF Core provider cannot translate (throws
        // InvalidOperationException at query execution). This must run against a real
        // relational provider (not an in-memory list) to catch that class of bug.
        var intentStore = new EfTransactionIntentStore(_db);
        var policyStore = new EfPolicyDecisionStore(_db);
        var ledger = new EfTransactionLedger(_db);
        var day = DateOnly.Parse("2027-06-01");
        var withinDay = new DateTimeOffset(2027, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var nextDay = new DateTimeOffset(2027, 6, 2, 10, 0, 0, TimeSpan.Zero);

        void SaveApproved(string txId, decimal amount, DateTimeOffset requestedAt)
        {
            intentStore.Save(new TransactionIntent(txId, "agt_ledger", "org_1", "purchase:fuel", "ABC Energy", "fuel", amount, "r", new List<EvidenceItem>(), requestedAt, null));
            policyStore.Save(new PolicyDecisionResult(txId, Decision.Approve, Array.Empty<PolicyCheck>(), "v1", Array.Empty<string>()));
        }

        SaveApproved("tx_a", 10000, withinDay);
        SaveApproved("tx_b", 5000, withinDay);
        SaveApproved("tx_c", 9999, nextDay); // different day, must not be counted

        var total = ledger.AmountSpentToday("agt_ledger", day);

        Assert.Equal(15000m, total);
    }

    [Fact]
    public void AuditRecordStoreLoadsChainInSequenceOrder()
    {
        var auditStore = new EfAuditRecordStore(_db);
        var ledger = new AgentTrust.Evidence.AuditLedger(auditStore);

        for (var i = 0; i < 3; i++)
        {
            var record = new AuditRecord($"tx_{i}", "agt_1", "org_1", "auth_1",
                new EvidenceManifest($"tx_{i}", new List<EvidenceItem>(), Array.Empty<string>()),
                "policy-v1",
                new PolicyDecisionResult($"tx_{i}", Decision.Approve, Array.Empty<PolicyCheck>(), "policy-v1", Array.Empty<string>()),
                new PaymentResult($"tx_{i}", PaymentStatus.Success, "ref", null),
                DateTimeOffset.Parse("2027-06-01T10:00:00Z").AddMinutes(i),
                "sha256:unused");
            ledger.Append(record);
        }

        var reloadedEntries = new EfAuditRecordStore(_db).LoadAll();
        var reloadedLedger = AgentTrust.Evidence.AuditLedger.LoadExisting(reloadedEntries);
        var verification = reloadedLedger.Verify();

        Assert.Equal(3, reloadedEntries.Count);
        Assert.True(verification.IsValid);
        Assert.Equal("tx_0", reloadedEntries[0].Record.TransactionId);
        Assert.Equal("tx_2", reloadedEntries[2].Record.TransactionId);
    }
}
