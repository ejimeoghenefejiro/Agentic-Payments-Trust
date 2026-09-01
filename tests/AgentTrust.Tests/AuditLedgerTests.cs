using AgentTrust.Core.Models;
using AgentTrust.Evidence;
using Xunit;

namespace AgentTrust.Tests;

public class AuditLedgerTests
{
    private static AuditRecord MakeRecord(string txId, string agentId = "agt_1") => new(
        txId, agentId, "org_1", "auth_1",
        new EvidenceManifest(txId, new List<EvidenceItem> { new("ev_1", "sensor_reading", "reading", true) }, new[] { "sensor_reading" }),
        "policy-v1",
        new PolicyDecisionResult(txId, Decision.Approve, Array.Empty<PolicyCheck>(), "policy-v1", Array.Empty<string>()),
        new PaymentResult(txId, PaymentStatus.Success, "psp_ref", null),
        DateTimeOffset.Parse("2027-06-01T10:00:00Z"),
        "sha256:placeholder");

    [Fact]
    public void FreshChainVerifiesAsValid()
    {
        var ledger = new AuditLedger();
        ledger.Append(MakeRecord("tx_1"));
        ledger.Append(MakeRecord("tx_2"));
        ledger.Append(MakeRecord("tx_3"));

        var result = ledger.Verify();

        Assert.True(result.IsValid);
        Assert.Empty(result.Breaks);
    }

    [Fact]
    public void EachEntryChainsToThePreviousHash()
    {
        var ledger = new AuditLedger();
        var e1 = ledger.Append(MakeRecord("tx_1"));
        var e2 = ledger.Append(MakeRecord("tx_2"));

        Assert.Equal(AuditLedger.GenesisHash, e1.PreviousHash);
        Assert.Equal(e1.CurrentHash, e2.PreviousHash);
        Assert.NotEqual(e1.CurrentHash, e2.CurrentHash);
    }

    [Fact]
    public void DetectsChangedRecordContent()
    {
        var ledger = new AuditLedger();
        ledger.Append(MakeRecord("tx_1"));
        var entries = ledger.Entries.ToList();

        var tampered = entries[0] with { Record = MakeRecord("tx_1") with { AgentId = "attacker_agent" } };
        var reloaded = AuditLedger.LoadExisting(new[] { tampered });

        var result = reloaded.Verify();

        Assert.False(result.IsValid);
        Assert.Contains(result.Breaks, b => b.Contains("hash mismatch") || b.Contains("stored hash"));
    }

    [Fact]
    public void DetectsDeletedRecord()
    {
        var ledger = new AuditLedger();
        ledger.Append(MakeRecord("tx_1"));
        ledger.Append(MakeRecord("tx_2"));
        ledger.Append(MakeRecord("tx_3"));
        var entries = ledger.Entries.ToList();

        // Simulate deletion of the middle record: entry 2 now claims to follow entry 0 directly.
        var withDeletion = new[] { entries[0], entries[2] };
        var reloaded = AuditLedger.LoadExisting(withDeletion);

        var result = reloaded.Verify();

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Breaks);
    }

    [Fact]
    public void DetectsReorderedRecords()
    {
        var ledger = new AuditLedger();
        ledger.Append(MakeRecord("tx_1"));
        ledger.Append(MakeRecord("tx_2"));
        var entries = ledger.Entries.ToList();

        var reordered = new[] { entries[1], entries[0] };
        var reloaded = AuditLedger.LoadExisting(reordered);

        var result = reloaded.Verify();

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Breaks);
    }

    [Fact]
    public void DetectsBrokenChainWhenPreviousHashForged()
    {
        var ledger = new AuditLedger();
        var e1 = ledger.Append(MakeRecord("tx_1"));

        var forged = e1 with { PreviousHash = "sha256:forged" };
        var reloaded = AuditLedger.LoadExisting(new[] { forged });

        var result = reloaded.Verify();

        Assert.False(result.IsValid);
    }
}
