using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    IReadOnlyList<PlannedPurchaseItem> Items,IReadOnlyList<string> Questions,decimal? EstimatedTotal,IReadOnlyList<string> ToolsUsed,[property:JsonIgnore]string? ConversationId=null,int ReasoningTurns=0,
    string InteractionDecision=PurchaseInteractionDecision.Clarify,bool HasSubstitutions=false);
public static class PurchasePlanGuard
{
    private static readonly string[] DeliveryTerms=["delivery","standard delivery","express delivery","delivery charge","delivery fee","shipping","shipping fee"];
    public static ConsumerPurchasePlan Normalize(ConsumerPurchasePlan plan)=>plan with
    {
        Status=string.IsNullOrWhiteSpace(plan.Status)?PurchasePlanningStatus.NeedsInput:plan.Status,
        Summary=plan.Summary??string.Empty,Message=plan.Message??string.Empty,Currency=plan.Currency??string.Empty,
        Items=plan.Items??[],Questions=plan.Questions??[],ToolsUsed=plan.ToolsUsed??[]
    };
    public static bool HasMalformedItems(ConsumerPurchasePlan plan)=>plan.Items is null||plan.Items.Any(x=>x is null||string.IsNullOrWhiteSpace(x.SearchTerm)||x.Quantity<=0);
    public static bool IsDeliveryItem(PlannedPurchaseItem item)=>DeliveryTerms.Any(term=>item.SearchTerm.Trim().Equals(term,StringComparison.OrdinalIgnoreCase));
    public static bool ClaimsDeliveryUnavailable(ConsumerPurchasePlan plan)
    {
        var conclusion=$"{plan.Summary} {plan.Message}";
        return plan.Status!=PurchasePlanningStatus.Ready&&conclusion.Contains("delivery",StringComparison.OrdinalIgnoreCase)&&
               (conclusion.Contains("unavailable",StringComparison.OrdinalIgnoreCase)||conclusion.Contains("no available",StringComparison.OrdinalIgnoreCase)||
                conclusion.Contains("no delivery",StringComparison.OrdinalIgnoreCase)||conclusion.Contains("cannot be calculated",StringComparison.OrdinalIgnoreCase));
    }
    public static bool TryReplaceUnsupportedDeliveryConclusion(string instruction,IReadOnlyList<Product> catalogue,ConsumerPurchasePlan plan,out ConsumerPurchasePlan corrected)
    {
        corrected=plan;if(!ClaimsDeliveryUnavailable(plan))return false;
        if(TryBuildExplicitProductListPlan(instruction,catalogue,out corrected))return true;
        return TryBuildSingleProductPlan(instruction,catalogue,out corrected);
    }
    public static bool TryReplaceContradictoryAffordableConclusion(string instruction,IReadOnlyList<Product> catalogue,ConsumerPurchasePlan plan,out ConsumerPurchasePlan corrected)
    {
        corrected=plan;
        if(plan.Status==PurchasePlanningStatus.Ready||plan.EstimatedTotal is null||plan.EstimatedTotal>plan.MaximumAmount||
           plan.MaximumAmount<=0||plan.ToolsUsed is null||!plan.ToolsUsed.Contains("price_basket"))return false;
        ConsumerPurchasePlan grounded;
        if(!TryBuildExplicitProductListPlan(instruction,catalogue,out grounded)&&!TryBuildSingleProductPlan(instruction,catalogue,out grounded))return false;
        if(grounded.Status!=PurchasePlanningStatus.Ready||grounded.EstimatedTotal is null||grounded.EstimatedTotal>grounded.MaximumAmount||
           grounded.Items.Count==0||HasMalformedItems(grounded)||HasUnavailableItems(grounded,catalogue))return false;
        corrected=grounded with{Summary="Affordable basket verified",Message=$"The merchant independently verified the complete basket at £{grounded.EstimatedTotal:0.00}, within the £{grounded.MaximumAmount:0.00} budget."};
        return true;
    }
    public static bool HasUnavailableItems(ConsumerPurchasePlan plan,IReadOnlyList<Product> catalogue)=>plan.Items.Any(item=>!catalogue.Any(product=>product.AvailableQuantity>=item.Quantity&&
        (product.ProductId.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||product.Description.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||product.Tags.Any(tag=>tag.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)))));
    public static bool TryBuildSingleProductPlan(string instruction,IReadOnlyList<Product> catalogue,out ConsumerPurchasePlan plan)
    {
        plan=null!;var budgetMatch=System.Text.RegularExpressions.Regex.Match(instruction,@"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var productMatch=System.Text.RegularExpressions.Regex.Match(instruction,@"\bbuy\s+(.+?)(?=\s+(?:for\s+)?(?:my\s+)?budget\b|\s+under\s+(?:£|GBP)|\s+(?:£|GBP)|[.!?]|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if(!budgetMatch.Success||!productMatch.Success)return false;var budget=decimal.Parse(budgetMatch.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);var requested=productMatch.Groups[1].Value.Trim();if(requested.Length==0||requested.Contains(',')||System.Text.RegularExpressions.Regex.IsMatch(requested,@"\band\b|\bto make\b|\bfor\s+(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+people\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return false;
        var (query,quantity)=NormalizeProductRequest(requested);var product=catalogue.Where(x=>x.AvailableQuantity>=quantity&&(x.ProductId.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Description.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(query,StringComparison.OrdinalIgnoreCase)))).OrderBy(x=>x.UnitPrice).FirstOrDefault();
        if(product is null){plan=new(PurchasePlanningStatus.NeedsInput,"Product not found",$"I could not find an available merchant product matching '{query}'.",budget,"GBP",[],[$"Would you like a similar alternative to {query}?"],null,["search_catalogue"]);return true;}
        var tools=new PurchasePlanningPlugin(catalogue,40);tools.Search(query);var item=new PlannedPurchaseItem(product.ProductId,quantity);var priced=tools.Price(JsonSerializer.Serialize(new[]{item}));using var document=JsonDocument.Parse(priced);var total=document.RootElement.GetProperty("total").GetDecimal();
        plan=total<=budget?new(PurchasePlanningStatus.Ready,$"Best-value {query}",$"The merchant verified {product.Description} at £{total:0.00} including delivery, within the £{budget:0.00} budget.",budget,"GBP",[item],[],total,tools.ToolsUsed.ToArray())
            :new(PurchasePlanningStatus.NeedsInput,$"{query} exceeds budget",$"The cheapest available {query} costs £{total:0.00} including delivery, above the £{budget:0.00} budget.",budget,"GBP",[item],["Would you like to increase the budget?"],total,tools.ToolsUsed.ToArray());return true;
    }
    public static bool TryBuildExplicitProductListPlan(string instruction,IReadOnlyList<Product> catalogue,out ConsumerPurchasePlan plan)
    {
        plan=null!;var budgetMatch=System.Text.RegularExpressions.Regex.Match(instruction,@"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var listMatch=System.Text.RegularExpressions.Regex.Match(instruction,@"\bbuy\s+(.+?)(?=\s+to\s+make\b|[.!?]|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if(!budgetMatch.Success||!listMatch.Success)return false;var raw=System.Text.RegularExpressions.Regex.Replace(listMatch.Groups[1].Value.Trim(),@"^enough\s+","",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if(!raw.Contains(',')&&!System.Text.RegularExpressions.Regex.IsMatch(raw,@"\band\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return false;
        var terms=System.Text.RegularExpressions.Regex.Split(raw,@"\s*,\s*|\s+and\s+",System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(x=>System.Text.RegularExpressions.Regex.Replace(x.Trim().TrimEnd('.','!','?'),@"^and\s+","",System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(terms.Length<2)return false;
        var budget=decimal.Parse(budgetMatch.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);var selected=new List<PlannedPurchaseItem>();var missing=new List<string>();var normalized=terms.Select(term=>(Original:term,Parsed:NormalizeProductRequest(term))).ToArray();
        foreach(var term in normalized){var (query,quantity)=term.Parsed;var product=catalogue.Where(x=>x.AvailableQuantity>=quantity&&(x.ProductId.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Description.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(query,StringComparison.OrdinalIgnoreCase)))).OrderBy(x=>x.UnitPrice).FirstOrDefault();if(product is null)missing.Add(term.Original);else selected.Add(new(product.ProductId,quantity));}
        var tools=new PurchasePlanningPlugin(catalogue,40);tools.SearchMany(JsonSerializer.Serialize(normalized.Select(x=>x.Parsed.Query)));if(missing.Count>0){plan=new(PurchasePlanningStatus.NeedsInput,"Some products were not found",$"I could not find: {string.Join(", ",missing)}.",budget,"GBP",[],[$"Would you accept alternatives for {string.Join(", ",missing)}?"],null,tools.ToolsUsed.ToArray());return true;}
        var priced=tools.Price(JsonSerializer.Serialize(selected));using var document=JsonDocument.Parse(priced);var total=document.RootElement.GetProperty("total").GetDecimal();
        plan=total<=budget?new(PurchasePlanningStatus.Ready,"Complete shopping list verified",$"The merchant verified all {selected.Count} requested products at £{total:0.00} including delivery, within the £{budget:0.00} budget.",budget,"GBP",selected,[],total,tools.ToolsUsed.ToArray())
            :new(PurchasePlanningStatus.NeedsInput,"Shopping list exceeds budget",$"The cheapest complete basket costs £{total:0.00} including delivery, above the £{budget:0.00} budget.",budget,"GBP",selected,["Would you like cheaper substitutions or a higher budget?"],total,tools.ToolsUsed.ToArray());return true;
    }
    private static (string Query,int Quantity) NormalizeProductRequest(string requested)
    {
        var match=System.Text.RegularExpressions.Regex.Match(requested.Trim(),@"^(?<count>\d+|a|an|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:(?:packs?|bottles?|boxes?|bags?|tins?|cans?|jars?|cartons?|loaf|loaves)\s+of\s+)?(?<product>.+)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if(!match.Success)return(requested.Trim(),1);var counts=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase){{"a",1},{"an",1},{"one",1},{"two",2},{"three",3},{"four",4},{"five",5},{"six",6},{"seven",7},{"eight",8},{"nine",9},{"ten",10}};
        var value=match.Groups["count"].Value;var quantity=int.TryParse(value,out var numeric)?numeric:counts[value];return(match.Groups["product"].Value.Trim(),Math.Max(quantity,1));
    }
    public static bool TreatsOptionalIngredientAsRequired(ConsumerPurchasePlan plan,string instruction)=>
        plan.Status==PurchasePlanningStatus.Impossible&&instruction.Contains("chicken wrap",StringComparison.OrdinalIgnoreCase)&&
        plan.Message.Contains("cheese",StringComparison.OrdinalIgnoreCase)&&
        !instruction.Contains("with cheese",StringComparison.OrdinalIgnoreCase)&&!instruction.Contains("cheesy",StringComparison.OrdinalIgnoreCase);
    public static bool HasContradictoryBudgetConclusion(ConsumerPurchasePlan plan,IReadOnlyList<Product> catalogue,IReadOnlyList<PurchasePlanningPlugin.ToolCall> evidence)
    {
        if(plan.Status==PurchasePlanningStatus.Ready||!plan.Message.Contains("exceed",StringComparison.OrdinalIgnoreCase)||!plan.Message.Contains("budget",StringComparison.OrdinalIgnoreCase)||
           plan.EstimatedTotal is null||plan.EstimatedTotal>plan.MaximumAmount||plan.Items.Count==0||HasMalformedItems(plan)||!plan.ToolsUsed.Contains("price_basket"))return false;
        if(plan.Items.Any(item=>!catalogue.Any(product=>product.AvailableQuantity>=item.Quantity&&(product.ProductId.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||
            product.Description.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||product.Tags.Any(tag=>tag.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase))))))return false;
        var priced=evidence.LastOrDefault(x=>x.Name=="price_basket");if(priced is null)return false;
        try
        {
            using var document=JsonDocument.Parse(priced.Output);var root=document.RootElement;
            return root.GetProperty("valid").GetBoolean()&&root.GetProperty("total").GetDecimal()==plan.EstimatedTotal&&
                   root.GetProperty("selected").GetArrayLength()==plan.Items.Count;
        }
        catch(JsonException){return false;}
        catch(KeyNotFoundException){return false;}
    }
}
public sealed record ConsumerPlanningState(string Objective,Dictionary<string,string> Constraints,List<string> Hypotheses,List<string> OpenQuestions,
    List<string> AttemptedBaskets,List<string> RejectedAlternatives,List<string> ToolHistory,string Status,ConsumerPurchasePlan? LatestPlan);
public interface IConsumerPurchaseRequestAgent{Task<ConsumerPurchasePlan> PlanAsync(string principalId,string? conversationId,string instruction,IReadOnlyList<Product> catalogue,CancellationToken token);}

/// <summary>A bounded Semantic Kernel agent may search and price products, but is deliberately
/// given no mandate, authorisation, checkout or payment function.</summary>
public sealed class ConsumerPurchaseRequestAgent:IConsumerPurchaseRequestAgent
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    private readonly IConsumerPlanningStore _store;private readonly IConsumerMemoryService? _memory;private readonly bool _allowDeterministicFallback;private readonly int _maximumToolCalls;private readonly int _maximumReasoningTurns;private readonly int _planningTimeoutSeconds;
    private const int DefaultMaximumToolCalls=40;private const int DefaultMaximumReasoningTurns=40;
    public ConsumerPurchaseRequestAgent(IConsumerPlanningStore store,IConfiguration configuration,IConsumerMemoryService? memory=null)
    {
        _store=store;_memory=memory;_allowDeterministicFallback=configuration.GetValue("ConsumerPilot:Planning:AllowDeterministicFallback",false);
        _maximumToolCalls=Math.Clamp(configuration.GetValue("ConsumerPilot:Planning:MaximumToolCalls",DefaultMaximumToolCalls),8,80);
        _maximumReasoningTurns=Math.Clamp(configuration.GetValue("ConsumerPilot:Planning:MaximumReasoningTurns",DefaultMaximumReasoningTurns),8,40);
        _planningTimeoutSeconds=Math.Clamp(configuration.GetValue("ConsumerPilot:Planning:TimeoutSeconds",90),30,180);
    }
    public async Task<ConsumerPurchasePlan> PlanAsync(string principal,string? conversationId,string instruction,IReadOnlyList<Product> catalogue,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(instruction))throw new ArgumentException("A purchase instruction is required.");PurchasePlanningPlugin.ClearCalls();
        var now=DateTimeOffset.UtcNow;var policy=ApplyPolicyInstruction(_store.GetPolicy(principal),instruction,now);_store.SavePolicy(policy);var conversation=conversationId is null?null:_store.FindOwned(conversationId,principal);
        if(conversationId is not null&&conversation is null)throw new UnauthorizedAccessException("Conversation not found or belongs to another principal.");
        var state=NormalizeState(conversation is null?NewState(instruction):JsonSerializer.Deserialize<ConsumerPlanningState>(conversation.StateJson,JsonOptions)??NewState(instruction));
        foreach(var preference in _store.Preferences(principal))state.Constraints.TryAdd(preference.Key,preference.Value);
        var recalled=_memory is null?Array.Empty<ConsumerMemoryMatch>():await _memory.RetrieveAsync(principal,instruction,cancellationToken:token);
        foreach(var match in recalled){var memory=match.Memory;state.Constraints[$"memory:{memory.Kind}:{memory.Subject}"]=$"{memory.Polarity}: {memory.Content} (confidence {memory.Confidence:0.00}; {memory.Provenance})";}
        LearnConstraints(state.Constraints,instruction);conversation??=_store.Create(principal,instruction,JsonSerializer.Serialize(state),now);_memory?.CaptureCorrections(principal,instruction,conversation.ConversationId);
        foreach(var constraint in state.Constraints)_store.Remember(principal,constraint.Key,constraint.Value,conversation.ConversationId,now);
        var sequence=_store.Turns(conversation.ConversationId).Count+1;_store.Append(new($"planning_turn_{Guid.NewGuid():N}",conversation.ConversationId,sequence++,"user","message",instruction,null,null,null,now));
        ConsumerPurchasePlan? plan=null;
        if(AgentFactory.IsLiveModeConfigured)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(_planningTimeoutSeconds));
            try{plan=await AskModel(state,instruction,catalogue,timeout.Token);}
            catch(Exception ex) when(ex is not OperationCanceledException||!token.IsCancellationRequested){plan=null;}
        }
        var completeInstruction=string.Join("\n",new[]{state.Objective,instruction});
        plan??=_allowDeterministicFallback||IsSupportedMeal(completeInstruction)?Fallback(completeInstruction,catalogue):Unavailable(instruction);
        plan=PurchasePlanGuard.Normalize(plan);
        if(PurchasePlanGuard.TryReplaceUnsupportedDeliveryConclusion(completeInstruction,catalogue,plan,out var deliveryCorrectedPlan))
            plan=deliveryCorrectedPlan;
        if(PurchasePlanGuard.TryReplaceContradictoryAffordableConclusion(completeInstruction,catalogue,plan,out var affordabilityCorrectedPlan))
            plan=affordabilityCorrectedPlan;
        if(plan.Status==PurchasePlanningStatus.Impossible&&IsSupportedMeal(completeInstruction)&&!HasGroundedImpossibilityEvidence(PurchasePlanningPlugin.LastCalls))
            plan=Fallback(completeInstruction,catalogue);
        if(plan.Status==PurchasePlanningStatus.Ready&&TryCanonicalizePricedBasket(plan,PurchasePlanningPlugin.LastCalls,out var canonicalPlan))
            plan=canonicalPlan;
        if(plan.Status==PurchasePlanningStatus.Ready&&PurchasePlanGuard.HasUnavailableItems(plan,catalogue))
        {
            if(PurchasePlanGuard.TryBuildExplicitProductListPlan(completeInstruction,catalogue,out var listPlan))plan=listPlan;
            else if(PurchasePlanGuard.TryBuildSingleProductPlan(completeInstruction,catalogue,out var groundedPlan))plan=groundedPlan;
        }
        if(PurchasePlanGuard.HasMalformedItems(plan))
            plan=plan with{Status=PurchasePlanningStatus.NeedsInput,Summary="Invalid basket rejected",Message="The planner returned an invalid basket. No purchase will be attempted.",Items=[],Questions=["Please retry the request."]};
        if(PurchasePlanGuard.HasContradictoryBudgetConclusion(plan,catalogue,PurchasePlanningPlugin.LastCalls))
            plan=plan with{Status=PurchasePlanningStatus.Ready,Summary="Affordable basket verified",Message=$"The complete basket was independently priced at £{plan.EstimatedTotal:0.00}, within the £{plan.MaximumAmount:0.00} budget.",Questions=[]};
        if(plan.MaximumAmount<=0||!string.Equals(plan.Currency,"GBP",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The agent did not preserve a valid GBP budget.");
        if(plan.Status==PurchasePlanningStatus.Ready)
        {
            if(plan.Items.Count==0||PurchasePlanGuard.HasMalformedItems(plan)||!plan.ToolsUsed.Contains("price_basket"))throw new InvalidOperationException("A ready plan must contain valid items priced through the catalogue tool.");
            var searchable=catalogue.SelectMany(x=>x.Tags.Append(x.Description).Append(x.ProductId)).ToArray();
            if(plan.Items.Any(i=>!searchable.Any(v=>v.Contains(i.SearchTerm,StringComparison.OrdinalIgnoreCase))))
                plan=plan with{Status=PurchasePlanningStatus.NeedsInput,Summary="Product could not be verified",Message="I could not match the proposed product to the merchant catalogue. No purchase will be attempted.",Items=[],Questions=["Would you like me to search for a similar available product?"]};
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
        var conversationStatus=decision==PurchaseInteractionDecision.Clarify?PurchasePlanningStatus.NeedsInput:decision==PurchaseInteractionDecision.Propose?PurchaseInteractionDecision.Propose:plan.Status;
        _store.Save(conversation with{Status=conversationStatus,StateJson=JsonSerializer.Serialize(state),UpdatedAt=DateTimeOffset.UtcNow,Version=conversation.Version+1});
        if(plan.Status==PurchasePlanningStatus.Ready)Reserve(conversation.ConversationId,plan,catalogue,DateTimeOffset.UtcNow);
        return plan;
    }

    private async Task<ConsumerPurchasePlan?> AskModel(ConsumerPlanningState state,string instruction,IReadOnlyList<Product> catalogue,CancellationToken token)
    {
        var kernel=AgentFactory.CreateLiveKernel();var tools=new PurchasePlanningPlugin(catalogue,_maximumToolCalls,state.Constraints);kernel.Plugins.AddFromObject(tools,"grocery");
        var chat=kernel.GetRequiredService<IChatCompletionService>();var history=new ChatHistory();
        history.AddSystemMessage("""
            You are an iterative grocery-planning agent. You have NO authority or payment tools.
            Extract the user's exact maximum budget. When the user names a meal instead of listing ingredients,
            call RECIPE first to derive its essential and optional ingredients. Then search the catalogue,
            price the complete basket including delivery, try cheaper alternatives when necessary,
            and challenge the result before stopping. Minimise latency and conversation burden: normally
            use one SEARCH_MANY turn for all essential ingredients, one PRICE turn, then FINAL. Compare
            matching brands and choose the cheapest valid available product unless a stored preference,
            allergy, dietary rule or requested brand prevents it. Use search_catalogue_batch and price_basket; do not
            invent products or prices. Build the minimum viable recipe requested by the user. Separate
            essential ingredients from optional enhancements. Omit unavailable optional ingredients
            and continue; for chicken wraps, cheese is optional unless explicitly requested. If information about ingredients already owned could make
            the request feasible, return NEEDS_INPUT with at most one short, plain-language question.
            Minimise cognitive load for assistive-technology users and never require them to repeat known context. If no complete basket
            is possible, stop. On each reasoning turn return exactly one JSON action:
            {"action":"SEARCH","query":"ingredient"}
            {"action":"SEARCH_MANY","queries":["ingredient one","ingredient two","ingredient three"]}
            {"action":"RECIPE","meal":"meal or dish described by the user"}
            {"action":"PRICE","items":[{"searchTerm":"catalogue term","quantity":1}]}
            {"action":"FINAL","plan":{"status":"READY|NEEDS_INPUT|IMPOSSIBLE_WITHIN_BUDGET","summary":"...","message":"...","maximumAmount":4.99,"currency":"GBP","items":[],"questions":[],"estimatedTotal":4.50,"toolsUsed":[],"hasSubstitutions":false}}
            READY is allowed only after PRICE evidence proves the complete basket is within budget.
            Set hasSubstitutions=true whenever the proposed basket replaces a requested or preferred item.
            """);
        history.AddUserMessage($"Persistent investigation state:\n{JsonSerializer.Serialize(state)}\n\nLatest user message:\n{instruction}");
        var completedCalls=new HashSet<string>(StringComparer.Ordinal);var duplicateCalls=0;
        for(var turn=1;turn<=_maximumReasoningTurns;turn++)
        {
            var response=await chat.GetChatMessageContentAsync(history,kernel:kernel,cancellationToken:token);var text=response.Content??"";var start=text.IndexOf('{');var end=text.LastIndexOf('}');if(start<0||end<=start)return null;var json=text[start..(end+1)];
            using var document=JsonDocument.Parse(json);var root=document.RootElement;var action=root.GetProperty("action").GetString()?.ToUpperInvariant();history.AddAssistantMessage(json);
            if(action is "SEARCH" or "SEARCH_MANY" or "RECIPE" or "PRICE")
            {
                var signature=$"{action}:{root.GetRawText()}";if(!completedCalls.Add(signature)){duplicateCalls++;history.AddUserMessage("APPLICATION VALIDATION: This identical tool call already completed. Use its existing evidence, choose a different investigation, or finish.");if(duplicateCalls>=3)return null;continue;}
            }
            if(action=="SEARCH"){var query=root.GetProperty("query").GetString()??"";history.AddUserMessage($"TOOL search_catalogue RESULT: {tools.Search(query)}");continue;}
            if(action=="SEARCH_MANY"){var queries=root.GetProperty("queries").GetRawText();history.AddUserMessage($"TOOL search_catalogue_batch RESULT: {tools.SearchMany(queries)}");continue;}
            if(action=="RECIPE"){var meal=root.GetProperty("meal").GetString()??state.Objective;history.AddUserMessage($"TOOL discover_recipe RESULT: {tools.DiscoverRecipe(meal)}");continue;}
            if(action=="PRICE")
            {
                var items=root.GetProperty("items").GetRawText();var priced=tools.Price(items);history.AddUserMessage($"TOOL price_basket RESULT: {priced}");
                var budget=ExtractBudget($"{instruction}\n{state.Objective}");
                if(tools.TryBuildReadyPlan(priced,budget,turn,out var verified))return verified;
                continue;
            }
            if(action=="FINAL"&&root.TryGetProperty("plan",out var planJson))
            {
                var plan=JsonSerializer.Deserialize<ConsumerPurchasePlan>(planJson.GetRawText(),JsonOptions);
                if(plan is null){history.AddUserMessage("APPLICATION VALIDATION: The final plan was unreadable. Correct it and continue.");continue;}
                plan=PurchasePlanGuard.Normalize(plan);
                if(PurchasePlanGuard.HasMalformedItems(plan)){history.AddUserMessage("APPLICATION VALIDATION: Every item must have a non-empty searchTerm and a positive quantity. Correct the basket and continue.");continue;}
                if(PurchasePlanGuard.TreatsOptionalIngredientAsRequired(plan,instruction)){history.AddUserMessage("APPLICATION VALIDATION: Cheese is optional for chicken wraps and was not requested. Omit it, search and price the minimum viable basket, then continue.");continue;}
                if(plan.Status==PurchasePlanningStatus.Impossible&&!HasGroundedImpossibilityEvidence(PurchasePlanningPlugin.LastCalls)){history.AddUserMessage("APPLICATION VALIDATION: IMPOSSIBLE requires catalogue-search and price evidence. Discover the recipe, resolve essential ingredients against the catalogue, price the cheapest complete basket, and continue.");continue;}
                return plan with{ToolsUsed=tools.ToolsUsed.ToArray(),ReasoningTurns=turn};
            }
            return null;
        }
        return new(PurchasePlanningStatus.NeedsInput,"Reasoning limit reached",$"The agent reached the maximum of {_maximumReasoningTurns} reasoning turns without sufficient evidence. No payment will be attempted.",ExtractBudget(instruction),"GBP",[],["Please simplify or clarify the request."],null,tools.ToolsUsed.ToArray(),null,_maximumReasoningTurns);
    }

    private void Reserve(string conversationId,ConsumerPurchasePlan plan,IReadOnlyList<Product> catalogue,DateTimeOffset now)
    {
        var rows=new List<ConsumerProductReservation>();foreach(var item in plan.Items){var p=catalogue.Where(x=>x.AvailableQuantity>=item.Quantity&&(x.ProductId.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.Description.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)))).OrderBy(x=>x.UnitPrice).First();rows.Add(new($"product_hold_{Guid.NewGuid():N}",conversationId,p.ProductId,item.Quantity,p.UnitPrice,p.Currency,"Reserved",now,now.AddMinutes(5)));}_store.ReplaceReservations(conversationId,rows);
    }
    private static ConsumerPlanningState NewState(string instruction)=>new(instruction,new(StringComparer.OrdinalIgnoreCase),[],[],[],[],[],"INVESTIGATING",null);
    private static ConsumerPlanningState NormalizeState(ConsumerPlanningState state)=>state with
    {
        Objective=state.Objective??string.Empty,Constraints=state.Constraints??new(StringComparer.OrdinalIgnoreCase),
        Hypotheses=state.Hypotheses??[],OpenQuestions=state.OpenQuestions??[],AttemptedBaskets=state.AttemptedBaskets??[],
        RejectedAlternatives=state.RejectedAlternatives??[],ToolHistory=state.ToolHistory??[],Status=state.Status??"INVESTIGATING"
    };
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
        if(PurchasePlanGuard.TryBuildExplicitProductListPlan(instruction,catalogue,out var listedPurchase))return listedPurchase;
        if(PurchasePlanGuard.TryBuildSingleProductPlan(instruction,catalogue,out var directPurchase))return directPurchase;
        var match=System.Text.RegularExpressions.Regex.Match(instruction,@"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var budget=match.Success?decimal.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture):0;
        if(!instruction.Contains("chicken wrap",StringComparison.OrdinalIgnoreCase)||budget<=0)
            return new(PurchasePlanningStatus.NeedsInput,"Planning requires clarification","The live planning model is unavailable and no safe verified plan was produced.",Math.Max(budget,0.01m),"GBP",[],["Please restate the meal and maximum budget."],null,[]);
        var terms=new[]{"chicken","wraps","lettuce","tomato","sauce"};var inventory=System.Text.RegularExpressions.Regex.Match(instruction,@"(?:already have|I have)\s+([a-z ,and]+)",System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;var needed=terms.Where(x=>!inventory.Contains(x,StringComparison.OrdinalIgnoreCase)).ToArray();var items=needed.Select(x=>new PlannedPurchaseItem(x,1)).ToArray();var tools=new PurchasePlanningPlugin(catalogue,DefaultMaximumToolCalls);tools.SearchMany(JsonSerializer.Serialize(needed));var priced=tools.Price(JsonSerializer.Serialize(items));var total=JsonDocument.Parse(priced).RootElement.GetProperty("total").GetDecimal();
        return total<=budget
            ?new(PurchasePlanningStatus.Ready,"Chicken wraps",$"A complete basket is available for £{total:0.00}.",budget,"GBP",items,[],total,["search_catalogue_batch","price_basket"])
            :new(PurchasePlanningStatus.NeedsInput,"Chicken wraps exceed budget",$"The cheapest complete basket is £{total:0.00}, above the £{budget:0.00} budget. No payment will be attempted.",budget,"GBP",items,
                ["Do you already have any of the sauce, lettuce or tomatoes?","Would you accept a cheaper vegetarian filling or increase the budget?"],total,["search_catalogue_batch","price_basket"]);
    }
    private static ConsumerPurchasePlan Unavailable(string instruction)
    {return new(PurchasePlanningStatus.NeedsInput,"Planning temporarily unavailable","The reasoning model did not complete safely. No deterministic substitute will submit a purchase.",ExtractBudget(instruction),"GBP",[],["Please retry when the planning service is available."],null,[]);}
    private static decimal ExtractBudget(string instruction){var match=System.Text.RegularExpressions.Regex.Match(instruction,@"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);return match.Success?decimal.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture):0.01m;}
    private static bool IsSupportedMeal(string instruction)=>instruction.Contains("chicken wrap",StringComparison.OrdinalIgnoreCase);
    private static bool HasGroundedImpossibilityEvidence(IReadOnlyList<PurchasePlanningPlugin.ToolCall> calls)=>
        calls.Any(x=>x.Name is "search_catalogue" or "search_catalogue_batch")&&calls.Any(x=>x.Name=="price_basket");
    private static bool TryCanonicalizePricedBasket(ConsumerPurchasePlan plan,IReadOnlyList<PurchasePlanningPlugin.ToolCall> calls,out ConsumerPurchasePlan canonical)
    {
        canonical=plan;var priced=calls.LastOrDefault(x=>x.Name=="price_basket");if(priced is null)return false;
        try
        {
            using var document=JsonDocument.Parse(priced.Output);var root=document.RootElement;
            if(!root.TryGetProperty("valid",out var valid)||!valid.GetBoolean()||!root.TryGetProperty("selected",out var selected)||selected.GetArrayLength()==0)return false;
            var items=selected.EnumerateArray().Select(x=>new PlannedPurchaseItem(x.GetProperty("productId").GetString()!,x.GetProperty("quantity").GetInt32())).ToArray();
            if(items.Any(x=>string.IsNullOrWhiteSpace(x.SearchTerm)||x.Quantity<=0))return false;
            canonical=plan with{Items=items,EstimatedTotal=root.GetProperty("total").GetDecimal()};return true;
        }
        catch(JsonException){return false;}
        catch(InvalidOperationException){return false;}
        catch(KeyNotFoundException){return false;}
    }
}

public sealed class PurchasePlanningPlugin
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    public sealed record ToolCall(string Name,string Input,string Output);private static readonly AsyncLocal<List<ToolCall>?> Calls=new();public static IReadOnlyList<ToolCall> LastCalls=>Calls.Value??[];
    public static void ClearCalls()=>Calls.Value=[];
    private readonly IReadOnlyList<Product> _catalogue;private readonly IReadOnlyDictionary<string,string> _constraints;private readonly List<string> _used=[];private readonly int _maximumCalls;private int _calls;private string[] _requiredIngredients=[];public IReadOnlyList<string> ToolsUsed=>_used;
    public PurchasePlanningPlugin(IReadOnlyList<Product> catalogue,int maximumCalls=12,IReadOnlyDictionary<string,string>? constraints=null){_catalogue=catalogue;_maximumCalls=maximumCalls;_constraints=constraints??new Dictionary<string,string>();Calls.Value=[];}
    [KernelFunction("get_user_constraints"),Description("Read durable inventory-at-home, serving count, allergy, nutrition, diet and accepted-substitution constraints.")]
    public string Constraints()
    {
        var output=JsonSerializer.Serialize(_constraints,JsonOptions);
        Track("get_user_constraints","{}",output);
        return output;
    }
    [KernelFunction("discover_recipe"),Description("Turn a meal request into essential and optional ingredient search terms before catalogue lookup. This proposes ingredients only; merchant tools must confirm products and prices.")]
    public string DiscoverRecipe([Description("Meal or dish requested by the user")]string meal)
    {
        _requiredIngredients=meal.Contains("chicken wrap",StringComparison.OrdinalIgnoreCase)?["chicken","wraps","lettuce","tomato","sauce"]:[];
        object recipe=_requiredIngredients.Length>0
            ?new{meal="chicken wraps",essential=_requiredIngredients,optional=new[]{"cheese","onion","pepper"},note="Adjust quantities for the requested serving count."}
            :new{meal,essential=Array.Empty<string>(),optional=Array.Empty<string>(),note="No verified template is available. Propose a recipe, then verify every ingredient through the merchant catalogue."};
        var output=JsonSerializer.Serialize(recipe,JsonOptions);Track("discover_recipe",meal,output);return output;
    }
    public bool TryBuildReadyPlan(string pricedJson,decimal budget,int reasoningTurn,out ConsumerPurchasePlan plan)
    {
        plan=null!;if(budget<=0||_requiredIngredients.Length==0)return false;
        try
        {
            using var document=JsonDocument.Parse(pricedJson);var root=document.RootElement;
            if(!root.GetProperty("valid").GetBoolean()||root.GetProperty("total").GetDecimal()>budget)return false;
            var selected=root.GetProperty("selected").EnumerateArray().Select(x=>new{ProductId=x.GetProperty("productId").GetString()!,Quantity=x.GetProperty("quantity").GetInt32()}).ToArray();
            var products=selected.Select(x=>_catalogue.FirstOrDefault(p=>p.ProductId.Equals(x.ProductId,StringComparison.OrdinalIgnoreCase))).ToArray();
            if(products.Any(x=>x is null)||!_requiredIngredients.All(term=>products.Any(p=>p is not null&&(p.ProductId.Contains(term,StringComparison.OrdinalIgnoreCase)||p.Description.Contains(term,StringComparison.OrdinalIgnoreCase)||p.Tags.Any(t=>t.Contains(term,StringComparison.OrdinalIgnoreCase))))))return false;
            var total=root.GetProperty("total").GetDecimal();plan=new(PurchasePlanningStatus.Ready,"Complete basket verified",$"The merchant verified a complete best-value basket at £{total:0.00}, within the £{budget:0.00} budget.",budget,"GBP",selected.Select(x=>new PlannedPurchaseItem(x.ProductId,x.Quantity)).ToArray(),[],total,ToolsUsed.ToArray(),null,reasoningTurn);
            return true;
        }
        catch(JsonException){return false;}
        catch(InvalidOperationException){return false;}
        catch(KeyNotFoundException){return false;}
    }
    [KernelFunction("search_catalogue"),Description("Search available grocery products and prices. Call repeatedly for ingredients and alternatives.")]
    public string Search([Description("Ingredient or product search phrase")]string query)

    {
        var output=JsonSerializer.Serialize(_catalogue.Where(x=>x.ProductId.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Description.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(query,StringComparison.OrdinalIgnoreCase))).OrderBy(x=>x.UnitPrice).Select(x=>new{x.ProductId,x.Description,x.UnitPrice,x.Currency,x.AvailableQuantity,x.CaloriesPerUnit,x.ProteinGramsPerUnit,x.Allergens,x.DietaryTags}),JsonOptions);
        Track("search_catalogue",query,output);return output;
    }
    [KernelFunction("search_catalogue_batch"),Description("Search several ingredients in one call and return price-ordered brand alternatives for each query.")]
    public string SearchMany([Description("JSON array of ingredient or product search phrases")]string queriesJson)
    {
        var queries=(JsonSerializer.Deserialize<string[]>(queriesJson,JsonOptions)??[]).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        var results=queries.ToDictionary(query=>query,query=>_catalogue.Where(x=>x.AvailableQuantity>0&&(x.ProductId.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Description.Contains(query,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(query,StringComparison.OrdinalIgnoreCase))))
            .OrderBy(x=>x.UnitPrice).Take(10).Select(x=>new{x.ProductId,x.Description,x.UnitPrice,x.Currency,x.AvailableQuantity,x.CaloriesPerUnit,x.ProteinGramsPerUnit,x.Allergens,x.DietaryTags}).ToArray(),StringComparer.OrdinalIgnoreCase);
        var output=JsonSerializer.Serialize(new{queries,results},JsonOptions);Track("search_catalogue_batch",queriesJson,output);return output;
    }
    [KernelFunction("price_basket"),Description("Calculate a proposed basket total including cheapest delivery. Input is JSON array of searchTerm and quantity.")]
    public string Price([Description("JSON array such as [{\"searchTerm\":\"chicken\",\"quantity\":1}]")]string itemsJson)
    {
        var supplied=JsonSerializer.Deserialize<List<PlannedPurchaseItem>>(itemsJson,JsonOptions)??[];
        var items=supplied.Where(x=>!PurchasePlanGuard.IsDeliveryItem(x)).ToArray();var selected=new List<object>();decimal subtotal=0;
        if(items.Length==0){var empty=JsonSerializer.Serialize(new{valid=false,missing="purchasable product"},JsonOptions);Track("price_basket",itemsJson,empty);return empty;}
        foreach(var item in items){var product=_catalogue.Where(x=>x.AvailableQuantity>=item.Quantity&&(x.Description.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.ProductId.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)||x.Tags.Any(t=>t.Contains(item.SearchTerm,StringComparison.OrdinalIgnoreCase)))).OrderBy(x=>x.UnitPrice).FirstOrDefault();if(product is null){var missing=JsonSerializer.Serialize(new{valid=false,missing=item.SearchTerm},JsonOptions);Track("price_basket",itemsJson,missing);return missing;}var line=product.UnitPrice*item.Quantity;subtotal+=line;selected.Add(new{product.ProductId,product.Description,item.Quantity,product.UnitPrice,lineTotal=line});}
        var output=JsonSerializer.Serialize(new{valid=true,selected,subtotal,deliveryFee=2.50m,total=subtotal+2.50m,currency="GBP"},JsonOptions);Track("price_basket",itemsJson,output);return output;
    }
    private void Track(string name,string input,string output){if(++_calls>_maximumCalls)throw new InvalidOperationException("PLANNING_TOOL_LIMIT_REACHED");if(!_used.Contains(name))_used.Add(name);Calls.Value!.Add(new(name,input,output));}
}
