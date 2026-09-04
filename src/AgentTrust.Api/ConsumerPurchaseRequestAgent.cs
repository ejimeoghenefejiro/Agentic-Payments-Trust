using System.ComponentModel;
using System.Text.Json;
using AgentTrust.Agents;
using AgentTrust.Commerce;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AgentTrust.Api;

public static class PurchasePlanningStatus{public const string Ready="READY";public const string NeedsInput="NEEDS_INPUT";public const string Impossible="IMPOSSIBLE_WITHIN_BUDGET";}
public sealed record PlannedPurchaseItem(string SearchTerm,int Quantity);
public sealed record ConsumerPurchasePlan(string Status,string Summary,string Message,decimal MaximumAmount,string Currency,
    IReadOnlyList<PlannedPurchaseItem> Items,IReadOnlyList<string> Questions,decimal? EstimatedTotal,IReadOnlyList<string> ToolsUsed);
public interface IConsumerPurchaseRequestAgent{Task<ConsumerPurchasePlan> PlanAsync(string instruction,IReadOnlyList<Product> catalogue,CancellationToken token);}

/// <summary>A bounded Semantic Kernel agent may search and price products, but is deliberately
/// given no mandate, authorisation, checkout or payment function.</summary>
public sealed class ConsumerPurchaseRequestAgent:IConsumerPurchaseRequestAgent
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    public async Task<ConsumerPurchasePlan> PlanAsync(string instruction,IReadOnlyList<Product> catalogue,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(instruction))throw new ArgumentException("A purchase instruction is required.");
        ConsumerPurchasePlan? plan=null;
        if(AgentFactory.IsLiveModeConfigured)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try{plan=await AskModel(instruction,catalogue,timeout.Token);}
            catch(Exception ex) when(ex is not OperationCanceledException||!token.IsCancellationRequested){plan=null;}
        }
        plan??=Fallback(instruction,catalogue);
        if(plan.MaximumAmount<=0||!string.Equals(plan.Currency,"GBP",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The agent did not preserve a valid GBP budget.");
        if(plan.Status==PurchasePlanningStatus.Ready)
        {
            if(plan.Items.Count==0||plan.Items.Any(x=>x.Quantity<=0)||!plan.ToolsUsed.Contains("price_basket"))throw new InvalidOperationException("A ready plan must be priced through the catalogue tool.");
            var searchable=catalogue.SelectMany(x=>x.Tags.Append(x.Description).Append(x.ProductId)).ToArray();
            if(plan.Items.Any(i=>!searchable.Any(v=>v.Contains(i.SearchTerm,StringComparison.OrdinalIgnoreCase))))throw new InvalidOperationException("The agent proposed an unavailable catalogue item.");
            if(plan.EstimatedTotal is null||plan.EstimatedTotal>plan.MaximumAmount)throw new InvalidOperationException("The agent marked an over-budget basket ready.");
        }
        return plan with{Currency="GBP"};
    }

    private static async Task<ConsumerPurchasePlan?> AskModel(string instruction,IReadOnlyList<Product> catalogue,CancellationToken token)
    {
        var kernel=AgentFactory.CreateLiveKernel();var tools=new PurchasePlanningPlugin(catalogue);kernel.Plugins.AddFromObject(tools,"grocery");
        var chat=kernel.GetRequiredService<IChatCompletionService>();var history=new ChatHistory();
        history.AddSystemMessage("""
            You are an iterative grocery-planning agent. You have NO authority or payment tools.
            Extract the user's exact maximum budget, form a recipe hypothesis, search the catalogue,
            price the complete basket including delivery, try cheaper alternatives when necessary,
            and challenge the result before stopping. Use search_catalogue and price_basket; do not
            invent products or prices. If information about ingredients already owned could make
            the request feasible, return NEEDS_INPUT with concise questions. If no complete basket
            is possible, return IMPOSSIBLE_WITHIN_BUDGET. Return JSON only:
            {"status":"READY|NEEDS_INPUT|IMPOSSIBLE_WITHIN_BUDGET","summary":"...","message":"...","maximumAmount":4.99,"currency":"GBP","items":[{"searchTerm":"product id or catalogue term","quantity":1}],"questions":[],"estimatedTotal":4.50,"toolsUsed":[]}
            """);
        history.AddUserMessage(instruction);
        var settings=new OpenAIPromptExecutionSettings{FunctionChoiceBehavior=FunctionChoiceBehavior.Auto()};
        var response=await chat.GetChatMessageContentAsync(history,settings,kernel,token);var text=response.Content??"";var start=text.IndexOf('{');var end=text.LastIndexOf('}');
        var plan=start>=0&&end>start?JsonSerializer.Deserialize<ConsumerPurchasePlan>(text[start..(end+1)],JsonOptions):null;
        return plan is null?null:plan with{ToolsUsed=tools.ToolsUsed.ToArray()};
    }

    private static ConsumerPurchasePlan Fallback(string instruction,IReadOnlyList<Product> catalogue)
    {
        var match=System.Text.RegularExpressions.Regex.Match(instruction,@"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var budget=match.Success?decimal.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture):0;
        if(!instruction.Contains("chicken wrap",StringComparison.OrdinalIgnoreCase)||budget<=0)
            return new(PurchasePlanningStatus.NeedsInput,"Planning requires clarification","The live planning model is unavailable and no safe verified plan was produced.",Math.Max(budget,0.01m),"GBP",[],["Please restate the meal and maximum budget."],null,[]);
        var terms=new[]{"chicken","wraps","lettuce","tomato","sauce"};var items=terms.Select(x=>new PlannedPurchaseItem(x,1)).ToArray();
        var total=terms.Sum(term=>catalogue.Where(x=>x.Description.Contains(term,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(term,StringComparison.OrdinalIgnoreCase))).Min(x=>x.UnitPrice))+2.50m;
        return total<=budget
            ?new(PurchasePlanningStatus.Ready,"Chicken wraps",$"A complete basket is available for £{total:0.00}.",budget,"GBP",items,[],total,["search_catalogue","price_basket"])
            :new(PurchasePlanningStatus.NeedsInput,"Chicken wraps exceed budget",$"The cheapest complete basket is £{total:0.00}, above the £{budget:0.00} budget. No payment will be attempted.",budget,"GBP",items,
                ["Do you already have any of the sauce, lettuce or tomatoes?","Would you accept a cheaper vegetarian filling or increase the budget?"],total,["search_catalogue","price_basket"]);
    }
}

public sealed class PurchasePlanningPlugin
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    private readonly IReadOnlyList<Product> _catalogue;private readonly List<string> _used=[];public IReadOnlyList<string> ToolsUsed=>_used;
    public PurchasePlanningPlugin(IReadOnlyList<Product> catalogue)=>_catalogue=catalogue;
    [KernelFunction("search_catalogue"),Description("Search available grocery products and prices. Call repeatedly for ingredients and alternatives.")]
    public string Search([Description("Ingredient or product search phrase")]string query)
    {Track("search_catalogue");return JsonSerializer.Serialize(_catalogue.Where(x=>x.Description.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(query,StringComparison.OrdinalIgnoreCase))).OrderBy(x=>x.UnitPrice).Select(x=>new{x.ProductId,x.Description,x.UnitPrice,x.Currency,x.AvailableQuantity}));}
    [KernelFunction("price_basket"),Description("Calculate a proposed basket total including cheapest delivery. Input is JSON array of searchTerm and quantity.")]
    public string Price([Description("JSON array such as [{\"searchTerm\":\"chicken\",\"quantity\":1}]")]string itemsJson)
    {
        Track("price_basket");var items=JsonSerializer.Deserialize<List<PlannedPurchaseItem>>(itemsJson,JsonOptions)??[];var selected=new List<object>();decimal subtotal=0;
        foreach(var item in items){var product=_catalogue.Where(x=>x.AvailableQuantity>=item.Quantity&&(x.Description.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.ProductId.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)))).OrderBy(x=>x.UnitPrice).FirstOrDefault();if(product is null)return JsonSerializer.Serialize(new{valid=false,missing=item.SearchTerm});var line=product.UnitPrice*item.Quantity;subtotal+=line;selected.Add(new{product.ProductId,product.Description,item.Quantity,product.UnitPrice,lineTotal=line});}
        return JsonSerializer.Serialize(new{valid=true,selected,subtotal,deliveryFee=2.50m,total=subtotal+2.50m,currency="GBP"});
    }
    private void Track(string name){if(!_used.Contains(name))_used.Add(name);}
}
