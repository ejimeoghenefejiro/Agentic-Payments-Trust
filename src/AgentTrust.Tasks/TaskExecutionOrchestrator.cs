using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Orchestration;
using AgentTrust.PaymentMethods;

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
    private readonly IPaymentMethodStore? _paymentMethods;
    private readonly IOneOffAuthorisationStore _oneOffAuthorisations;
    private readonly Dictionary<string, PendingExecution> _pendingEscalations = new();

    private sealed record PendingExecution(AgentTask Task, FinancialMandate Mandate, decimal ProposedAmount,
        string Currency, IReadOnlyDictionary<string, string> Context, MandateCheckResult MandateCheck,
        DateTimeOffset Now, string Fingerprint);

    public TaskExecutionOrchestrator(IMandateStore mandates, IMandateUsageTracker usageTracker,
        IDelegatedAuthorityStore authorities, TrustFramework trustFramework,
        IPaymentMethodStore? paymentMethods = null, IOneOffAuthorisationStore? oneOffAuthorisations = null)
    {
        _mandates = mandates;
        _usageTracker = usageTracker;
        _mandateEvaluationService = new MandateEvaluationService(usageTracker);
        _authorities = authorities;
        _trustFramework = trustFramework;
        _paymentMethods = paymentMethods;
        _oneOffAuthorisations = oneOffAuthorisations ?? new InMemoryOneOffAuthorisationStore();
    }

    public TaskExecutionResult Execute(AgentTask task, decimal proposedAmount,
        IReadOnlyDictionary<string, string> proposedContext, DateTimeOffset now, string? proposedCurrency = null)
    {
        var mandate = _mandates.Find(task.MandateId)
            ?? throw new InvalidOperationException($"No mandate {task.MandateId} found for task {task.TaskId}.");
        var executionId = $"exec_{Guid.NewGuid():N}";
        var currency = proposedCurrency ?? mandate.Currency;
        var invariantFailures = ValidateInvariants(task, mandate, proposedAmount, currency, now);
        if (invariantFailures.Count > 0)
        {
            var blocked = new MandateCheckResult(MandateCheckDecision.Block, invariantFailures, false, false, false, false, false);
            return new TaskExecutionResult(executionId, task.TaskId, mandate.MandateId, proposedAmount,
                TaskExecutionDecision.Deny, invariantFailures, blocked, null, PaymentStatus.NotAttempted, false);
        }
        var mandateCheck = _mandateEvaluationService.Evaluate(mandate, proposedAmount, proposedContext, now);

        if (mandateCheck.Decision == MandateCheckDecision.Block)
        {
            return new TaskExecutionResult(executionId, task.TaskId, mandate.MandateId, proposedAmount,
                TaskExecutionDecision.Deny, mandateCheck.Reasons, mandateCheck, null, PaymentStatus.NotAttempted, false);
        }

        if (mandateCheck.Decision == MandateCheckDecision.Escalate)
        {
            var fingerprint = TransactionFingerprint.Create(mandate, executionId, proposedAmount, currency, proposedContext);
            _pendingEscalations[executionId] = new PendingExecution(task, mandate, proposedAmount, currency,
                new Dictionary<string, string>(proposedContext), mandateCheck, now, fingerprint);
            return new TaskExecutionResult(executionId, task.TaskId, mandate.MandateId, proposedAmount,
                TaskExecutionDecision.Escalate, mandateCheck.Reasons, mandateCheck, null, PaymentStatus.NotAttempted, true);
        }

        return ExecuteThroughTrustLayer(executionId, task, mandate, proposedAmount, mandateCheck, null, now);
    }

    /// <summary>Resolves a pending escalation. On approve, the human's decision covers exactly
    /// this one task execution — it never raises the mandate's own standing limit.</summary>
    public TaskExecutionResult ResolveEscalation(string taskExecutionId, bool approve,
        string approver = "legacy-human", string? approvedFingerprint = null)
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

        if (approvedFingerprint is not null && approvedFingerprint != pending.Fingerprint)
            return new TaskExecutionResult(taskExecutionId, pending.Task.TaskId, pending.Mandate.MandateId, pending.ProposedAmount,
                TaskExecutionDecision.Deny, new[] { "APPROVED_CONTEXT_CHANGED" }, pending.MandateCheck, null, PaymentStatus.NotAttempted, false);

        var authorisation = new OneOffAuthorisation($"ooa_{Guid.NewGuid():N}", taskExecutionId,
            pending.Mandate.MandateId, pending.Mandate.Version, pending.Fingerprint, pending.ProposedAmount,
            pending.Currency, pending.Mandate.Merchant, pending.Mandate.PaymentMethodId, approver,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), OneOffAuthorisationStatus.Active, null);
        _oneOffAuthorisations.Save(authorisation);
        if (!_oneOffAuthorisations.TryConsume(authorisation.AuthorisationId, pending.Fingerprint,
                DateTimeOffset.UtcNow, out _))
            return new TaskExecutionResult(taskExecutionId, pending.Task.TaskId, pending.Mandate.MandateId, pending.ProposedAmount,
                TaskExecutionDecision.Deny, new[] { "ONE_OFF_AUTHORISATION_INVALID" }, pending.MandateCheck, null, PaymentStatus.NotAttempted, false);

        var oneOffAmount = pending.MandateCheck.WithinPerTransactionLimit ? (decimal?)null : pending.ProposedAmount;
        return ExecuteThroughTrustLayer(taskExecutionId, pending.Task, pending.Mandate,
            pending.ProposedAmount, pending.MandateCheck, oneOffAmount, pending.Now);
    }

    private TaskExecutionResult ExecuteThroughTrustLayer(
        string executionId, AgentTask task, FinancialMandate mandate, decimal proposedAmount,
        MandateCheckResult mandateCheck, decimal? oneOffApprovedAmount, DateTimeOffset now)
    {
        var normalAuthority = MandateToAuthorityMapper.ToAuthority(mandate);
        var authorityForThisCall = oneOffApprovedAmount is decimal amt ? MandateToAuthorityMapper.ToAuthority(mandate, amt) : normalAuthority;
        _authorities.Grant(normalAuthority);

        if (!_usageTracker.TryReserve(mandate, executionId, proposedAmount, now, out var reservation,
                out var reservationFailures, oneOffLimitOverride: oneOffApprovedAmount is not null))
            return new TaskExecutionResult(executionId, task.TaskId, mandate.MandateId, proposedAmount,
                TaskExecutionDecision.Deny, reservationFailures, mandateCheck, null, PaymentStatus.NotAttempted, false);

        var evidence = new List<EvidenceItem>
        {
            new($"mandate-{mandate.MandateId}", "mandate", $"Mandate for {mandate.Merchant}/{mandate.Purpose}", true),
            new($"task-{task.TaskId}", "task", $"Recurring task {task.TaskType}", true)
        };
        var intent = new TransactionIntent(
            executionId, mandate.AgentId, mandate.PrincipalId, $"purchase:{mandate.Purpose}", mandate.Merchant,
            mandate.Purpose, proposedAmount, $"Mandate-authorised task execution for {task.TaskId}", evidence, now, executionId);
        var manifest = new EvidenceManifest(executionId, evidence, Array.Empty<string>());

        var outcome = _trustFramework.ProcessTransaction(intent, manifest,
            oneOffApprovedAmount is null ? null : authorityForThisCall);

        if (outcome.PolicyDecision.Decision == Decision.Approve && outcome.PaymentResult.Status == PaymentStatus.Success)
            _usageTracker.Commit(reservation!.ReservationId);
        else
            _usageTracker.Release(reservation!.ReservationId);

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

    public string? GetPendingFingerprint(string executionId) =>
        _pendingEscalations.TryGetValue(executionId, out var pending) ? pending.Fingerprint : null;

    private List<string> ValidateInvariants(AgentTask task, FinancialMandate mandate,
        decimal amount, string currency, DateTimeOffset now)
    {
        var failures = new List<string>();
        if (amount <= 0) failures.Add("AMOUNT_MUST_BE_POSITIVE");
        if (task.Status != AgentTaskStatus.Active) failures.Add("TASK_INACTIVE");
        if (task.AgentId != mandate.AgentId) failures.Add("TASK_AGENT_MISMATCH");
        if (task.PrincipalId != mandate.PrincipalId) failures.Add("TASK_PRINCIPAL_MISMATCH");
        if (!string.Equals(currency, mandate.Currency, StringComparison.OrdinalIgnoreCase)) failures.Add("CURRENCY_MISMATCH");
        if (!mandate.IsActive(now)) failures.Add("MANDATE_INACTIVE");
        if (_paymentMethods is not null)
        {
            var method = _paymentMethods.Find(mandate.PaymentMethodId);
            if (method is null) failures.Add("PAYMENT_METHOD_NOT_FOUND");
            else
            {
                if (method.PrincipalId != mandate.PrincipalId) failures.Add("PAYMENT_METHOD_PRINCIPAL_MISMATCH");
                if (!method.IsUsable(DateOnly.FromDateTime(now.UtcDateTime))) failures.Add("PAYMENT_METHOD_INACTIVE");
            }
        }
        return failures;
    }
}
