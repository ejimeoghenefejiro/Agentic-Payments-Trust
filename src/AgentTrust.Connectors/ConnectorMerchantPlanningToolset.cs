using AgentTrust.Commerce;

namespace AgentTrust.Connectors;

/// <summary>Exposes only non-financial discovery and quotation capabilities to a planning agent.
/// Checkout and payment are intentionally absent.</summary>
public sealed class ConnectorMerchantPlanningToolset:IMerchantPlanningToolset
{
    private readonly ICommerceConnector _connector;
    private readonly IReadOnlyList<IObjectiveExpansionCapability> _expanders;
    private readonly string _principalId;
    public ConnectorMerchantPlanningToolset(ICommerceConnector connector,string principalId,string currency,
        IEnumerable<IObjectiveExpansionCapability>? expanders=null)
    {
        _connector=connector;_principalId=principalId;_expanders=(expanders??[]).ToArray();
        Context=new(principalId,connector.MerchantId,connector.MerchantName,currency,
            new HashSet<string>(["search_items","availability","quote_order","delivery_options"],StringComparer.OrdinalIgnoreCase));
    }
    public MerchantPlanningContext Context{get;}
    public Task<IReadOnlyList<Product>> SearchAsync(string concept,CancellationToken cancellationToken=default)=>_connector.SearchProductsAsync(concept,cancellationToken);
    public async Task<ObjectiveExpansion?> ExpandObjectiveAsync(string objective,CancellationToken cancellationToken=default)
    {
        var expander=_expanders.FirstOrDefault(x=>x.CanExpand(objective));
        return expander is null?null:await expander.ExpandAsync(objective,cancellationToken);
    }
    public async Task<MerchantPlanningQuote> QuoteAsync(IReadOnlyList<ProposedMerchantItem> items,CancellationToken cancellationToken=default)
    {
        if(items.Count==0||items.Any(x=>string.IsNullOrWhiteSpace(x.Concept)||x.Quantity<=0))throw new ArgumentException("A quote requires valid merchant items.");
        var basket=await _connector.CreateBasketAsync(_principalId,cancellationToken);
        foreach(var requested in items)
        {
            var matches=await _connector.SearchProductsAsync(requested.Concept,cancellationToken);
            var product=matches.Where(x=>x.AvailableQuantity>=requested.Quantity).OrderBy(x=>x.UnitPrice).FirstOrDefault()
                ??throw new KeyNotFoundException($"No available item matches '{requested.Concept}'.");
            basket=await _connector.AddBasketItemAsync(basket.BasketId,product.ProductId,requested.Quantity,requested.AllowSubstitution,cancellationToken);
        }
        var delivery=(await _connector.GetDeliveryOptionsAsync(basket.BasketId,cancellationToken)).OrderBy(x=>x.Fee).FirstOrDefault()
            ??throw new InvalidOperationException("The merchant returned no delivery option.");
        await _connector.SelectDeliveryOptionAsync(basket.BasketId,delivery.DeliveryOptionId,cancellationToken);
        var quote=await _connector.GetQuoteAsync(basket.BasketId,delivery.DeliveryOptionId,cancellationToken);
        return new(quote.QuoteId,quote.MerchantId,quote.MerchantName,quote.Currency,quote.Items,quote.Subtotal,quote.DeliveryFee,
            0,0,0,quote.TotalAmount,quote.ExpiresAt,quote.DeliveryOptionId);
    }
}

public sealed class GroceryMealObjectiveCapability:IObjectiveExpansionCapability
{
    public string CapabilityName=>"meal_planning";
    public bool CanExpand(string objective)=>objective.Contains("chicken wrap",StringComparison.OrdinalIgnoreCase)
        ||((objective.Contains("groceries for dinner",StringComparison.OrdinalIgnoreCase)
            ||objective.Contains("food for dinner",StringComparison.OrdinalIgnoreCase))
            &&(objective.Contains("use your best judgement",StringComparison.OrdinalIgnoreCase)
                ||objective.Contains("go ahead with the suggested meal",StringComparison.OrdinalIgnoreCase)
                ||objective.Contains("use the suggested meal",StringComparison.OrdinalIgnoreCase)));
    public Task<ObjectiveExpansion?> ExpandAsync(string objective,CancellationToken cancellationToken=default)
    {
        var dinner=objective.Contains("groceries for dinner",StringComparison.OrdinalIgnoreCase)
            ||objective.Contains("food for dinner",StringComparison.OrdinalIgnoreCase);
        return Task.FromResult<ObjectiveExpansion?>(dinner
            ?new(objective,["chicken","rice","lettuce","tomato"],["sauce"],"grocery-dinner-capability:v1")
            :new(objective,["chicken","wraps","lettuce","tomato","sauce"],["cheese","onion","pepper"],"grocery-meal-capability:v1"));
    }
    public Task<ObjectiveClarification?> ClarifyAsync(string objective,CancellationToken cancellationToken=default)
    {
        var dinner=(objective.Contains("groceries",StringComparison.OrdinalIgnoreCase)
                ||objective.Contains("food",StringComparison.OrdinalIgnoreCase))
            &&objective.Contains("dinner",StringComparison.OrdinalIgnoreCase);
        var delegated=objective.Contains("use your best judgement",StringComparison.OrdinalIgnoreCase)
            ||objective.Contains("choose for me",StringComparison.OrdinalIgnoreCase)
            ||objective.Contains("surprise me",StringComparison.OrdinalIgnoreCase)
            ||objective.Contains("go ahead with the suggested meal",StringComparison.OrdinalIgnoreCase)
            ||objective.Contains("use the suggested meal",StringComparison.OrdinalIgnoreCase);
        return Task.FromResult<ObjectiveClarification?>(dinner&&!delegated
            ?new("Dinner choice needed","I can help choose a complete dinner.",
                "Would you like the suggested chicken-and-rice meal, a vegetarian meal, or something else?",
                ["chicken","rice","lettuce","tomato"],"grocery-dinner-clarification:v1")
            :null);
    }
}
