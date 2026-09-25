using System;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using AgentTrust.Consumer;
using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Orchestration;
using AgentTrust.PaymentMethods;
using AgentTrust.Policy;

namespace AgentTrust.Commerce;

public sealed record PurchaseOrchestrationResult(PurchaseExecution Execution, PurchaseIntent? Intent,
    PurchaseAuthorisation? Authorisation, PurchaseReceipt? Receipt,
    CommerceFulfilmentEvidence? Fulfilment = null);

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
    private readonly CommerceGoalLoop _goalLoop = new();
    private readonly ICommerceOodaCycleStore _oodaCycles;
    private readonly IConsumerMemoryService? _memory;
    private readonly CommerceOutcomeLearningService? _outcomeLearning;
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
        IOneOffAuthorisationStore? oneOffs = null, ICommerceDurability? durability = null,
        ICommerceOodaCycleStore? oodaCycles = null,IConsumerMemoryService? memory=null)
    { _tasks = tasks; _executions = executions; _mandates = mandates; _usage = usage;
      _paymentMethods = paymentMethods; _authorities = authorities; _trust = trust;
      _authorisations = authorisations; _audit = audit; _liveGate = liveGate;
      _oneOffs = oneOffs ?? new InMemoryOneOffAuthorisationStore();
      _durability = durability ?? new NullCommerceDurability();
      _oodaCycles = oodaCycles ?? new InMemoryCommerceOodaCycleStore();_memory=memory;
      _outcomeLearning=memory is null?null:new CommerceOutcomeLearningService(memory); }

    public async Task<PurchaseOrchestrationResult> RunAsync(string taskId, string authenticatedPrincipalId,
        DateTimeOffset scheduledFor, ICommerceConnector connector, LiveExecutionContext liveContext,
        CancellationToken cancellationToken = default)
    {
        var task = _tasks.FindOwned(taskId, authenticatedPrincipalId)
            ?? throw new UnauthorizedAccessException("Task does not belong to the authenticated principal.");
        var intentId = StableIntentId(task.TaskId, scheduledFor);
        PurchaseExecution? previousExecution;
        lock (_gate)
        {
            previousExecution=_executions.FindByIntent(intentId);
            if(previousExecution is not null&&previousExecution.State is not(PurchaseExecutionState.Failed or PurchaseExecutionState.Unknown))
                return new PurchaseOrchestrationResult(previousExecution, null, null, null);
            if(previousExecution is null)Save(NewExecution(intentId, task, PurchaseExecutionState.BasketBuilding));
            else Save(previousExecution with{State=PurchaseExecutionState.BasketBuilding,Reasons=[],UpdatedAt=DateTimeOffset.UtcNow});
        }
        Audit("TaskTriggered", intentId, task.PrincipalId, null);

        var priorCycle=_oodaCycles.FindOwned(intentId,task.PrincipalId);
        var ooda = priorCycle is null ? new CommerceOodaCycle(
            $"ooda_{intentId}", task.TaskId, task.PrincipalId, intentId, scheduledFor, 1,
            CommerceOodaStatus.Observing, "[]", "[]", "[]", "{}", "{}", "{}", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            :priorCycle with{CycleNumber=priorCycle.CycleNumber+1,Status=CommerceOodaStatus.Observing,
                Outcome=null,UpdatedAt=DateTimeOffset.UtcNow,Version=priorCycle.Version+1};
        SaveOoda(ooda);
        AppendOodaStep(ooda,CommerceOodaStatus.Observing,"{}","{}","{}",previousExecution is null?"INITIAL_EXECUTION":"RECOVERY_REOBSERVATION");

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

            if (previousExecution?.State == PurchaseExecutionState.Unknown
                && _durability.FindIntentOwned(intentId, task.PrincipalId) is { } recoveryIntent
                && _durability.FindAuthorisationOwned(intentId, task.PrincipalId) is { } recoveryAuthorisation)
                return await RecoverUnknownPaymentAsync(task, mandate, recoveryIntent, recoveryAuthorisation,
                    connector, ooda, cancellationToken);

            Audit("GoalObserved", intentId, task.PrincipalId, null);
            var goals = task.ShoppingList.Select((requested, index) => new CommerceGoal(
                $"{task.TaskId}:{index}", requested.SearchTerm, requested.Quantity,
                requested.PreferredProductId, requested.MaximumUnitPrice,
                task.Preferences.Substitutions, requested.RequiredForOutcome)).ToArray();
            ooda = AdvanceOoda(ooda, CommerceOodaStatus.Orienting, goal: JsonSerializer.Serialize(goals));
            Audit("GoalOriented", intentId, task.PrincipalId, null);
            var basket = await connector.CreateBasketAsync(task.PrincipalId, cancellationToken);
            var proofs = new List<CommerceGoalProof>();
            var observations = new List<object>();
            foreach (var goal in goals)
            {
                var memories=_memory is null?[]:await _memory.RetrieveAsync(task.PrincipalId,goal.SearchTerm,cancellationToken:cancellationToken);
                var rejectedTerms=memories.Where(x=>x.Memory.Polarity==ConsumerMemoryPolarity.Negative).Select(x=>x.Memory.Subject).ToArray();
                var preferredTerms=memories.Where(x=>x.Memory.Polarity==ConsumerMemoryPolarity.Positive).Select(x=>x.Memory.Subject).ToArray();
                var outcome = await _goalLoop.SatisfyAsync(goal, basket, connector, cancellationToken, rejectedTerms,preferredTerms);
                observations.Add(new { goal.GoalId, goal.SearchTerm, Alternatives = outcome.ObservedAlternatives.Select(x => new { x.ProductId, x.Description, x.UnitPrice, x.AvailableQuantity }) });
                if (outcome.Proof is null)
                {
                    if (goal.RequiredForOutcome)
                    {
                        AdvanceOoda(ooda, CommerceOodaStatus.NeedsIntervention,
                            observations: JsonSerializer.Serialize(observations), outcome: $"GOAL_UNSATISFIED:{goal.SearchTerm}");
                        return Denied(intentId, task, $"GOAL_UNSATISFIED:{goal.SearchTerm}");
                    }
                    Audit("GoalAdapted", intentId, task.PrincipalId, null);
                    continue;
                }
                basket = outcome.Basket;
                proofs.Add(outcome.Proof);
            }
            ooda = AdvanceOoda(ooda, CommerceOodaStatus.Deciding,
                observations: JsonSerializer.Serialize(observations), alternatives: JsonSerializer.Serialize(observations),
                decision: JsonSerializer.Serialize(proofs));
            Audit("GoalDecisionMade", intentId, task.PrincipalId, null);
            Audit("BasketBuilt", intentId, task.PrincipalId, null);
            var deliveries = await connector.GetDeliveryOptionsAsync(basket.BasketId, cancellationToken);
            var delivery = deliveries.OrderBy(x => x.Fee).First();
            await connector.SelectDeliveryOptionAsync(basket.BasketId, delivery.DeliveryOptionId, cancellationToken);
            var quote = await connector.GetQuoteAsync(basket.BasketId, delivery.DeliveryOptionId, cancellationToken);
            ooda = AdvanceOoda(ooda, CommerceOodaStatus.Acting, action: JsonSerializer.Serialize(new
            {
                quote.QuoteId, quote.MerchantId, quote.Currency, quote.TotalAmount, quote.ExpiresAt,
                Items = quote.Items.Select(x => new { x.ProductId, x.Quantity, x.UnitPrice })
            }));
            Audit("GoalActed", intentId, task.PrincipalId, null);
            var goalCheck = _goalLoop.Check(goals, proofs, quote, task.MaximumAmount);
            ooda = AdvanceOoda(ooda, CommerceOodaStatus.Verifying, proof: JsonSerializer.Serialize(goalCheck));
            if (!goalCheck.Passed)
            {
                AdvanceOoda(ooda, CommerceOodaStatus.NeedsIntervention, outcome: string.Join(',', goalCheck.Failures));
                return Denied(intentId, task, goalCheck.Failures);
            }
            ooda = AdvanceOoda(ooda, CommerceOodaStatus.ProposalVerified, outcome: "PROPOSAL_VERIFIED");
            Audit("GoalProved", intentId, task.PrincipalId, null);
            Audit("GoalChecked", intentId, task.PrincipalId, null);
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
                _durability.SavePending(intent,fingerprint,reservation!.ReservationId,mandate.Version);
                Update(intentId, PurchaseExecutionState.AwaitingHumanApproval, mandateCheck.Reasons);
                Audit("TrustEscalated", intentId, task.PrincipalId, null); Audit("HumanApprovalRequested", intentId, task.PrincipalId, null);
                return Current(intentId, intent);
            }
            var result=await EvaluateAndExecute(intent, task, mandate, connector, reservation!.ReservationId,
                null, cancellationToken);
            FinaliseOoda(result,ooda);
            return result;
        }
        catch (Exception exception)
        {
            AdvanceOoda(ooda, CommerceOodaStatus.Failed, outcome: $"{exception.GetType().Name}:{exception.Message}");
            Update(intentId, PurchaseExecutionState.Unknown, ["EXECUTION_OUTCOME_UNKNOWN"]); throw;
        }
    }

    public async Task<PurchaseOrchestrationResult> ResolveAsync(string purchaseIntentId,
        string authenticatedPrincipalId, bool approve, string approver, CancellationToken cancellationToken = default,ICommerceConnector? recoveryConnector=null)
    {
        PendingPurchase? pending;
        lock (_gate)
        {
            _pending.Remove(purchaseIntentId, out pending);
        }
        if(pending is null&&_durability.FindPendingOwned(purchaseIntentId,authenticatedPrincipalId)is{} durable)
        {var task=_tasks.FindOwned(durable.Intent.TaskId,authenticatedPrincipalId)??throw new UnauthorizedAccessException();var mandate=_mandates.FindVersion(durable.Intent.MandateId,durable.MandateVersion)??throw new InvalidOperationException("Mandate version missing.");pending=new PendingPurchase(durable.Intent,task,mandate,recoveryConnector??throw new InvalidOperationException("Commerce connector required for durable recovery."),durable.Fingerprint,durable.ReservationId,DateTimeOffset.UtcNow,new(false,false));}
        if(pending is null)throw new InvalidOperationException("No pending purchase.");
        if (pending.Task.PrincipalId != authenticatedPrincipalId) throw new UnauthorizedAccessException("Purchase does not belong to the authenticated principal.");
        if (!approve) { _durability.CompletePending(purchaseIntentId,false,approver);_usage.Release(pending.ReservationId); Update(purchaseIntentId, PurchaseExecutionState.Denied, ["HUMAN_REJECTED"]);var denied=Current(purchaseIntentId,pending.Intent);if(_oodaCycles.FindOwned(purchaseIntentId,authenticatedPrincipalId)is{} rejectedCycle)AdvanceOoda(rejectedCycle,CommerceOodaStatus.NeedsIntervention,outcome:"HUMAN_REJECTED");return denied; }
        var oneOff = new OneOffAuthorisation($"ooa_{Guid.NewGuid():N}", purchaseIntentId, pending.Mandate.MandateId,
            pending.Mandate.Version, pending.Fingerprint, pending.Intent.TotalAmount, pending.Intent.Currency,
            pending.Intent.MerchantId, pending.Intent.PaymentMethodReference, approver, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5), OneOffAuthorisationStatus.Active, null);
        _oneOffs.Save(oneOff);
        if (!_oneOffs.TryConsume(oneOff.AuthorisationId, pending.Fingerprint, DateTimeOffset.UtcNow, out _))
            return Denied(purchaseIntentId, pending.Task, "ONE_OFF_AUTHORISATION_INVALID");
        _durability.CompletePending(purchaseIntentId,true,approver);
        Audit("HumanApprovalGranted", purchaseIntentId, authenticatedPrincipalId, null);
        var result=await EvaluateAndExecute(pending.Intent, pending.Task, pending.Mandate, pending.Connector,
            pending.ReservationId, pending.Intent.TotalAmount, cancellationToken);
        if(_oodaCycles.FindOwned(purchaseIntentId,authenticatedPrincipalId)is{} cycle)FinaliseOoda(result,cycle);
        return result;
    }

    public CommerceOodaCycle RecordFulfilmentOutcome(string purchaseIntentId,string authenticatedPrincipalId,
        CommerceFulfilmentEvidence evidence)
    {
        var execution=_executions.FindByIntent(purchaseIntentId)
            ??throw new KeyNotFoundException("Purchase execution was not found.");
        if(execution.PrincipalId!=authenticatedPrincipalId)throw new UnauthorizedAccessException("Purchase does not belong to the authenticated principal.");
        if(execution.State!=PurchaseExecutionState.Purchased)throw new InvalidOperationException("PURCHASE_NOT_CONFIRMED");
        var intent=_durability.FindIntentOwned(purchaseIntentId,authenticatedPrincipalId)
            ??throw new InvalidOperationException("PURCHASE_INTENT_NOT_FOUND");
        if(evidence.PurchaseIntentId!=intent.PurchaseIntentId||!evidence.ProviderId.Equals(intent.MerchantId,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FULFILMENT_EVIDENCE_BINDING_MISMATCH");
        var receipt=_durability.FindReceiptByPurchaseOwned(purchaseIntentId,authenticatedPrincipalId)
            ??throw new InvalidOperationException("PURCHASE_RECEIPT_NOT_FOUND");
        var cycle=_oodaCycles.FindOwned(purchaseIntentId,authenticatedPrincipalId)
            ??throw new InvalidOperationException("OODA_CYCLE_NOT_FOUND");
        FinaliseOoda(new(execution,intent,null,receipt,evidence),cycle);
        return _oodaCycles.FindOwned(purchaseIntentId,authenticatedPrincipalId)!;
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
        Update(intent.PurchaseIntentId, PurchaseExecutionState.CheckoutSubmitted, []); Audit("PaymentSubmitted", intent.PurchaseIntentId, task.PrincipalId, tx.TransactionId);
        var result = await connector.ExecutePurchaseAsync(intent, auth, cancellationToken);
        var state = result.Status switch { ConnectorPurchaseStatus.Succeeded => PurchaseExecutionState.Purchased,
            ConnectorPurchaseStatus.RequiresAction => PurchaseExecutionState.RequiresAction,
            ConnectorPurchaseStatus.Processing => PurchaseExecutionState.Processing,
            ConnectorPurchaseStatus.Failed => PurchaseExecutionState.Failed, _ => PurchaseExecutionState.Unknown };
        if (state == PurchaseExecutionState.Purchased) _usage.Commit(reservationId);
        else if (state == PurchaseExecutionState.Failed) _usage.Release(reservationId);
        Update(intent.PurchaseIntentId, state, result.FailureReason is null ? [] : [result.FailureReason], result.ProviderReference, result.RequiredAction, tx.TransactionId);
        Audit(state == PurchaseExecutionState.Purchased ? "PaymentConfirmed" : state == PurchaseExecutionState.RequiresAction ? "RequiresAction" : state==PurchaseExecutionState.Processing?"PaymentProcessing":"PurchaseFailed", intent.PurchaseIntentId, task.PrincipalId, tx.TransactionId);
        if(state==PurchaseExecutionState.Purchased)Audit("PurchaseCompleted",intent.PurchaseIntentId,task.PrincipalId,tx.TransactionId);
        if (result.Receipt is not null){_durability.SaveReceipt(result.Receipt, task.PrincipalId);Audit("ReceiptCreated",intent.PurchaseIntentId,task.PrincipalId,tx.TransactionId);}
        return new PurchaseOrchestrationResult(_executions.FindByIntent(intent.PurchaseIntentId)!, intent, auth, result.Receipt,result.Fulfilment);
    }

    private async Task<PurchaseOrchestrationResult> RecoverUnknownPaymentAsync(ConsumerPurchaseTask task,
        FinancialMandate mandate,PurchaseIntent intent,PurchaseAuthorisation authorisation,
        ICommerceConnector connector,CommerceOodaCycle cycle,CancellationToken cancellationToken)
    {
        if (!_usage.TryReserve(mandate,intent.PurchaseIntentId,intent.TotalAmount,DateTimeOffset.UtcNow,
            out var reservation,out var reasons))
            return Denied(intent.PurchaseIntentId,task,reasons.ToArray(),intent);

        Audit("PaymentReconciliationStarted",intent.PurchaseIntentId,task.PrincipalId,authorisation.TransactionId);
        var providerResult=await connector.ExecutePurchaseAsync(intent,authorisation,cancellationToken);
        var state=providerResult.Status switch
        {
            ConnectorPurchaseStatus.Succeeded=>PurchaseExecutionState.Purchased,
            ConnectorPurchaseStatus.RequiresAction=>PurchaseExecutionState.RequiresAction,
            ConnectorPurchaseStatus.Processing=>PurchaseExecutionState.Processing,
            ConnectorPurchaseStatus.Failed=>PurchaseExecutionState.Failed,
            _=>PurchaseExecutionState.Unknown
        };
        if(state==PurchaseExecutionState.Purchased)_usage.Commit(reservation!.ReservationId);
        else if(state==PurchaseExecutionState.Failed)_usage.Release(reservation!.ReservationId);
        Update(intent.PurchaseIntentId,state,providerResult.FailureReason is null?[]:[providerResult.FailureReason],
            providerResult.ProviderReference,providerResult.RequiredAction,authorisation.TransactionId);
        if(providerResult.Receipt is not null)_durability.SaveReceipt(providerResult.Receipt,task.PrincipalId);
        var result=new PurchaseOrchestrationResult(_executions.FindByIntent(intent.PurchaseIntentId)!,intent,
            authorisation,providerResult.Receipt,providerResult.Fulfilment);
        Audit(state==PurchaseExecutionState.Purchased?"PaymentReconciled":"PaymentReconciliationPending",
            intent.PurchaseIntentId,task.PrincipalId,authorisation.TransactionId);
        FinaliseOoda(result,cycle);
        return result;
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
    private CommerceOodaCycle AdvanceOoda(CommerceOodaCycle cycle, CommerceOodaStatus status,
        string? goal = null, string? observations = null, string? alternatives = null,
        string? decision = null, string? action = null, string? proof = null, string? outcome = null)
    {
        var updated = cycle with
        {
            Status = status,
            GoalJson = goal ?? cycle.GoalJson,
            ObservationsJson = observations ?? cycle.ObservationsJson,
            AlternativesJson = alternatives ?? cycle.AlternativesJson,
            DecisionJson = decision ?? cycle.DecisionJson,
            ActionJson = action ?? cycle.ActionJson,
            ProofJson = proof ?? cycle.ProofJson,
            Outcome = outcome ?? cycle.Outcome,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = cycle.Version + 1
        };
        SaveOoda(updated);
        AppendOodaStep(updated,status,goal??decision??action??"{}",outcome??"{}",proof??observations??alternatives??"{}",outcome);
        return updated;
    }
    private void SaveOoda(CommerceOodaCycle cycle) => _oodaCycles.Save(cycle);
    private void AppendOodaStep(CommerceOodaCycle cycle,CommerceOodaStatus phase,string input,string output,string evidence,string? reason)
    {
        var sequence=_oodaCycles.StepsOwned(cycle.CycleId,cycle.PrincipalId).Count(x=>x.CycleNumber==cycle.CycleNumber)+1;
        _oodaCycles.Append(new($"ooda_step_{Guid.NewGuid():N}",cycle.CycleId,cycle.PrincipalId,cycle.CycleNumber,
            sequence,phase,input,output,evidence,reason,DateTimeOffset.UtcNow));
    }
    private void FinaliseOoda(PurchaseOrchestrationResult result,CommerceOodaCycle cycle)
    {
        var fulfilmentAccepted=result.Fulfilment?.Status is FulfilmentStatus.Accepted or FulfilmentStatus.Preparing
            or FulfilmentStatus.ReadyForPickup or FulfilmentStatus.CourierRequested or FulfilmentStatus.CourierAssigned
            or FulfilmentStatus.CourierArriving or FulfilmentStatus.Collected or FulfilmentStatus.OutForDelivery
            or FulfilmentStatus.Delivered or FulfilmentStatus.CollectedByCustomer;
        var fulfilmentBound=result.Intent is not null&&result.Fulfilment?.PurchaseIntentId==result.Intent.PurchaseIntentId&&
            string.Equals(result.Fulfilment?.ProviderId,result.Intent.MerchantId,StringComparison.OrdinalIgnoreCase);
        var completed=result.Execution.State==PurchaseExecutionState.Purchased&&result.Receipt is not null&&fulfilmentAccepted&&fulfilmentBound;
        AdvanceOoda(cycle,completed?CommerceOodaStatus.Completed:CommerceOodaStatus.Verifying,
            proof:JsonSerializer.Serialize(new{Payment=result.Execution.State,result.Execution.ProviderReference,
                Receipt=result.Receipt?.ReceiptId,Fulfilment=result.Fulfilment?.FulfilmentId,FulfilmentStatus=result.Fulfilment?.Status}),
            outcome:completed?"GOAL_COMPLETED_PAYMENT_FULFILMENT_RECEIPT_PROVED":result.Execution.State.ToString());
        if(completed&&_outcomeLearning is not null&&result.Intent is not null&&result.Receipt is not null&&result.Fulfilment is not null)
            _outcomeLearning.Learn(new(cycle.PrincipalId,cycle.PurchaseIntentId,result.Intent.MerchantId,
                result.Intent.BasketItems,JsonSerializer.Deserialize<CommerceGoalProof[]>(cycle.DecisionJson)??[],
                result.Receipt.ReceiptId,result.Fulfilment.FulfilmentId,result.Fulfilment.Status));
    }
    private static string StableIntentId(string task, DateTimeOffset scheduled) => "purchase_" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{task}|{scheduled:O}"))).ToLowerInvariant()[..32];
}
