using System;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentTrust.Consumer;
using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Orchestration;
using AgentTrust.PaymentMethods;
using AgentTrust.Policy;

namespace AgentTrust.Commerce;

public sealed record PurchaseOrchestrationResult(PurchaseExecution Execution, PurchaseIntent? Intent,
    PurchaseAuthorisation? Authorisation, PurchaseReceipt? Receipt);

/// <summary>The agent builds a proposal; this class crosses into deterministic mandate/policy
/// evaluation. Only a signed, intent-bound authorisation can reach connector checkout.</summary>
public sealed class AgentPurchaseOrchestrator
{
    private readonly IConsumerTaskStore _tasks; private readonly IPurchaseExecutionStore _executions;
    private readonly IMandateStore _mandates; private readonly IMandateUsageTracker _usage;
    private readonly IPaymentMethodStore _paymentMethods; private readonly IDelegatedAuthorityStore _authorities;
    private readonly TrustFramework _trust; private readonly IPurchaseAuthorisationService _authorisations;
    private readonly IPurchaseAuditSink _audit; private readonly LivePurchaseGate _liveGate;
    private readonly IOneOffAuthorisationStore _oneOffs;
    private readonly ICommerceDurability _durability;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPurchase> _pending = new();
    private readonly Dictionary<string, string> _intentHashes = new();
    private sealed record PendingPurchase(PurchaseIntent Intent, ConsumerPurchaseTask Task,
        FinancialMandate Mandate, ICommerceConnector Connector, string Fingerprint, string ReservationId,
        DateTimeOffset CreatedAt, LiveExecutionContext LiveContext);

    public AgentPurchaseOrchestrator(IConsumerTaskStore tasks, IPurchaseExecutionStore executions,
        IMandateStore mandates, IMandateUsageTracker usage, IPaymentMethodStore paymentMethods,
        IDelegatedAuthorityStore authorities, TrustFramework trust,
        IPurchaseAuthorisationService authorisations, IPurchaseAuditSink audit, LivePurchaseGate liveGate,
        IOneOffAuthorisationStore? oneOffs = null, ICommerceDurability? durability = null)
    { _tasks = tasks; _executions = executions; _mandates = mandates; _usage = usage;
      _paymentMethods = paymentMethods; _authorities = authorities; _trust = trust;
      _authorisations = authorisations; _audit = audit; _liveGate = liveGate;
      _oneOffs = oneOffs ?? new InMemoryOneOffAuthorisationStore();
      _durability = durability ?? new NullCommerceDurability(); }

    public async Task<PurchaseOrchestrationResult> RunAsync(string taskId, string authenticatedPrincipalId,
        DateTimeOffset scheduledFor, ICommerceConnector connector, LiveExecutionContext liveContext,
        CancellationToken cancellationToken = default)
    {
        var task = _tasks.FindOwned(taskId, authenticatedPrincipalId)
            ?? throw new UnauthorizedAccessException("Task does not belong to the authenticated principal.");
        var intentId = StableIntentId(task.TaskId, scheduledFor);
        lock (_gate)
        {
            if (_executions.FindByIntent(intentId) is { } existing)
                return new PurchaseOrchestrationResult(existing, null, null, null);
            Save(NewExecution(intentId, task, PurchaseExecutionState.BasketBuilding));
        }
        Audit("TaskTriggered", intentId, task.PrincipalId, null);

        try
        {
            if (task.Status != ConsumerTaskStatus.Active) return Denied(intentId, task, "TASK_INACTIVE");
            if (!task.MerchantScope.Contains(connector.MerchantId)) return Denied(intentId, task, "MERCHANT_OUTSIDE_TASK_SCOPE");
            var mandate = _mandates.Find(task.MandateId);
            if (mandate is null || mandate.PrincipalId != task.PrincipalId || mandate.AgentId != task.AgentId)
                return Denied(intentId, task, "MANDATE_OWNERSHIP_MISMATCH");
            if (!mandate.IsActive(DateTimeOffset.UtcNow)) return Denied(intentId, task, "MANDATE_INACTIVE");
            if (!string.Equals(mandate.Merchant, connector.MerchantId, StringComparison.OrdinalIgnoreCase))
                return Denied(intentId, task, "MANDATE_MERCHANT_MISMATCH");
            var method = _paymentMethods.Find(task.PaymentMethodId);
            if (method is null || method.PrincipalId != task.PrincipalId || method.PaymentMethodId != mandate.PaymentMethodId)
                return Denied(intentId, task, "PAYMENT_METHOD_OWNERSHIP_MISMATCH");
            if (!method.IsUsable(DateOnly.FromDateTime(DateTime.UtcNow))) return Denied(intentId, task, "PAYMENT_METHOD_INACTIVE");

            var basket = await connector.CreateBasketAsync(task.PrincipalId, cancellationToken);
            foreach (var requested in task.ShoppingList)
            {
                var products = await connector.SearchProductsAsync(requested.SearchTerm, cancellationToken);
                var product = requested.PreferredProductId is not null
                    ? products.FirstOrDefault(p => p.ProductId == requested.PreferredProductId)
                    : products.Where(p => requested.MaximumUnitPrice is null || p.UnitPrice <= requested.MaximumUnitPrice)
                        .OrderBy(p => p.UnitPrice).FirstOrDefault();
                if (product is null) return Denied(intentId, task, $"PRODUCT_NOT_FOUND:{requested.SearchTerm}");
                basket = await connector.AddBasketItemAsync(basket.BasketId, product.ProductId, requested.Quantity,
                    task.Preferences.Substitutions != SubstitutionPolicy.Never, cancellationToken);
            }
            Audit("BasketBuilt", intentId, task.PrincipalId, null);
            var deliveries = await connector.GetDeliveryOptionsAsync(basket.BasketId, cancellationToken);
            var delivery = deliveries.OrderBy(x => x.Fee).First();
            await connector.SelectDeliveryOptionAsync(basket.BasketId, delivery.DeliveryOptionId, cancellationToken);
            var quote = await connector.GetQuoteAsync(basket.BasketId, delivery.DeliveryOptionId, cancellationToken);
            var intent = new PurchaseIntent(intentId, task.PrincipalId, task.AgentId, task.MandateId, task.TaskId,
                connector.MerchantId, connector.MerchantName, quote.Currency, quote.Items, quote.Subtotal,
                quote.DeliveryFee, quote.TotalAmount, task.Preferences.DeliveryAddressReference,
                task.Preferences.RequestedDeliveryWindow, task.PaymentMethodId, DateTimeOffset.UtcNow,
                quote.ExpiresAt, intentId);
            lock (_gate) _intentHashes[intentId] = PurchaseIntentCanonicalizer.Hash(intent);
            _durability.SaveIntent(intent, $"pex_{intentId}", mandate.Version);
            Update(intentId, PurchaseExecutionState.Quoted, []);
            Audit("QuoteReceived", intentId, task.PrincipalId, null); Audit("PurchaseIntentCreated", intentId, task.PrincipalId, null);

            var liveFailures = _liveGate.Validate(intent, liveContext);
            if (liveFailures.Count > 0) return Denied(intentId, task, liveFailures.ToArray(), intent);
            var context = new Dictionary<string, string>(mandate.TaskParameters)
            { ["deliveryAddressReference"] = intent.DeliveryAddressReference };
            var mandateCheck = new MandateEvaluationService(_usage).Evaluate(mandate, intent.TotalAmount, context, DateTimeOffset.UtcNow);
            if (mandateCheck.Decision == MandateCheckDecision.Block) return Denied(intentId, task, mandateCheck.Reasons.ToArray(), intent);
            if (!_usage.TryReserve(mandate, intentId, intent.TotalAmount, DateTimeOffset.UtcNow,
                out var reservation, out var reserveReasons, mandateCheck.Decision == MandateCheckDecision.Escalate))
                return Denied(intentId, task, reserveReasons.ToArray(), intent);
            if (mandateCheck.Decision == MandateCheckDecision.Escalate)
            {
                var fingerprint = TransactionFingerprint.Create(mandate, intentId, intent.TotalAmount, intent.Currency, context);
                lock (_gate) _pending[intentId] = new PendingPurchase(intent, task, mandate, connector,
                    fingerprint, reservation!.ReservationId, DateTimeOffset.UtcNow, liveContext);
                Update(intentId, PurchaseExecutionState.AwaitingHumanApproval, mandateCheck.Reasons);
                Audit("TrustEscalated", intentId, task.PrincipalId, null); Audit("HumanApprovalRequested", intentId, task.PrincipalId, null);
                return Current(intentId, intent);
            }
            return await EvaluateAndExecute(intent, task, mandate, connector, reservation!.ReservationId,
                null, cancellationToken);
        }
        catch
        {
            Update(intentId, PurchaseExecutionState.Unknown, ["EXECUTION_OUTCOME_UNKNOWN"]); throw;
        }
    }

    public async Task<PurchaseOrchestrationResult> ResolveAsync(string purchaseIntentId,
        string authenticatedPrincipalId, bool approve, string approver, CancellationToken cancellationToken = default)
    {
        PendingPurchase pending;
        lock (_gate)
        {
            if (!_pending.Remove(purchaseIntentId, out pending!)) throw new InvalidOperationException("No pending purchase.");
        }
        if (pending.Task.PrincipalId != authenticatedPrincipalId) throw new UnauthorizedAccessException("Purchase does not belong to the authenticated principal.");
        if (!approve) { _usage.Release(pending.ReservationId); Update(purchaseIntentId, PurchaseExecutionState.Denied, ["HUMAN_REJECTED"]); return Current(purchaseIntentId, pending.Intent); }
        var oneOff = new OneOffAuthorisation($"ooa_{Guid.NewGuid():N}", purchaseIntentId, pending.Mandate.MandateId,
            pending.Mandate.Version, pending.Fingerprint, pending.Intent.TotalAmount, pending.Intent.Currency,
            pending.Intent.MerchantId, pending.Intent.PaymentMethodReference, approver, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5), OneOffAuthorisationStatus.Active, null);
        _oneOffs.Save(oneOff);
        if (!_oneOffs.TryConsume(oneOff.AuthorisationId, pending.Fingerprint, DateTimeOffset.UtcNow, out _))
            return Denied(purchaseIntentId, pending.Task, "ONE_OFF_AUTHORISATION_INVALID");
        Audit("HumanApprovalGranted", purchaseIntentId, authenticatedPrincipalId, null);
        return await EvaluateAndExecute(pending.Intent, pending.Task, pending.Mandate, pending.Connector,
            pending.ReservationId, pending.Intent.TotalAmount, cancellationToken);
    }

    private async Task<PurchaseOrchestrationResult> EvaluateAndExecute(PurchaseIntent intent,
        ConsumerPurchaseTask task, FinancialMandate mandate, ICommerceConnector connector,
        string reservationId, decimal? oneOffAmount, CancellationToken cancellationToken)
    {
        Update(intent.PurchaseIntentId, PurchaseExecutionState.AwaitingTrustDecision, []);
        Audit("TrustEvaluationStarted", intent.PurchaseIntentId, task.PrincipalId, intent.PurchaseIntentId);
        var normalAuthority = MandateToAuthorityMapper.ToAuthority(mandate); _authorities.Grant(normalAuthority);
        var authority = oneOffAmount is null ? normalAuthority : MandateToAuthorityMapper.ToAuthority(mandate, oneOffAmount.Value);
        var evidence = new[] { new EvidenceItem($"purchase-{intent.PurchaseIntentId}", "purchase_intent", PurchaseIntentCanonicalizer.Hash(intent), true) };
        var tx = new TransactionIntent(intent.PurchaseIntentId, intent.AgentId, intent.PrincipalId,
            $"purchase:{mandate.Purpose}", intent.MerchantId, mandate.Purpose, intent.TotalAmount,
            $"Commerce purchase {intent.PurchaseIntentId}", evidence, DateTimeOffset.UtcNow, intent.IdempotencyKey);
        var outcome = _trust.EvaluateTransaction(tx, new EvidenceManifest(tx.TransactionId, evidence, []),
            oneOffAmount is null ? null : authority);
        if (outcome.PolicyDecision.Decision != Decision.Approve)
        {
            _usage.Release(reservationId);
            var decisionState = outcome.PolicyDecision.Decision == Decision.Escalate ? PurchaseExecutionState.AwaitingHumanApproval : PurchaseExecutionState.Denied;
            Update(intent.PurchaseIntentId, decisionState, outcome.PolicyDecision.ReasonCodes);
            Audit(outcome.PolicyDecision.Decision == Decision.Deny ? "TrustDenied" : "TrustEscalated", intent.PurchaseIntentId, task.PrincipalId, tx.TransactionId);
            return Current(intent.PurchaseIntentId, intent);
        }
        Audit("TrustApproved", intent.PurchaseIntentId, task.PrincipalId, tx.TransactionId);
        var auth = _authorisations.Issue(intent, tx.TransactionId, mandate.Version,
            outcome.PolicyDecision.PolicyVersion, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        _durability.SaveAuthorisation(auth);
        Update(intent.PurchaseIntentId, PurchaseExecutionState.Authorised, []); Audit("PurchaseAuthorisationIssued", intent.PurchaseIntentId, task.PrincipalId, tx.TransactionId);
        await connector.PrepareCheckoutAsync(intent, cancellationToken);
        _durability.SaveCheckout(intent, "Submitted");
        Update(intent.PurchaseIntentId, PurchaseExecutionState.CheckoutSubmitted, []); Audit("PaymentSubmitted", intent.PurchaseIntentId, task.PrincipalId, tx.TransactionId);
        var result = await connector.ExecutePurchaseAsync(intent, auth, cancellationToken);
        var state = result.Status switch { ConnectorPurchaseStatus.Succeeded => PurchaseExecutionState.Purchased,
            ConnectorPurchaseStatus.RequiresAction => PurchaseExecutionState.RequiresAction,
            ConnectorPurchaseStatus.Processing => PurchaseExecutionState.Processing,
            ConnectorPurchaseStatus.Failed => PurchaseExecutionState.Failed, _ => PurchaseExecutionState.Unknown };
        if (state == PurchaseExecutionState.Purchased) _usage.Commit(reservationId);
        else if (state == PurchaseExecutionState.Failed) _usage.Release(reservationId);
        Update(intent.PurchaseIntentId, state, result.FailureReason is null ? [] : [result.FailureReason], result.ProviderReference, result.RequiredAction, tx.TransactionId);
        Audit(state == PurchaseExecutionState.Purchased ? "PurchaseCompleted" : state == PurchaseExecutionState.RequiresAction ? "RequiresAction" : "PurchaseFailed", intent.PurchaseIntentId, task.PrincipalId, tx.TransactionId);
        if (result.Receipt is not null) _durability.SaveReceipt(result.Receipt, task.PrincipalId);
        return new PurchaseOrchestrationResult(_executions.FindByIntent(intent.PurchaseIntentId)!, intent, auth, result.Receipt);
    }

    private PurchaseOrchestrationResult Denied(string id, ConsumerPurchaseTask task, string reason, PurchaseIntent? intent = null) => Denied(id, task, [reason], intent);
    private PurchaseOrchestrationResult Denied(string id, ConsumerPurchaseTask task, IReadOnlyList<string> reasons, PurchaseIntent? intent = null)
    { Update(id, PurchaseExecutionState.Denied, reasons); Audit("TrustDenied", id, task.PrincipalId, null); return Current(id, intent); }
    private PurchaseOrchestrationResult Current(string id, PurchaseIntent? intent) => new(_executions.FindByIntent(id)!, intent, null, null);
    private PurchaseExecution NewExecution(string id, ConsumerPurchaseTask task, PurchaseExecutionState state) =>
        new($"pex_{id}", task.TaskId, task.PrincipalId, id, state, null, null, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private void Save(PurchaseExecution item) => _executions.Save(item);
    private void Update(string id, PurchaseExecutionState state, IReadOnlyList<string> reasons,
        string? provider = null, string? action = null, string? tx = null)
    { var old = _executions.FindByIntent(id) ?? throw new InvalidOperationException("Execution missing."); Save(old with { State = state, Reasons = reasons, ProviderReference = provider ?? old.ProviderReference, RequiredAction = action, TransactionId = tx ?? old.TransactionId, UpdatedAt = DateTimeOffset.UtcNow }); }
    private void Audit(string type, string intent, string principal, string? tx)
    {
        lock (_gate)
            _audit.Append(new PurchaseAuditEvent($"pae_{Guid.NewGuid():N}", type, intent, principal, tx,
                _intentHashes.GetValueOrDefault(intent, "pending"), DateTimeOffset.UtcNow,
                new Dictionary<string, string>()));
    }
    private static string StableIntentId(string task, DateTimeOffset scheduled) => "purchase_" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{task}|{scheduled:O}"))).ToLowerInvariant()[..32];
}
