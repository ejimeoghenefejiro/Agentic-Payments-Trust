using System.Security.Cryptography;
using System.Text;
using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Consumer;
using AgentTrust.Mandates;
using AgentTrust.PaymentMethods;

namespace AgentTrust.Api;

public sealed record ConsumerCommercePreparation(
    ConsumerPurchasePlan Plan,
    MerchantPlanningQuote? Quote,
    FinancialMandate? Mandate,
    PaymentMethod? PaymentMethod,
    IReadOnlyList<Product> Catalogue,
    string? FailureCode = null,
    string? FailureMessage = null)
{
    public bool IsExecutable => Plan.Status == PurchasePlanningStatus.Ready && Quote is not null &&
                                Mandate is not null && PaymentMethod is not null && FailureCode is null;
}

/// <summary>
/// Coordinates customer intent, provider capabilities and silent deterministic readiness checks.
/// It can prepare an action, but it cannot authorise or execute a payment.
/// </summary>
public sealed class ConsumerCommerceAgent
{
    private readonly IConsumerPurchaseRequestAgent _planner;
    private readonly MerchantConnectorRegistry _connectors;
    private readonly IConsumerPlanningStore _planning;
    private readonly IMandateStore _mandates;
    private readonly IPaymentMethodStore _paymentMethods;
    private readonly IReadOnlyList<IObjectiveExpansionCapability> _objectiveCapabilities;

    public ConsumerCommerceAgent(IConsumerPurchaseRequestAgent planner,MerchantConnectorRegistry connectors,
        IConsumerPlanningStore planning,IMandateStore mandates,IPaymentMethodStore paymentMethods,
        IEnumerable<IObjectiveExpansionCapability> objectiveCapabilities)
    {
        _planner=planner;_connectors=connectors;_planning=planning;_mandates=mandates;
        _paymentMethods=paymentMethods;_objectiveCapabilities=objectiveCapabilities.ToArray();
    }

    public async Task<ConsumerCommercePreparation> PrepareAsync(ConsumerActionPlanningContext context,
        DateTimeOffset now,CancellationToken cancellationToken)
    {
        var plan=await _planner.PlanAsync(context,cancellationToken);
        if(plan.Status!=PurchasePlanningStatus.Ready)
            return new(plan,null,null,null,[]);

        MerchantPlanningQuote? quote=null;ICommerceConnector? selectedConnector=null;
        try
        {
            var permittedMerchants=_mandates.FindByPrincipal(context.PrincipalId).Where(x=>x.IsActive(now)&&
                string.Equals(x.Currency,plan.Currency,StringComparison.OrdinalIgnoreCase))
                .Select(x=>x.Merchant).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var providers=_connectors.All.Where(x=>permittedMerchants.Contains(x.MerchantId)).ToArray();
            var requested=plan.Items.Select(x=>new ProposedMerchantItem(x.SearchTerm,x.Quantity,true)).ToArray();
            var optimized=await new CommerceProviderOptimizer(_objectiveCapabilities).QuoteBestCurrentAsync(
                context.PrincipalId,providers,requested,plan.MaximumAmount,plan.Currency,3,cancellationToken);
            quote=optimized.BestQuote;selectedConnector=_connectors.GetRequired(quote.MerchantId);
        }
        catch(KeyNotFoundException ex)
        {
            return Failed(plan,"MERCHANT_ITEM_UNAVAILABLE",ex.Message);
        }
        catch(InvalidOperationException ex)
        {
            return Failed(plan,"MERCHANT_QUOTE_UNAVAILABLE",ex.Message);
        }

        if(quote.Total>plan.MaximumAmount)
        {
            var overBudget=plan with{Status=PurchasePlanningStatus.NeedsInput,InteractionDecision=PurchaseInteractionDecision.Clarify,
                Summary="Merchant quote exceeds budget",Message=$"The authoritative merchant total is £{quote.Total:0.00}, above your £{plan.MaximumAmount:0.00} budget.",
                EstimatedTotal=quote.Total,Questions=["Would you like to revise the basket or budget?"]};
            return new(overBudget,quote,null,null,[]);
        }

        plan=plan with{Currency=quote.Currency,Items=quote.Items.Select(x=>new PlannedPurchaseItem(x.ProductId,x.Quantity)).ToArray(),
            EstimatedTotal=quote.Total,Message=$"{quote.MerchantName} verified the complete order at £{quote.Total:0.00}, including £{quote.DeliveryFee:0.00} delivery. Shall I go ahead?",
            Questions=plan.InteractionDecision==PurchaseInteractionDecision.Execute?[]:["Shall I go ahead?"]};

        var catalogue=await selectedConnector.SearchProductsAsync("",cancellationToken);
        _planning.ReplaceReservations(plan.ConversationId!,quote.Items.Select(item=>new ConsumerProductReservation(
            $"product_hold_{Guid.NewGuid():N}",plan.ConversationId!,item.ProductId,item.Quantity,item.UnitPrice,
            quote.Currency,"Reserved",now,quote.ExpiresAt)).ToArray());

        var mandate=_mandates.FindByPrincipal(context.PrincipalId)
            .Where(x=>x.IsActive(now)&&string.Equals(x.Merchant,quote.MerchantId,StringComparison.OrdinalIgnoreCase)&&
                      string.Equals(x.Currency,quote.Currency,StringComparison.OrdinalIgnoreCase)&&x.PerTransactionLimit>=quote.Total)
            .OrderBy(x=>x.PerTransactionLimit).FirstOrDefault();
        if(mandate is null)return Failed(plan,"NO_SUITABLE_ACTIVE_MANDATE","No active mandate covers the verified total.",quote,catalogue);
        var method=_paymentMethods.Find(mandate.PaymentMethodId);
        if(method is null||method.PrincipalId!=context.PrincipalId||!method.IsUsable(DateOnly.FromDateTime(now.UtcDateTime)))
            return Failed(plan,"NO_USABLE_PAYMENT_METHOD","The mandate does not have an owned, usable payment method.",quote,catalogue);
        var finalAudit=CommerceFinalProposalAudit.Validate(plan,quote,now);
        if(finalAudit.Count>0)return Failed(plan,"FINAL_PROPOSAL_AUDIT_FAILED",string.Join(',',finalAudit),quote,catalogue);
        var auditTools=plan.ToolsUsed.Where(x=>!x.StartsWith("auditor:proposal-hash:",StringComparison.Ordinal)
            &&!x.Equals("auditor:final-controls-accepted",StringComparison.OrdinalIgnoreCase)).ToList();
        if(!auditTools.Contains("auditor:accepted",StringComparer.OrdinalIgnoreCase))auditTools.Add("auditor:accepted");
        auditTools.Add("auditor:final-controls-accepted");
        var auditedPlan=plan with{ToolsUsed=auditTools.ToArray()};
        auditTools.Add($"auditor:proposal-hash:{CommerceProposalFingerprint.Hash(auditedPlan,quote)}");
        plan=auditedPlan with{ToolsUsed=auditTools.ToArray()};
        return new(plan,quote,mandate,method,catalogue);
    }

    public ConsumerPurchaseTask PreparePurchase(ConsumerCommercePreparation prepared,string principalId,string instruction,DateTimeOffset now)
    {
        if(!prepared.IsExecutable)throw new InvalidOperationException("The purchase is not ready for execution.");
        var plan=prepared.Plan;var mandate=prepared.Mandate!;var method=prepared.PaymentMethod!;
        return new ConsumerPurchaseTask(StableTaskId(plan.ConversationId!),principalId,mandate.AgentId,
            new HashSet<string>([prepared.Quote!.MerchantId],StringComparer.OrdinalIgnoreCase),"OneOff","Europe/London",plan.MaximumAmount,plan.Currency,
            plan.Items.Select(x=>new ShoppingListItem(x.SearchTerm,x.Quantity,prepared.Catalogue.FirstOrDefault(p=>p.ProductId.Equals(x.SearchTerm,StringComparison.OrdinalIgnoreCase))?.ProductId)).ToList(),
            new PurchasePreference(mandate.TaskParameters.GetValueOrDefault("deliveryAddressReference")??"default-delivery-address",null,SubstitutionPolicy.SameOrLowerPrice,new Dictionary<string,string>{{"instruction",instruction}}),
            mandate.MandateId,method.PaymentMethodId,ConsumerTaskStatus.Active,now,now);
    }

    private static ConsumerCommercePreparation Failed(ConsumerPurchasePlan plan,string code,string message,MerchantPlanningQuote? quote=null,IReadOnlyList<Product>? catalogue=null)
    {
        var clarified=plan with{Status=PurchasePlanningStatus.NeedsInput,InteractionDecision=PurchaseInteractionDecision.Clarify,
            Summary=code,Message=message,Items=[],Questions=["Would you like me to revise the request?"]};
        return new(clarified,quote,null,null,catalogue??[],code,message);
    }

    private static string StableTaskId(string conversationId)
    {
        var hash=SHA256.HashData(Encoding.UTF8.GetBytes(conversationId));
        return $"ctask_{Convert.ToHexString(hash.AsSpan(0,16)).ToLowerInvariant()}";
    }
}
