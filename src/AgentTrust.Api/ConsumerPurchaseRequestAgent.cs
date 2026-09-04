using System.ComponentModel;
using System.Text.Json;
using AgentTrust.Agents;
using AgentTrust.Commerce;
using AgentTrust.Consumer;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentTrust.Api;

public static class PurchasePlanningStatus{public const string Ready="READY";public const string NeedsInput="NEEDS_INPUT";public const string Impossible="IMPOSSIBLE_WITHIN_BUDGET";}
public static class PurchaseInteractionDecision{public const string Execute="EXECUTE";public const string Clarify="CLARIFY";public const string Propose="PROPOSE";}
public sealed record PlannedPurchaseItem(string SearchTerm,int Quantity);
public sealed record ConsumerPurchasePlan(string Status,string Summary,string Message,decimal MaximumAmount,string Currency,
    IReadOnlyList<PlannedPurchaseItem> Items,IReadOnlyList<string> Questions,decimal? EstimatedTotal,IReadOnlyList<string> ToolsUsed,string? ConversationId=null,int ReasoningTurns=0,
    string InteractionDecision=PurchaseInteractionDecision.Clarify,bool HasSubstitutions=false);
public sealed record ConsumerPlanningState(string Objective,Dictionary<string,string> Constraints,List<string> Hypotheses,List<string> OpenQuestions,
    List<string> AttemptedBaskets,List<string> RejectedAlternatives,List<string> ToolHistory,string Status,ConsumerPurchasePlan? LatestPlan);
public interface IConsumerPurchaseRequestAgent{Task<ConsumerPurchasePlan> PlanAsync(string principalId,string? conversationId,string instruction,IReadOnlyList<Product> catalogue,CancellationToken token);}

/// <summary>A bounded Semantic Kernel agent may search and price products, but is deliberately
/// given no mandate, authorisation, checkout or payment function.</summary>
public sealed class ConsumerPurchaseRequestAgent:IConsumerPurchaseRequestAgent
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    private readonly IConsumerPlanningStore _store;private readonly bool _allowDeterministicFallback;private const int MaximumToolCalls=12;private const int MaximumReasoningTurns=8;
    public ConsumerPurchaseRequestAgent(IConsumerPlanningStore store,IConfiguration configuration){_store=store;_allowDeterministicFallback=configuration.GetValue("ConsumerPilot:Planning:AllowDeterministicFallback",false);}
    public async Task<ConsumerPurchasePlan> PlanAsync(string principal,string? conversationId,string instruction,IReadOnlyList<Product> catalogue,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(instruction))throw new ArgumentException("A purchase instruction is required.");PurchasePlanningPlugin.ClearCalls();
        var now=DateTimeOffset.UtcNow;var policy=ApplyPolicyInstruction(_store.GetPolicy(principal),instruction,now);_store.SavePolicy(policy);var conversation=conversationId is null?null:_store.FindOwned(conversationId,principal);
        if(conversationId is not null&&conversation is null)throw new UnauthorizedAccessException("Conversation not found or belongs to another principal.");
        var state=conversation is null?NewState(instruction):JsonSerializer.Deserialize<ConsumerPlanningState>(conversation.StateJson,JsonOptions)??NewState(instruction);
        foreach(var preference in _store.Preferences(principal))state.Constraints.TryAdd(preference.Key,preference.Value);LearnConstraints(state.Constraints,instruction);conversation??=_store.Create(principal,instruction,JsonSerializer.Serialize(state),now);
        foreach(var constraint in state.Constraints)_store.Remember(principal,constraint.Key,constraint.Value,conversation.ConversationId,now);
        var sequence=_store.Turns(conversation.ConversationId).Count+1;_store.Append(new($"planning_turn_{Guid.NewGuid():N}",conversation.ConversationId,sequence++,"user","message",instruction,null,null,null,now));
        ConsumerPurchasePlan? plan=null;
        if(AgentFactory.IsLiveModeConfigured)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try{plan=await AskModel(state,instruction,catalogue,timeout.Token);}
            catch(Exception ex) when(ex is not OperationCanceledException||!token.IsCancellationRequested){plan=null;}
        }
        plan??=_allowDeterministicFallback?Fallback(string.Join("\n",new[]{state.Objective,instruction}),catalogue):Unavailable(instruction);
        if(plan.MaximumAmount<=0||!string.Equals(plan.Currency,"GBP",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The agent did not preserve a valid GBP budget.");
        if(plan.Status==PurchasePlanningStatus.Ready)
        {
            if(plan.Items.Count==0||plan.Items.Any(x=>x.Quantity<=0)||!plan.ToolsUsed.Contains("price_basket"))throw new InvalidOperationException("A ready plan must be priced through the catalogue tool.");
            var searchable=catalogue.SelectMany(x=>x.Tags.Append(x.Description).Append(x.ProductId)).ToArray();
            if(plan.Items.Any(i=>!searchable.Any(v=>v.Contains(i.SearchTerm,StringComparison.OrdinalIgnoreCase))))throw new InvalidOperationException("The agent proposed an unavailable catalogue item.");
            if(plan.EstimatedTotal is null||plan.EstimatedTotal>plan.MaximumAmount)throw new InvalidOperationException("The agent marked an over-budget basket ready.");
            var violation=FindConstraintViolation(plan,state.Constraints,catalogue);
            if(violation is not null)
                plan=plan with{Status=PurchasePlanningStatus.NeedsInput,Message=violation,Items=[],Questions=["Please confirm an acceptable alternative or revise the constraint."]};
        }
        var explicitlyApproved=ContainsAny(instruction,"use your best judgement and proceed","proceed with this basket","confirm purchase","go ahead and pay");
        var decision=plan.Status!=PurchasePlanningStatus.Ready?PurchaseInteractionDecision.Clarify
            :policy.ShowBasketBeforePayment&&!explicitlyApproved||policy.AskBeforeSubstitutions&&plan.HasSubstitutions&&!explicitlyApproved?PurchaseInteractionDecision.Propose
            :PurchaseInteractionDecision.Execute;
        plan=plan with{Currency="GBP",ConversationId=conversation.ConversationId,ReasoningTurns=plan.ReasoningTurns==0?plan.ToolsUsed.Count:plan.ReasoningTurns,InteractionDecision=decision};
        foreach(var call in PurchasePlanningPlugin.LastCalls){_store.Append(new($"planning_turn_{Guid.NewGuid():N}",conversation.ConversationId,sequence++,"tool","evidence",call.Output,call.Name,call.Input,call.Output,DateTimeOffset.UtcNow));state.ToolHistory.Add(call.Name);if(call.Name=="price_basket")state.AttemptedBaskets.Add(call.Output);}
        state.OpenQuestions.Clear();state.OpenQuestions.AddRange(plan.Questions);state.Hypotheses.Add(plan.Summary);if(plan.Status!=PurchasePlanningStatus.Ready&&plan.EstimatedTotal is not null)state.RejectedAlternatives.Add(plan.Message);state=state with{Status=plan.Status,LatestPlan=plan};
        _store.Append(new($"planning_turn_{Guid.NewGuid():N}",conversation.ConversationId,sequence,"assistant","decision",plan.Message,null,null,null,DateTimeOffset.UtcNow));
        _store.Save(conversation with{Status=plan.Status,StateJson=JsonSerializer.Serialize(state),UpdatedAt=DateTimeOffset.UtcNow,Version=conversation.Version+1});
        if(plan.Status==PurchasePlanningStatus.Ready)Reserve(conversation.ConversationId,plan,catalogue,DateTimeOffset.UtcNow);
        return plan;
    }

    private static async Task<ConsumerPurchasePlan?> AskModel(ConsumerPlanningState state,string instruction,IReadOnlyList<Product> catalogue,CancellationToken token)
    {
        var kernel=AgentFactory.CreateLiveKernel();var tools=new PurchasePlanningPlugin(catalogue,MaximumToolCalls,state.Constraints);kernel.Plugins.AddFromObject(tools,"grocery");
        var chat=kernel.GetRequiredService<IChatCompletionService>();var history=new ChatHistory();
        history.AddSystemMessage("""
            You are an iterative grocery-planning agent. You have NO authority or payment tools.
            Extract the user's exact maximum budget, form a recipe hypothesis, search the catalogue,
            price the complete basket including delivery, try cheaper alternatives when necessary,
            and challenge the result before stopping. Use search_catalogue and price_basket; do not
            invent products or prices. If information about ingredients already owned could make
            the request feasible, return NEEDS_INPUT with concise questions. If no complete basket
            is possible, stop. On each reasoning turn return exactly one JSON action:
            {"action":"SEARCH","query":"ingredient"}
            {"action":"PRICE","items":[{"searchTerm":"catalogue term","quantity":1}]}
            {"action":"FINAL","plan":{"status":"READY|NEEDS_INPUT|IMPOSSIBLE_WITHIN_BUDGET","summary":"...","message":"...","maximumAmount":4.99,"currency":"GBP","items":[],"questions":[],"estimatedTotal":4.50,"toolsUsed":[],"hasSubstitutions":false}}
            READY is allowed only after PRICE evidence proves the complete basket is within budget.
            Set hasSubstitutions=true whenever the proposed basket replaces a requested or preferred item.
            """);
        history.AddUserMessage($"Persistent investigation state:\n{JsonSerializer.Serialize(state)}\n\nLatest user message:\n{instruction}");
        for(var turn=1;turn<=MaximumReasoningTurns;turn++)
        {
            var response=await chat.GetChatMessageContentAsync(history,kernel:kernel,cancellationToken:token);var text=response.Content??"";var start=text.IndexOf('{');var end=text.LastIndexOf('}');if(start<0||end<=start)return null;var json=text[start..(end+1)];
            using var document=JsonDocument.Parse(json);var root=document.RootElement;var action=root.GetProperty("action").GetString()?.ToUpperInvariant();history.AddAssistantMessage(json);
            if(action=="SEARCH"){var query=root.GetProperty("query").GetString()??"";history.AddUserMessage($"TOOL search_catalogue RESULT: {tools.Search(query)}");continue;}
            if(action=="PRICE"){var items=root.GetProperty("items").GetRawText();history.AddUserMessage($"TOOL price_basket RESULT: {tools.Price(items)}");continue;}
            if(action=="FINAL"&&root.TryGetProperty("plan",out var planJson)){var plan=JsonSerializer.Deserialize<ConsumerPurchasePlan>(planJson.GetRawText(),JsonOptions);return plan is null?null:plan with{ToolsUsed=tools.ToolsUsed.ToArray(),ReasoningTurns=turn};}
            return null;
        }
        return new(PurchasePlanningStatus.NeedsInput,"Reasoning limit reached","The agent reached the maximum of eight reasoning turns without sufficient evidence. No payment will be attempted.",ExtractBudget(instruction),"GBP",[],["Please simplify or clarify the request."],null,tools.ToolsUsed.ToArray(),null,MaximumReasoningTurns);
    }

    private void Reserve(string conversationId,ConsumerPurchasePlan plan,IReadOnlyList<Product> catalogue,DateTimeOffset now)
    {
        var rows=new List<ConsumerProductReservation>();foreach(var item in plan.Items){var p=catalogue.Where(x=>x.AvailableQuantity>=item.Quantity&&(x.ProductId.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.Description.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)))).OrderBy(x=>x.UnitPrice).First();rows.Add(new($"product_hold_{Guid.NewGuid():N}",conversationId,p.ProductId,item.Quantity,p.UnitPrice,p.Currency,"Reserved",now,now.AddMinutes(5)));}_store.ReplaceReservations(conversationId,rows);
    }
    private static ConsumerPlanningState NewState(string instruction)=>new(instruction,new(StringComparer.OrdinalIgnoreCase),[],[],[],[],[],"INVESTIGATING",null);
    private static ConversationPolicy ApplyPolicyInstruction(ConversationPolicy current,string instruction,DateTimeOffset now)
    {
        var ask=current.AskBeforeSubstitutions;var show=current.ShowBasketBeforePayment;
        if(ContainsAny(instruction,"ask me before making substitutions","ask before substitutions"))ask=true;
        if(ContainsAny(instruction,"you may make substitutions","substitutions are okay","approve substitutions"))ask=false;
        if(ContainsAny(instruction,"show me the basket before paying","show basket before payment"))show=true;
        if(ContainsAny(instruction,"use your best judgement and proceed","do not ask unless necessary","auto when safe"))show=false;
        return current with{InteractionMode="AUTO_WHEN_SAFE",AskBeforeSubstitutions=ask,ShowBasketBeforePayment=show,UpdatedAt=now,Version=current.Version+1};
    }
    private static bool ContainsAny(string value,params string[] phrases)=>phrases.Any(x=>value.Contains(x,StringComparison.OrdinalIgnoreCase));
    private static void LearnConstraints(Dictionary<string,string> values,string message)
    {
        var servings=System.Text.RegularExpressions.Regex.Match(message,@"(?:serves?|for)\s+(\d+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);if(servings.Success)values["servings"]=servings.Groups[1].Value;
        var allergy=System.Text.RegularExpressions.Regex.Match(message,@"allergic to\s+([a-z ,]+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);if(allergy.Success)values["allergies"]=allergy.Groups[1].Value.Trim();
        if(message.Contains("vegetarian",StringComparison.OrdinalIgnoreCase))values["diet"]="vegetarian";if(message.Contains("vegan",StringComparison.OrdinalIgnoreCase))values["diet"]="vegan";
        if(message.Contains("high protein",StringComparison.OrdinalIgnoreCase))values["nutrition"]="high-protein";var calories=System.Text.RegularExpressions.Regex.Match(message,@"(?:under|max(?:imum)?)\s+(\d+)\s+calories",System.Text.RegularExpressions.RegexOptions.IgnoreCase);if(calories.Success)values["maximumCalories"]=calories.Groups[1].Value;
        var owns=System.Text.RegularExpressions.Regex.Match(message,@"(?:already have|I have)\s+([a-z ,]+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);if(owns.Success)values["inventoryAtHome"]=owns.Groups[1].Value.Trim();
        if(message.Contains("substitution",StringComparison.OrdinalIgnoreCase)||message.Contains("substitute",StringComparison.OrdinalIgnoreCase))values["acceptedSubstitutionFeedback"]=message;
    }
    private static string? FindConstraintViolation(ConsumerPurchasePlan plan,IReadOnlyDictionary<string,string> constraints,IReadOnlyList<Product> catalogue)
    {
        var selected=plan.Items.Select(item=>(Item:item,Product:catalogue.Where(x=>x.ProductId.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.Description.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase))).OrderBy(x=>x.UnitPrice).First())).ToList();
        if(constraints.TryGetValue("allergies",out var allergies)&&selected.Any(x=>(x.Product.Allergens??new HashSet<string>()).Any(a=>allergies.Contains(a,StringComparison.OrdinalIgnoreCase))))return "The proposed basket conflicts with a stored allergy. No purchase will be attempted.";
        if(constraints.TryGetValue("diet",out var diet)&&selected.Any(x=>!(x.Product.DietaryTags??new HashSet<string>()).Any(tag=>string.Equals(tag,diet,StringComparison.OrdinalIgnoreCase))))return "The proposed basket conflicts with the stored dietary preference. No purchase will be attempted.";
        if(constraints.TryGetValue("maximumCalories",out var maximum)&&decimal.TryParse(maximum,out var calories)&&selected.Sum(x=>(x.Product.CaloriesPerUnit??0)*x.Item.Quantity)>calories)return "The proposed basket exceeds the stored calorie constraint. No purchase will be attempted.";
        return null;
    }

    private static ConsumerPurchasePlan Fallback(string instruction,IReadOnlyList<Product> catalogue)
    {
        var match=System.Text.RegularExpressions.Regex.Match(instruction,@"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var budget=match.Success?decimal.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture):0;
        if(!instruction.Contains("chicken wrap",StringComparison.OrdinalIgnoreCase)||budget<=0)
            return new(PurchasePlanningStatus.NeedsInput,"Planning requires clarification","The live planning model is unavailable and no safe verified plan was produced.",Math.Max(budget,0.01m),"GBP",[],["Please restate the meal and maximum budget."],null,[]);
        var terms=new[]{"chicken","wraps","lettuce","tomato","sauce"};var inventory=System.Text.RegularExpressions.Regex.Match(instruction,@"(?:already have|I have)\s+([a-z ,and]+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;var needed=terms.Where(x=>!inventory.Contains(x,StringComparison.OrdinalIgnoreCase)).ToArray();var items=needed.Select(x=>new PlannedPurchaseItem(x,1)).ToArray();var tools=new PurchasePlanningPlugin(catalogue,MaximumToolCalls);foreach(var term in needed)tools.Search(term);var priced=tools.Price(JsonSerializer.Serialize(items));var total=JsonDocument.Parse(priced).RootElement.GetProperty("total").GetDecimal();
        return total<=budget
            ?new(PurchasePlanningStatus.Ready,"Chicken wraps",$"A complete basket is available for £{total:0.00}.",budget,"GBP",items,[],total,["search_catalogue","price_basket"])
            :new(PurchasePlanningStatus.NeedsInput,"Chicken wraps exceed budget",$"The cheapest complete basket is £{total:0.00}, above the £{budget:0.00} budget. No payment will be attempted.",budget,"GBP",items,
                ["Do you already have any of the sauce, lettuce or tomatoes?","Would you accept a cheaper vegetarian filling or increase the budget?"],total,["search_catalogue","price_basket"]);
    }
    private static ConsumerPurchasePlan Unavailable(string instruction)
    {return new(PurchasePlanningStatus.NeedsInput,"Planning temporarily unavailable","The reasoning model did not complete safely. No deterministic substitute will submit a purchase.",ExtractBudget(instruction),"GBP",[],["Please retry when the planning service is available."],null,[]);}
    private static decimal ExtractBudget(string instruction){var match=System.Text.RegularExpressions.Regex.Match(instruction,@"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);return match.Success?decimal.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture):0.01m;}
}

public sealed class PurchasePlanningPlugin
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    public sealed record ToolCall(string Name,string Input,string Output);private static readonly AsyncLocal<List<ToolCall>?> Calls=new();public static IReadOnlyList<ToolCall> LastCalls=>Calls.Value??[];
    public static void ClearCalls()=>Calls.Value=[];
    private readonly IReadOnlyList<Product> _catalogue;private readonly IReadOnlyDictionary<string,string> _constraints;private readonly List<string> _used=[];private readonly int _maximumCalls;private int _calls;public IReadOnlyList<string> ToolsUsed=>_used;
    public PurchasePlanningPlugin(IReadOnlyList<Product> catalogue,int maximumCalls=12,IReadOnlyDictionary<string,string>? constraints=null){_catalogue=catalogue;_maximumCalls=maximumCalls;_constraints=constraints??new Dictionary<string,string>();Calls.Value=[];}
    [KernelFunction("get_user_constraints"),Description("Read durable inventory-at-home, serving count, allergy, nutrition, diet and accepted-substitution constraints.")]
    public string Constraints()
    {
        var output=JsonSerializer.Serialize(_constraints);
        Track("get_user_constraints","{}",output);
        return output;
    }
    [KernelFunction("search_catalogue"),Description("Search available grocery products and prices. Call repeatedly for ingredients and alternatives.")]
    public string Search([Description("Ingredient or product search phrase")]string query)

    {
        var output=JsonSerializer.Serialize(_catalogue.Where(x=>x.Description.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(query,StringComparison.OrdinalIgnoreCase))).OrderBy(x=>x.UnitPrice).Select(x=>new{x.ProductId,x.Description,x.UnitPrice,x.Currency,x.AvailableQuantity,x.CaloriesPerUnit,x.ProteinGramsPerUnit,x.Allergens,x.DietaryTags}));
        Track("search_catalogue",query,output);return output;
    }
    [KernelFunction("price_basket"),Description("Calculate a proposed basket total including cheapest delivery. Input is JSON array of searchTerm and quantity.")]
    public string Price([Description("JSON array such as [{\"searchTerm\":\"chicken\",\"quantity\":1}]")]string itemsJson)
    {
        var items=JsonSerializer.Deserialize<List<PlannedPurchaseItem>>(itemsJson,JsonOptions)??[];var selected=new List<object>();decimal subtotal=0;
        foreach(var item in items){var product=_catalogue.Where(x=>x.AvailableQuantity>=item.Quantity&&(x.Description.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.ProductId.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)))).OrderBy(x=>x.UnitPrice).FirstOrDefault();if(product is null){var missing=JsonSerializer.Serialize(new{valid=false,missing=item.SearchTerm});Track("price_basket",itemsJson,missing);return missing;}var line=product.UnitPrice*item.Quantity;subtotal+=line;selected.Add(new{product.ProductId,product.Description,item.Quantity,product.UnitPrice,lineTotal=line});}
        var output=JsonSerializer.Serialize(new{valid=true,selected,subtotal,deliveryFee=2.50m,total=subtotal+2.50m,currency="GBP"});Track("price_basket",itemsJson,output);return output;
    }
    private void Track(string name,string input,string output){if(++_calls>_maximumCalls)throw new InvalidOperationException("PLANNING_TOOL_LIMIT_REACHED");if(!_used.Contains(name))_used.Add(name);Calls.Value!.Add(new(name,input,output));}
}
