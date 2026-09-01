using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Payments;
using AgentTrust.Policy;
using Xunit;

namespace AgentTrust.Tests;

public class PolicyEngineTests
{
    private static (PolicyEngine engine, InMemoryAgentRegistry agents, InMemoryPrincipalBindingStore bindings,
        InMemoryDelegatedAuthorityStore authorities, InMemoryTransactionLedger ledger) BuildEngine()
    {
        var agents = new InMemoryAgentRegistry();
        var bindings = new InMemoryPrincipalBindingStore();
        var authorities = new InMemoryDelegatedAuthorityStore();
        var ledger = new InMemoryTransactionLedger();
        var engine = new PolicyEngine(agents, bindings, authorities, ledger);
        return (engine, agents, bindings, authorities, ledger);
    }

    private static AgentIdentity DefaultIdentity(CredentialStatus status = CredentialStatus.Active) => new(
        "agt_1", "org_1", "procurement", "production", status,
        DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-12-31T00:00:00Z"), "ca");

    private static DelegatedAuthority DefaultAuthority(bool revoked = false, DateOnly? expiry = null) => new(
        "auth_1", "agt_1", new[] { "purchase:fuel" }, 50000, 200000,
        new[] { "ABC Energy" }, new[] { "fuel" }, "NG", null, null, 40000,
        expiry ?? DateOnly.Parse("2027-12-31"), revoked);

    private static TransactionIntent DefaultIntent(decimal amount = 20000, string merchant = "ABC Energy", string action = "purchase:fuel") => new(
        "tx_1", "agt_1", "org_1", action, merchant, "fuel", amount, "test_reason",
        new[] { new EvidenceItem("ev_1", "sensor_reading", "reading", true) },
        DateTimeOffset.Parse("2027-06-01T10:00:00Z"), "idem_1");

    private static EvidenceManifest DefaultEvidence(TransactionIntent intent, IReadOnlyList<string>? required = null) =>
        new(intent.TransactionId, intent.Evidence.ToList(), required ?? new[] { "sensor_reading" });

    [Fact]
    public void ApprovesLegitimateTransaction()
    {
        var (engine, agents, bindings, authorities, _) = BuildEngine();
        agents.Register(DefaultIdentity());
        bindings.Bind(new PrincipalBinding("agt_1", "org_1", DateTimeOffset.UtcNow, true, "ref"));
        authorities.Grant(DefaultAuthority());
        var intent = DefaultIntent();

        var result = engine.Evaluate(intent, DefaultEvidence(intent));

        Assert.Equal(Decision.Approve, result.Decision);
    }

    [Fact]
    public void DeniesWhenCredentialRevoked()
    {
        var (engine, agents, bindings, authorities, _) = BuildEngine();
        agents.Register(DefaultIdentity(CredentialStatus.Revoked));
        bindings.Bind(new PrincipalBinding("agt_1", "org_1", DateTimeOffset.UtcNow, true, "ref"));
        authorities.Grant(DefaultAuthority());
        var intent = DefaultIntent();

        var result = engine.Evaluate(intent, DefaultEvidence(intent));

        Assert.Equal(Decision.Deny, result.Decision);
        Assert.Contains("IDENTITY_INVALID", result.ReasonCodes);
    }

    [Fact]
    public void DeniesWhenAmountExceedsPerTransactionLimit()
    {
        var (engine, agents, bindings, authorities, _) = BuildEngine();
        agents.Register(DefaultIdentity());
        bindings.Bind(new PrincipalBinding("agt_1", "org_1", DateTimeOffset.UtcNow, true, "ref"));
        authorities.Grant(DefaultAuthority());
        var intent = DefaultIntent(amount: 60000);

        var result = engine.Evaluate(intent, DefaultEvidence(intent));

        Assert.Equal(Decision.Deny, result.Decision);
        Assert.Contains("TRANSACTION_LIMIT_EXCEEDED", result.ReasonCodes);
    }

    [Fact]
    public void EscalatesWhenAboveHumanApprovalThreshold()
    {
        var (engine, agents, bindings, authorities, _) = BuildEngine();
        agents.Register(DefaultIdentity());
        bindings.Bind(new PrincipalBinding("agt_1", "org_1", DateTimeOffset.UtcNow, true, "ref"));
        authorities.Grant(DefaultAuthority());
        var intent = DefaultIntent(amount: 45000);

        var result = engine.Evaluate(intent, DefaultEvidence(intent));

        Assert.Equal(Decision.Escalate, result.Decision);
        Assert.Contains("HUMAN_APPROVAL_REQUIRED", result.ReasonCodes);
    }

    [Fact]
    public void DeniesActionOutsideDelegatedScope()
    {
        var (engine, agents, bindings, authorities, _) = BuildEngine();
        agents.Register(DefaultIdentity());
        bindings.Bind(new PrincipalBinding("agt_1", "org_1", DateTimeOffset.UtcNow, true, "ref"));
        authorities.Grant(DefaultAuthority());
        var intent = DefaultIntent(action: "transfer:funds");

        var result = engine.Evaluate(intent, DefaultEvidence(intent));

        Assert.Equal(Decision.Deny, result.Decision);
        Assert.Contains("ACTION_OUT_OF_SCOPE", result.ReasonCodes);
    }

    [Fact]
    public void DeniesDuplicateIdempotentTransaction()
    {
        var (engine, agents, bindings, authorities, ledger) = BuildEngine();
        agents.Register(DefaultIdentity());
        bindings.Bind(new PrincipalBinding("agt_1", "org_1", DateTimeOffset.UtcNow, true, "ref"));
        authorities.Grant(DefaultAuthority());
        var intent = DefaultIntent();
        ledger.Record(intent, Decision.Approve);

        var result = engine.Evaluate(intent, DefaultEvidence(intent));

        Assert.Equal(Decision.Deny, result.Decision);
        Assert.Contains("DUPLICATE_TRANSACTION", result.ReasonCodes);
    }

    [Fact]
    public void EscalatesWhenEvidenceMissing()
    {
        var (engine, agents, bindings, authorities, _) = BuildEngine();
        agents.Register(DefaultIdentity());
        bindings.Bind(new PrincipalBinding("agt_1", "org_1", DateTimeOffset.UtcNow, true, "ref"));
        authorities.Grant(DefaultAuthority());
        var intent = DefaultIntent();
        var evidence = new EvidenceManifest(intent.TransactionId, new List<EvidenceItem>(), new[] { "sensor_reading" });

        var result = engine.Evaluate(intent, evidence);

        Assert.Equal(Decision.Escalate, result.Decision);
        Assert.Contains("EVIDENCE_INSUFFICIENT", result.ReasonCodes);
    }

    [Fact]
    public void EnforcesDailyAggregateLimitAcrossTransactions()
    {
        var (engine, agents, bindings, authorities, ledger) = BuildEngine();
        agents.Register(DefaultIdentity());
        bindings.Bind(new PrincipalBinding("agt_1", "org_1", DateTimeOffset.UtcNow, true, "ref"));
        authorities.Grant(DefaultAuthority());
        var priorIntent = DefaultIntent(amount: 190000) with { TransactionId = "tx_0", IdempotencyKey = "idem_0" };
        ledger.Record(priorIntent, Decision.Approve);
        var intent = DefaultIntent(amount: 20000);

        var result = engine.Evaluate(intent, DefaultEvidence(intent));

        Assert.Equal(Decision.Deny, result.Decision);
        Assert.Contains("DAILY_LIMIT_EXCEEDED", result.ReasonCodes);
    }
}
