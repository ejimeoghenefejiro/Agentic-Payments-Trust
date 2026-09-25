using AgentTrust.Commerce;

namespace AgentTrust.Connectors;

public sealed record ProviderQuoteEvaluation(string MerchantId,MerchantPlanningQuote? Quote,string? RejectionReason);
public sealed record ProviderOptimizationResult(MerchantPlanningQuote BestQuote,IReadOnlyList<ProviderQuoteEvaluation> Evaluations);

/// <summary>Compares authoritative provider quotes. Failed or incomplete providers remain as evidence.</summary>
public sealed class CommerceProviderOptimizer(IEnumerable<IObjectiveExpansionCapability> expanders)
{
    private readonly IReadOnlyList<IObjectiveExpansionCapability> _expanders=expanders.ToArray();

    public async Task<ProviderOptimizationResult> QuoteBestAsync(string principalId,IEnumerable<ICommerceConnector> providers,
        IReadOnlyList<ProposedMerchantItem> items,decimal maximumAmount,string currency,CancellationToken token=default)
    {
        var evaluations=new List<ProviderQuoteEvaluation>();
        foreach(var provider in providers.OrderBy(x=>x.MerchantId,StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var quote=await new ConnectorMerchantPlanningToolset(provider,principalId,currency,_expanders).QuoteAsync(items,token);
                var rejection=!string.Equals(quote.Currency,currency,StringComparison.OrdinalIgnoreCase)?"CURRENCY_MISMATCH"
                    :quote.Total>maximumAmount?"BUDGET_EXCEEDED":quote.Items.Count!=items.Count?"INCOMPLETE_QUOTE":null;
                evaluations.Add(new(provider.MerchantId,quote,rejection));
            }
            catch(Exception ex) when(ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {evaluations.Add(new(provider.MerchantId,null,ex.Message));}
        }
        var best=evaluations.Where(x=>x.Quote is not null&&x.RejectionReason is null).Select(x=>x.Quote!)
            .OrderBy(x=>x.Total).ThenBy(x=>x.ExpiresAt).FirstOrDefault()
            ??throw new InvalidOperationException("No provider returned a complete quote within the customer constraints.");
        return new(best,evaluations);
    }
}
