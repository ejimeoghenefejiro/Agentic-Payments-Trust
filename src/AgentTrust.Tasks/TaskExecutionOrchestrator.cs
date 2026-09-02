using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Orchestration;

namespace AgentTrust.Tasks;

/// <summary>
/// Ties a task's execution to its mandate and the frozen trust layer: AGENT -> TASK -> DELEGATED
/// AUTHORITY -> FINANCIAL MANDATE -> POLICY -> PAYMENT, per the doc's flow diagram. Escalation
/// (context mismatch, or above-limit with RequireApproval) is resolved here rather than by
/// calling the trust layer speculatively — TrustFramework.ProcessTransaction always executes
/// payment immediately on Approve, so nothing may be sent to it until a human has actually
/// approved an escalated case.
/// </summary>
public sealed class TaskExecutionOrchestrator
{
    private readonly IMandateStore _mandates;
    private readonly IMandateUsageTracker _usageTracker;
    private readonly MandateEvaluationService _mandateEvaluationService;
    private readonly IDelegatedAuthorityStore _authorities;
    private readonly TrustFramework _trustFramework;
    private readonly Dictionary<string, PendingExecution> _pendingEscalations = new();

    private sealed record PendingExecution(AgentTask Task, FinancialMandate Mandate, decimal ProposedAmount, MandateCheckResult MandateCheck, DateTimeOffset Now);

    public TaskExecutionOrchestrator(IMandateStore mandates, IMandateUsageTracker usageTracker, IDelegatedAuthorityStore authorities, TrustFramework trustFramework)
    {
        _mandates = mandates;
        _usageTracker = usageTracker;
        _mandateEvaluationService = new MandateEvaluationService(usageTracker);
        _authorities = authorities;
        _trustFramework = trustFramework;
    }

    public TaskExecutionResult Execute(AgentTask task, decimal proposedAmount, IReadOnlyDictionary<string, string> proposedContext, DateTimeOffset now)
    {
        var mandate = _mandates.Find(task.MandateId)
            ?? throw new InvalidOperationException($"No mandate {task.MandateId} found for task {task.TaskId}.");
        var executionId = $"exec_{Guid.NewGuid():N}";
        var mandateCheck = _mandateEvaluationService.Evaluate(mandate, proposedAmount, proposedContext, now);

        if (mandateCheck.Decision == MandateCheckDecision.Block)
        {
            return new TaskExecutionResult(executionId, task.TaskId, mandate.MandateId, proposedAmount,
                TaskExecutionDecision.Deny, mandateCheck.Reasons, mandateCheck, null, PaymentStatus.NotAttempted, false);
        }

        if (mandateCheck.Decision == MandateCheckDecision.Escalate)
        {
            _pendingEscalations[executionId] = new PendingExecution(task, mandate, proposedAmount, mandateCheck, now);
            return new TaskExecutionResult(executionId, task.TaskId, mandate.MandateId, proposedAmount,
                TaskExecutionDecision.Escalate, mandateCheck.Reasons, mandateCheck, null, PaymentStatus.NotAttempted, true);
        }

        return ExecuteThroughTrustLayer(executionId, task, mandate, proposedAmount, mandateCheck, oneOffApprovedAmount: null, now);
    }

    /// <summary>Resolves a pending escalation. On approve, the human's decision covers exactly
    /// this one task execution — it never raises the mandate's own standing limit.</summary>
    public TaskExecutionResult ResolveEscalation(string taskExecutionId, bool approve)
    {
        if (!_pendingEscalations.Remove(taskExecutionId, out var pending))
        {
            throw new InvalidOperationException($"No pending escalation found for execution {taskExecutionId}.");
        }

        if (!approve)
        {
            return new TaskExecutionResult(taskExecutionId, pending.Task.TaskId, pending.Mandate.MandateId, pending.ProposedAmount,
                TaskExecutionDecision.Deny, new[] { "HUMAN_REJECTED" }, pending.MandateCheck, null, PaymentStatus.NotAttempted, false);
        }

        var oneOffAmount = pending.MandateCheck.WithinPerTransactionLimit ? (decimal?)null : pending.ProposedAmount;
        return ExecuteThroughTrustLayer(taskExecutionId, pending.Task, pending.Mandate, pending.ProposedAmount, pending.MandateCheck, oneOffAmount, pending.Now);
    }

    private TaskExecutionResult ExecuteThroughTrustLayer(
        string executionId, AgentTask task, FinancialMandate mandate, decimal proposedAmount,
        MandateCheckResult mandateCheck, decimal? oneOffApprovedAmount, DateTimeOffset now)
    {
        var normalAuthority = MandateToAuthorityMapper.ToAuthority(mandate);
        var authorityForThisCall = oneOffApprovedAmount is decimal amt ? MandateToAuthorityMapper.ToAuthority(mandate, amt) : normalAuthority;
        _authorities.Grant(authorityForThisCall);

        var evidence = new List<EvidenceItem>
        {
            new($"mandate-{mandate.MandateId}", "mandate", $"Mandate for {mandate.Merchant}/{mandate.Purpose}", true),
            new($"task-{task.TaskId}", "task", $"Recurring task {task.TaskType}", true)
        };
        var intent = new TransactionIntent(
            executionId, mandate.AgentId, mandate.PrincipalId, $"purchase:{mandate.Purpose}", mandate.Merchant,
            mandate.Purpose, proposedAmount, $"Mandate-authorised task execution for {task.TaskId}", evidence, now, executionId);
        var manifest = new EvidenceManifest(executionId, evidence, Array.Empty<string>());

        var outcome = _trustFramework.ProcessTransaction(intent, manifest);

        if (oneOffApprovedAmount is not null)
        {
            // A one-off approval must never persist as a standing increase to unattended authority.
            _authorities.Grant(normalAuthority);
        }

        if (outcome.PolicyDecision.Decision == Decision.Approve)
        {
            _usageTracker.RecordSpend(mandate.MandateId, proposedAmount, now);
        }

        var finalDecision = outcome.PolicyDecision.Decision switch
        {
            Decision.Approve => TaskExecutionDecision.Approve,
            Decision.Deny => TaskExecutionDecision.Deny,
            _ => TaskExecutionDecision.Escalate
        };

        return new TaskExecutionResult(executionId, task.TaskId, mandate.MandateId, proposedAmount,
            finalDecision, outcome.PolicyDecision.ReasonCodes, mandateCheck, outcome.PolicyDecision.Decision,
            outcome.PaymentResult.Status, finalDecision == TaskExecutionDecision.Escalate);
    }
}
