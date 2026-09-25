using System.Text.Json;
using AgentTrust.Agents;
using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Consumer;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AgentTrust.Api;

public sealed record CommerceAgentItem(string Concept,int Quantity=1,bool AllowSubstitution=true);
public sealed record CommerceAgentDecision(IReadOnlyList<string> SearchQueries,IReadOnlyList<CommerceAgentItem> Items,string Summary,string Message);
public sealed record CommerceAgentContext(string Objective,decimal MaximumAmount,string Currency,
    MerchantPlanningContext Provider,IReadOnlyDictionary<string,string> CustomerContext,
    IReadOnlyList<Product> Candidates,IReadOnlyList<string> Evidence,int Turn);

public interface ICommerceAgentReasoningEngine
{
    Task<CommerceAgentDecision> DecideAsync(CommerceAgentContext context,CancellationToken cancellationToken);
}
public interface ICommercePlannerWorker:ICommerceAgentReasoningEngine { }

public interface ICommerceAnalystWorker
{
    Task<CustomerRequestUnderstanding> AnalyzeAsync(string instruction,string? openObjective,IReadOnlySet<string> capabilities,CancellationToken cancellationToken);
}
public sealed class CommerceAnalystWorker(ICustomerRequestUnderstandingAgent understanding):ICommerceAnalystWorker
{
    public Task<CustomerRequestUnderstanding> AnalyzeAsync(string instruction,string? openObjective,IReadOnlySet<string> capabilities,CancellationToken cancellationToken)=>
        understanding.UnderstandAsync(instruction,openObjective,capabilities,cancellationToken);
}

public sealed record CommerceAuditResult(bool Accepted,bool RequiresRevision,IReadOnlyList<string> Findings,string? RevisionInstruction);
public interface ICommerceAuditorWorker
{
    Task<CommerceAuditResult> AuditAsync(CommerceAgentContext context,CommerceAgentDecision decision,MerchantPlanningQuote quote,CancellationToken cancellationToken);
}

/// <summary>Semantic reasoning is limited to planning. It receives provider facts, never credentials or payment tools.</summary>
public sealed class SemanticKernelCommerceAgentReasoningEngine(IConfiguration configuration):ICommercePlannerWorker
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=true};
    public async Task<CommerceAgentDecision> DecideAsync(CommerceAgentContext context,CancellationToken cancellationToken)
    {
        var kernel=AgentFactory.CreateLiveKernel();
        var chat=kernel.GetRequiredService<IChatCompletionService>();
        var history=new ChatHistory();
        history.AddSystemMessage("""
            You are a consumer commerce planning agent. Use provider evidence rather than memorised catalogues.
            Return JSON only: {"searchQueries":[],"items":[{"concept":"provider product id or search concept","quantity":1,"allowSubstitution":true}],"summary":"...","message":"..."}.
            If candidates are empty, return concise searchQueries that discover the objective. When candidates exist,
            select the best complete, available combination within the stated budget. Use exact productId values when
            possible. Revise after quote errors or over-budget evidence. Never invent prices, availability or products.
            """);
        history.AddUserMessage(JsonSerializer.Serialize(context,JsonOptions));
        var timeoutSeconds=Math.Clamp(configuration.GetValue("ConsumerPilot:Planning:AgentTurnTimeoutSeconds",20),5,60);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var response=await chat.GetChatMessageContentAsync(history,cancellationToken:timeout.Token);
        var text=response.Content??string.Empty;var start=text.IndexOf('{');var end=text.LastIndexOf('}');
        if(start<0||end<=start)throw new InvalidOperationException("The commerce agent returned no valid decision.");
        return JsonSerializer.Deserialize<CommerceAgentDecision>(text[start..(end+1)],JsonOptions)
            ??throw new InvalidOperationException("The commerce agent decision was unreadable.");
    }
}

/// <summary>An independent critic. It cannot change the basket and can only accept, reject, or request revision.</summary>
public sealed class SemanticKernelCommerceAuditorWorker(IConfiguration configuration):ICommerceAuditorWorker
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=true};
    public async Task<CommerceAuditResult> AuditAsync(CommerceAgentContext context,CommerceAgentDecision decision,MerchantPlanningQuote quote,CancellationToken cancellationToken)
    {
        if(quote.Total>context.MaximumAmount)return new(false,true,["AUTHORITATIVE_QUOTE_EXCEEDS_BUDGET"],"Find a cheaper complete option.");
        if(quote.Items.Count==0)return new(false,false,["EMPTY_QUOTE"],null);
        var kernel=AgentFactory.CreateLiveKernel();var chat=kernel.GetRequiredService<IChatCompletionService>();var history=new ChatHistory();
        history.AddSystemMessage("""
            You are an independent commerce proposal auditor. You cannot edit baskets or execute purchases.
            Check completeness, catalogue grounding, quantities, customer constraints, budget and authoritative quote evidence.
            Return JSON only: {"accepted":true,"requiresRevision":false,"findings":[],"revisionInstruction":null}.
            Reject invented evidence. Request revision only when the planner can correct the proposal.
            """);
        history.AddUserMessage(JsonSerializer.Serialize(new{context,decision,quote},JsonOptions));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("ConsumerPilot:Planning:AuditorTimeoutSeconds",15),5,60)));
        var response=await chat.GetChatMessageContentAsync(history,cancellationToken:timeout.Token);var text=response.Content??"";
        var start=text.IndexOf('{');var end=text.LastIndexOf('}');
        if(start<0||end<=start)return new(false,false,["AUDITOR_RESPONSE_INVALID"],null);
        try{return JsonSerializer.Deserialize<CommerceAuditResult>(text[start..(end+1)],JsonOptions)??new(false,false,["AUDITOR_RESPONSE_INVALID"],null);}
        catch(JsonException){return new(false,false,["AUDITOR_RESPONSE_INVALID"],null);}
    }
}

/// <summary>
/// Application-controlled agent loop. The model chooses discovery and basket actions; the application executes
/// provider capabilities, verifies authoritative quotes and enforces bounded turns before producing a proposal.
/// </summary>
public sealed class ConsumerCommerceAgentLoop
{
    private readonly IConsumerPlanningStore _store;private readonly ICommerceAnalystWorker _analyst;private readonly ICommercePlannerWorker _planner;private readonly ICommerceAuditorWorker _auditor;
    private readonly IReadOnlyList<IObjectiveExpansionCapability> _expanders;private readonly int _maximumTurns;
    public ConsumerCommerceAgentLoop(IConsumerPlanningStore store,ICommerceAnalystWorker analyst,ICommercePlannerWorker planner,ICommerceAuditorWorker auditor,
        IEnumerable<IObjectiveExpansionCapability> expanders,IConfiguration configuration)
    {_store=store;_analyst=analyst;_planner=planner;_auditor=auditor;_expanders=expanders.ToArray();_maximumTurns=Math.Clamp(configuration.GetValue("ConsumerPilot:Planning:AgentLoopMaximumTurns",8),2,20);}

    public async Task<ConsumerPurchasePlan> RunAsync(ConsumerActionPlanningContext request,ProviderPlanningSession provider,CancellationToken token)
    {
        if(provider.Provider is not ICommerceConnector connector)throw new InvalidOperationException("Provider does not expose commerce planning capabilities.");
        var now=DateTimeOffset.UtcNow;var existing=request.ConversationId is null?null:_store.FindOwned(request.ConversationId,request.PrincipalId);
        if(request.ConversationId is not null&&existing is null)throw new UnauthorizedAccessException("Conversation not found or belongs to another principal.");
        var previous=existing is null?null:JsonSerializer.Deserialize<ConsumerPlanningState>(existing.StateJson)?.LatestPlan;
        if(previous is {Status:PurchasePlanningStatus.Ready}&&IsApproval(request.Instruction))
            return previous with{ConversationId=existing!.ConversationId,InteractionDecision=PurchaseInteractionDecision.Execute,Questions=[]};
        var analysis=request.Analysis??await _analyst.AnalyzeAsync(request.Instruction,existing?.Objective,request.AvailableCapabilities,token);
        var parsedBudget=analysis.ExplicitBudget;
        if(parsedBudget is null)
            return PurchaseRequestBudgetGate.Clarification() with{InteractionDecision=PurchaseInteractionDecision.Clarify};

        var conversation=existing??_store.Create(request.PrincipalId,request.Instruction,"{}",now);
        var tools=new ConnectorMerchantPlanningToolset(connector,request.PrincipalId,"GBP",_expanders);
        var candidates=new Dictionary<string,Product>(StringComparer.OrdinalIgnoreCase);var evidence=new List<string>();
        var used=new List<string>{"analyst:get_customer_context","analyst:discover_providers"};
        var preferences=_store.Preferences(request.PrincipalId);
        for(var turn=1;turn<=_maximumTurns;turn++)
        {
            var context=new CommerceAgentContext(analysis.Objective,parsedBudget.Value,"GBP",tools.Context,preferences,candidates.Values.ToArray(),evidence,turn);
            CommerceAgentDecision decision;
            try{decision=await _planner.DecideAsync(context,token);used.Add("planner:reason");}
            catch(Exception ex) when(ex is not OperationCanceledException||!token.IsCancellationRequested)
            {evidence.Add($"reasoning_error:{ex.GetType().Name}");break;}
            if(decision.SearchQueries.Count>0)
            {
                used.Add("search");
                foreach(var query in decision.SearchQueries.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach(var product in await tools.SearchAsync(query,token))candidates[product.ProductId]=product;
                used.Add("get_details");used.Add("compare_options");evidence.Add($"search returned {candidates.Count} available candidates");continue;
            }
            if(decision.Items.Count==0){evidence.Add("build_request rejected: no items");continue;}
            used.Add("build_request");
            try
            {
                var requested=decision.Items.Select(x=>new ProposedMerchantItem(x.Concept,x.Quantity,x.AllowSubstitution)).ToArray();
                used.Add("get_authoritative_quote");var quote=await tools.QuoteAsync(requested,token);
                if(quote.Total>parsedBudget.Value){used.Add("revise_proposal");evidence.Add($"authoritative total {quote.Total:0.00} exceeds budget {parsedBudget:0.00}");continue;}
                used.Add("check_execution_readiness");used.Add("prepare_purchase");
                var audit=await _auditor.AuditAsync(context,decision,quote,token);used.Add("auditor:review");
                foreach(var finding in audit.Findings)evidence.Add($"audit:{finding}");
                if(audit.RequiresRevision){used.Add("auditor:revise");if(!string.IsNullOrWhiteSpace(audit.RevisionInstruction))evidence.Add(audit.RevisionInstruction);continue;}
                if(!audit.Accepted)return RejectAudit(conversation,parsedBudget.Value,used,audit,now,turn);
                used.Add("auditor:accepted");
                var items=quote.Items.Select(x=>new PlannedPurchaseItem(x.ProductId,x.Quantity)).ToArray();
                var plan=new ConsumerPurchasePlan(PurchasePlanningStatus.Ready,decision.Summary,
                    string.IsNullOrWhiteSpace(decision.Message)?$"{quote.MerchantName} verified the order at £{quote.Total:0.00}. Shall I go ahead?":decision.Message,
                    parsedBudget.Value,quote.Currency,items,["Shall I go ahead?"],quote.Total,used,conversation.ConversationId,turn,PurchaseInteractionDecision.Propose);
                Save(conversation,plan,quote,now);return plan;
            }
            catch(Exception ex) when(ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {used.Add("revise_proposal");evidence.Add($"quote rejected: {ex.Message}");}
        }
        var failed=new ConsumerPurchasePlan(PurchasePlanningStatus.NeedsInput,"Agent needs clarification",
            "I could not build a sufficiently evidenced proposal. Please clarify the product or service you want.",parsedBudget.Value,"GBP",[],
            ["What should I change or prioritise?"],null,used,conversation.ConversationId,_maximumTurns,PurchaseInteractionDecision.Clarify);
        Save(conversation,failed,null,now);return failed;
    }

    private ConsumerPurchasePlan RejectAudit(ConsumerPlanningConversation conversation,decimal budget,List<string> used,CommerceAuditResult audit,DateTimeOffset now,int turn)
    {
        var plan=new ConsumerPurchasePlan(PurchasePlanningStatus.NeedsInput,"Proposal rejected by independent audit",
            "I could not verify the proposal safely. No purchase will be attempted.",budget,"GBP",[],["Please revise your request."],null,used,
            conversation.ConversationId,turn,PurchaseInteractionDecision.Clarify);
        Save(conversation,plan,null,now);return plan;
    }

    private void Save(ConsumerPlanningConversation conversation,ConsumerPurchasePlan plan,MerchantPlanningQuote? quote,DateTimeOffset now)
    {
        var state=new ConsumerPlanningState(conversation.Objective,new(),[plan.Summary],plan.Questions.ToList(),[],[],plan.ToolsUsed.ToList(),plan.Status,plan);
        _store.Save(conversation with{Status=plan.InteractionDecision,StateJson=JsonSerializer.Serialize(state),UpdatedAt=now,Version=conversation.Version+1});
        _store.Append(new($"planning_turn_{Guid.NewGuid():N}",conversation.ConversationId,_store.Turns(conversation.ConversationId).Count+1,"assistant","agent-loop",plan.Message,null,null,null,now));
        if(quote is not null)_store.ReplaceReservations(conversation.ConversationId,quote.Items.Select(x=>new ConsumerProductReservation(
            $"product_hold_{Guid.NewGuid():N}",conversation.ConversationId,x.ProductId,x.Quantity,x.UnitPrice,quote.Currency,"Reserved",now,quote.ExpiresAt)).ToArray());
    }
    private static bool IsApproval(string value)=>System.Text.RegularExpressions.Regex.IsMatch(value.Trim(),@"^(?:yes|yes,?\s+please|go\s+ahead|confirm|proceed|buy\s+it|pay)[.!]?$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
