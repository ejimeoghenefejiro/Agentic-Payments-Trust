using AgentTrust.Commerce;

namespace AgentTrust.Connectors;

public sealed record ProviderQuoteEvaluation(string MerchantId,MerchantPlanningQuote? Quote,string? RejectionReason);
public sealed record ProviderOptimizationResult(MerchantPlanningQuote BestQuote,IReadOnlyList<ProviderQuoteEvaluation> Evaluations,int Attempts=1);

/// <summary>Compares authoritative provider quotes. Failed or incomplete providers remain as evidence.</summary>
public sealed class CommerceProviderOptimizer(IEnumerable<IObjectiveExpansionCapability> expanders)
{
    private readonly IReadOnlyList<IObjectiveExpansionCapability> _expanders=expanders.ToArray();

    public async Task<ProviderOptimizationResult> QuoteBestAsync(string principalId,IEnumerable<ICommerceConnector> providers,
        IReadOnlyList<ProposedMerchantItem> items,decimal maximumAmount,string currency,CancellationToken token=default)
    {
        var ordered=providers.OrderBy(x=>x.MerchantId,StringComparer.OrdinalIgnoreCase).ToArray();
        var evaluations=await Task.WhenAll(ordered.Select(EvaluateAsync));
        var best=evaluations.Where(x=>x.Quote is not null&&x.RejectionReason is null).Select(x=>x.Quote!)
            .OrderBy(x=>x.Total).ThenBy(x=>x.DeliveryFee).ThenByDescending(x=>x.ExpiresAt).FirstOrDefault()
            ??throw new InvalidOperationException("No provider returned a complete quote within the customer constraints.");
        return new(best,evaluations);

        async Task<ProviderQuoteEvaluation> EvaluateAsync(ICommerceConnector provider)
        {
            try
            {
                var quote=await new ConnectorMerchantPlanningToolset(provider,principalId,currency,_expanders).QuoteAsync(items,token);
                var rejection=!string.Equals(quote.Currency,currency,StringComparison.OrdinalIgnoreCase)?"CURRENCY_MISMATCH"
                    :quote.Total>maximumAmount?"BUDGET_EXCEEDED":quote.Items.Count!=items.Count?"INCOMPLETE_QUOTE":null;
                return new(provider.MerchantId,quote,rejection);
            }
            catch(Exception ex) when(ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {return new(provider.MerchantId,null,ex.Message);}
        }
    }

    public async Task<ProviderOptimizationResult> QuoteBestCurrentAsync(string principalId,
        IEnumerable<ICommerceConnector> providers,IReadOnlyList<ProposedMerchantItem> items,
        decimal maximumAmount,string currency,int maximumAttempts=3,CancellationToken token=default)
    {
        if(maximumAttempts is <1 or >10)throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        var materialized=providers.ToArray();
        for(var attempt=1;attempt<=maximumAttempts;attempt++)
        {
            var result=await QuoteBestAsync(principalId,materialized,items,maximumAmount,currency,token);
            var provider=materialized.Single(x=>x.MerchantId.Equals(result.BestQuote.MerchantId,StringComparison.OrdinalIgnoreCase));
            if(await IsCurrentAsync(result.BestQuote,provider,token))return result with{Attempts=attempt};
        }
        throw new InvalidOperationException("PROVIDER_STATE_DID_NOT_STABILISE");
    }

    private static async Task<bool> IsCurrentAsync(MerchantPlanningQuote quote,ICommerceConnector provider,CancellationToken token)
    {
        if(quote.ExpiresAt<=DateTimeOffset.UtcNow)return false;
        foreach(var item in quote.Items)
        {
            var current=await provider.GetProductAsync(item.ProductId,token);
            if(current is null||current.AvailableQuantity<item.Quantity||current.UnitPrice!=item.UnitPrice)return false;
        }
        return true;
    }
}
