using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Orchestration;
using AgentTrust.Payments;
using Xunit;

namespace AgentTrust.Tests;

public class ApprovalWorkflowTests
{
    private static (TrustFramework Framework, MockPaymentAdapter Adapter) BuildFramework()
    {
        var agents = new InMemoryAgentRegistry();
        var bindings = new InMemoryPrincipalBindingStore();
        var authorities = new InMemoryDelegatedAuthorityStore();
        var ledger = new InMemoryTransactionLedger();
        var adapter = new MockPaymentAdapter();

        agents.Register(new AgentIdentity("agt_1", "org_1", "procurement", "production",
            CredentialStatus.Active, DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-12-31T00:00:00Z"), "ca"));
        bindings.Bind(new PrincipalBinding("agt_1", "org_1", DateTimeOffset.UtcNow, true, "ref"));
        authorities.Grant(new DelegatedAuthority("auth_1", "agt_1", new[] { "purchase:fuel" }, 50000, 200000,
            new[] { "ABC Energy" }, new[] { "fuel" }, "NG", null, null, 40000, DateOnly.Parse("2027-12-31"), false));

        var framework = new TrustFramework(agents, bindings, authorities, ledger, adapter);
        return (framework, adapter);
    }

    private static TransactionIntent EscalatingIntent() => new(
        "tx_escalate", "agt_1", "org_1", "purchase:fuel", "ABC Energy", "fuel", 45000, "large purchase",
        new[] { new EvidenceItem("ev_1", "sensor_reading", "reading", true) },
        DateTimeOffset.Parse("2027-06-01T10:00:00Z"), "idem_escalate");

    [Fact]
    public void EscalationCreatesPendingApprovalAndNeverCallsPaymentAdapter()
    {
        var (framework, adapter) = BuildFramework();
        var intent = EscalatingIntent();
        var manifest = new EvidenceManifest(intent.TransactionId, intent.Evidence.ToList(), new[] { "sensor_reading" });

        var outcome = framework.ProcessTransaction(intent, manifest);

        Assert.Equal(Decision.Escalate, outcome.PolicyDecision.Decision);
        Assert.Equal(PaymentStatus.NotAttempted, outcome.PaymentResult.Status);
        Assert.Empty(adapter.SubmittedTransactionIds);

        var approval = framework.FindApproval(intent.TransactionId);
        Assert.NotNull(approval);
        Assert.Equal(ApprovalStatus.Pending, approval!.Status);
        Assert.Equal(Decision.Escalate, approval.OriginalDecision);
    }

    [Fact]
    public void ApprovingResumesAndExecutesPaymentExactlyOnce()
    {
        var (framework, adapter) = BuildFramework();
        var intent = EscalatingIntent();
        var manifest = new EvidenceManifest(intent.TransactionId, intent.Evidence.ToList(), new[] { "sensor_reading" });
        framework.ProcessTransaction(intent, manifest);

        var outcome = framework.ResolveApproval(intent.TransactionId, approve: true, approver: "supervisor@example.com", reason: "confirmed with finance");

        Assert.Equal(Decision.Approve, outcome.PolicyDecision.Decision);
        Assert.Equal(PaymentStatus.Success, outcome.PaymentResult.Status);
        Assert.Single(adapter.SubmittedTransactionIds);
        Assert.Contains("HUMAN_APPROVED", outcome.PolicyDecision.ReasonCodes);

        var approval = framework.FindApproval(intent.TransactionId);
        Assert.Equal(ApprovalStatus.Approved, approval!.Status);
        Assert.Equal("supervisor@example.com", approval.Approver);
        Assert.Equal(Decision.Approve, approval.FinalOutcome);
    }

    [Fact]
    public void RejectingFinalisesAsDeniedAndNeverExecutesPayment()
    {
        var (framework, adapter) = BuildFramework();
        var intent = EscalatingIntent();
        var manifest = new EvidenceManifest(intent.TransactionId, intent.Evidence.ToList(), new[] { "sensor_reading" });
        framework.ProcessTransaction(intent, manifest);

        var outcome = framework.ResolveApproval(intent.TransactionId, approve: false, approver: "supervisor@example.com", reason: "not justified");

        Assert.Equal(Decision.Deny, outcome.PolicyDecision.Decision);
        Assert.Equal(PaymentStatus.NotAttempted, outcome.PaymentResult.Status);
        Assert.Empty(adapter.SubmittedTransactionIds);

        var approval = framework.FindApproval(intent.TransactionId);
        Assert.Equal(ApprovalStatus.Rejected, approval!.Status);
        Assert.Equal(Decision.Deny, approval.FinalOutcome);
    }

    [Fact]
    public void CannotResolveTheSameApprovalTwice()
    {
        var (framework, _) = BuildFramework();
        var intent = EscalatingIntent();
        var manifest = new EvidenceManifest(intent.TransactionId, intent.Evidence.ToList(), new[] { "sensor_reading" });
        framework.ProcessTransaction(intent, manifest);
        framework.ResolveApproval(intent.TransactionId, approve: true, approver: "a", reason: null);

        Assert.Throws<InvalidOperationException>(() => framework.ResolveApproval(intent.TransactionId, approve: false, approver: "b", reason: null));
    }

    [Fact]
    public void ResolvingNonexistentApprovalThrows()
    {
        var (framework, _) = BuildFramework();
        Assert.Throws<InvalidOperationException>(() => framework.ResolveApproval("tx_does_not_exist", approve: true, approver: "a", reason: null));
    }
}
