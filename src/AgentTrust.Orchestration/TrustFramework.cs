using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Evidence;
using AgentTrust.Payments;
using AgentTrust.Policy;

namespace AgentTrust.Orchestration;

/// <summary>
/// Wires identity, binding, authority, policy, evidence, payment and audit together
/// into the end-to-end transaction lifecycle described in the PhD concept document
/// (agent proposes -> trust layer authorises -> payment adapter executes -> audit recorded).
/// Every store dependency is optional and defaults to an in-memory implementation, so callers
/// (the scenario runner, unit tests) that only care about the deterministic core don't need to
/// know about persistence; the API layer supplies EF-Core-backed stores instead.
/// </summary>
public sealed class TrustFramework
{
    private readonly IAgentRegistry _agents;
    private readonly IPrincipalBindingStore _bindings;
    private readonly IDelegatedAuthorityStore _authorities;
    private readonly ITransactionLedger _ledger;
    private readonly PolicyEngine _policyEngine;
    private readonly EvidenceService _evidenceService;
    private readonly AuditLedger _auditLedger;
    private readonly ITransactionIntentStore _intentStore;
    private readonly IEvidenceManifestStore _evidenceManifestStore;
    private readonly IPolicyDecisionStore _policyDecisionStore;
    private readonly IPaymentOutcomeStore _paymentOutcomeStore;
    private readonly IApprovalStore _approvalStore;
    private readonly PaymentExecutionCoordinator _paymentExecution;

    public AuditLedger AuditLedger => _auditLedger;
    public ITransactionLedger Ledger => _ledger;

    public TrustFramework(
        IAgentRegistry agents,
        IPrincipalBindingStore bindings,
        IDelegatedAuthorityStore authorities,
        ITransactionLedger ledger,
        IPaymentAdapter paymentAdapter,
        ITransactionIntentStore? intentStore = null,
        IEvidenceManifestStore? evidenceManifestStore = null,
        IPolicyDecisionStore? policyDecisionStore = null,
        IPaymentOutcomeStore? paymentOutcomeStore = null,
        IApprovalStore? approvalStore = null,
        IAuditRecordStore? persistentAuditStore = null,
        IPaymentAttemptStore? paymentAttemptStore = null)
    {
        _agents = agents;
        _bindings = bindings;
        _authorities = authorities;
        _ledger = ledger;
        _policyEngine = new PolicyEngine(agents, bindings, authorities, ledger);
        _evidenceService = new EvidenceService();
        _intentStore = intentStore ?? new InMemoryTransactionIntentStore();
        _evidenceManifestStore = evidenceManifestStore ?? new InMemoryEvidenceManifestStore();
        _policyDecisionStore = policyDecisionStore ?? new InMemoryPolicyDecisionStore();
        _paymentOutcomeStore = paymentOutcomeStore ?? new InMemoryPaymentOutcomeStore();
        _approvalStore = approvalStore ?? new InMemoryApprovalStore();
        _paymentExecution = new PaymentExecutionCoordinator(paymentAdapter, paymentAttemptStore ?? new InMemoryPaymentAttemptStore());
        _auditLedger = persistentAuditStore is null ? new AuditLedger() : new AuditLedger(persistentAuditStore);
    }

    public sealed record Outcome(PolicyDecisionResult PolicyDecision, PaymentResult PaymentResult, AuditRecord Audit, long LatencyMs);

    /// <summary>Evaluates and audits a proposal without executing a payment. Commerce uses this
    /// path to obtain a deterministic decision before issuing a separately bound purchase
    /// authorisation. This prevents policy approval from becoming an accidental double charge.</summary>
    public Outcome EvaluateTransaction(TransactionIntent intent, EvidenceManifest evidenceManifest,
        DelegatedAuthority? transactionScopedAuthority = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _intentStore.Save(intent);
        _evidenceManifestStore.Save(evidenceManifest);
        var policyDecision = _policyEngine.Evaluate(intent, evidenceManifest, transactionScopedAuthority);
        _policyDecisionStore.Save(policyDecision);
        _ledger.Record(intent, policyDecision.Decision);
        var paymentResult = new PaymentResult(intent.TransactionId, PaymentStatus.NotAttempted, string.Empty, null);
        _paymentOutcomeStore.Save(paymentResult);
        if (policyDecision.Decision == Decision.Escalate)
            _approvalStore.Save(new ApprovalRequest(Guid.NewGuid().ToString("N"), intent.TransactionId,
                ApprovalStatus.Pending, DateTimeOffset.UtcNow, Decision.Escalate, null, null, null, null));
        var authorityId = transactionScopedAuthority?.AuthorityId ?? _authorities.FindByAgent(intent.AgentId)?.AuthorityId ?? "unknown";
        var audit = _evidenceService.BuildAuditRecord(intent, authorityId, evidenceManifest,
            policyDecision, paymentResult, DateTimeOffset.UtcNow);
        _auditLedger.Append(audit);
        stopwatch.Stop();
        return new Outcome(policyDecision, paymentResult, audit, stopwatch.ElapsedMilliseconds);
    }

    public Outcome ProcessTransaction(TransactionIntent intent, EvidenceManifest evidenceManifest,
        DelegatedAuthority? transactionScopedAuthority = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _intentStore.Save(intent);
        _evidenceManifestStore.Save(evidenceManifest);

        var policyDecision = _policyEngine.Evaluate(intent, evidenceManifest, transactionScopedAuthority);
        _policyDecisionStore.Save(policyDecision);
        _ledger.Record(intent, policyDecision.Decision);

        PaymentResult paymentResult;
        if (policyDecision.Decision == Decision.Approve)
        {
            paymentResult = _paymentExecution.Submit(intent);
        }
        else
        {
            paymentResult = new PaymentResult(intent.TransactionId, PaymentStatus.NotAttempted, string.Empty, null);
            if (policyDecision.Decision == Decision.Escalate)
            {
                _approvalStore.Save(new ApprovalRequest(
                    Guid.NewGuid().ToString("N"), intent.TransactionId, ApprovalStatus.Pending,
                    DateTimeOffset.UtcNow, Decision.Escalate, null, null, null, null));
            }
        }
        _paymentOutcomeStore.Save(paymentResult);

        var authorityId = transactionScopedAuthority?.AuthorityId ?? _authorities.FindByAgent(intent.AgentId)?.AuthorityId ?? "unknown";
        var audit = _evidenceService.BuildAuditRecord(
            intent, authorityId, evidenceManifest, policyDecision, paymentResult, DateTimeOffset.UtcNow);
        _auditLedger.Append(audit);

        stopwatch.Stop();
        return new Outcome(policyDecision, paymentResult, audit, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Resolves a pending ESCALATE created by ProcessTransaction. On approve, resumes the
    /// original stored intent and executes payment for the first time — the payment adapter
    /// is never invoked before this call for an escalated transaction. On reject, the
    /// transaction is finalised as denied and payment never executes.
    /// </summary>
    public Outcome ResolveApproval(string transactionId, bool approve, string approver, string? reason)
    {
        var approval = _approvalStore.Find(transactionId)
            ?? throw new InvalidOperationException($"No approval request found for transaction {transactionId}.");
        if (approval.Status != ApprovalStatus.Pending)
        {
            throw new InvalidOperationException($"Approval for transaction {transactionId} was already resolved ({approval.Status}).");
        }

        var intent = _intentStore.Find(transactionId)
            ?? throw new InvalidOperationException($"No stored intent found for transaction {transactionId}.");
        var evidenceManifest = _evidenceManifestStore.Find(transactionId)
            ?? throw new InvalidOperationException($"No stored evidence manifest found for transaction {transactionId}.");
        var originalPolicyDecision = _policyDecisionStore.Find(transactionId)
            ?? throw new InvalidOperationException($"No stored policy decision found for transaction {transactionId}.");

        var finalDecision = approve ? Decision.Approve : Decision.Deny;
        var paymentResult = approve
            ? _paymentExecution.Submit(intent)
            : new PaymentResult(transactionId, PaymentStatus.NotAttempted, string.Empty, null);
        _paymentOutcomeStore.Save(paymentResult);
        if (approve)
        {
            _ledger.Record(intent, Decision.Approve);
        }

        var resolvedApproval = approval with
        {
            Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected,
            Approver = approver,
            DecidedAt = DateTimeOffset.UtcNow,
            Reason = reason,
            FinalOutcome = finalDecision
        };
        _approvalStore.Save(resolvedApproval);

        var finalReasonCodes = new List<string>(originalPolicyDecision.ReasonCodes)
        {
            approve ? "HUMAN_APPROVED" : "HUMAN_REJECTED"
        };
        var finalPolicyDecision = originalPolicyDecision with { Decision = finalDecision, ReasonCodes = finalReasonCodes };
        _policyDecisionStore.Save(finalPolicyDecision);

        var authorityId = _authorities.FindByAgent(intent.AgentId)?.AuthorityId ?? "unknown";
        var audit = _evidenceService.BuildAuditRecord(
            intent, authorityId, evidenceManifest, finalPolicyDecision, paymentResult, DateTimeOffset.UtcNow);
        _auditLedger.Append(audit);

        return new Outcome(finalPolicyDecision, paymentResult, audit, 0);
    }

    public ApprovalRequest? FindApproval(string transactionId) => _approvalStore.Find(transactionId);

    public TransactionIntent? FindIntent(string transactionId) => _intentStore.Find(transactionId);

    public PolicyDecisionResult? FindPolicyDecision(string transactionId) => _policyDecisionStore.Find(transactionId);

    public PaymentResult? FindPaymentResult(string transactionId) => _paymentOutcomeStore.Find(transactionId);

    public AuditRecord? FindLatestAudit(string transactionId) =>
        _auditLedger.Entries.LastOrDefault(e => e.Record.TransactionId == transactionId)?.Record;
}
