using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Consumer;
using AgentTrust.PaymentMethods;
using AgentTrust.Mandates;
using AgentTrust.Scheduling;
using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stripe;

namespace AgentTrust.Api.Controllers;

[ApiController, Route("api/consumer"), Authorize(Policy = "Consumer")]
public sealed class ConsumerController : ControllerBase
{
    private readonly IConsumerTaskStore _tasks; private readonly IPurchaseExecutionStore _purchases;
    private readonly IPaymentMethodStore _paymentMethods; private readonly AgentPurchaseOrchestrator _orchestrator;
    private readonly ICommerceConnector _connector;
    private readonly IMandateStore _mandates; private readonly ICommerceDurability _durability; private readonly IPurchaseAuditSink _audit;
    private readonly IScheduledOccurrenceStore _occurrences;
    private readonly IAgentRegistry _agents;private readonly IPrincipalBindingStore _bindings;private readonly IPrincipalStore _principals;
    private readonly ConsumerCommerceAgent _commerceAgent;private readonly ConsumerCommerceOperator _commerceOperator;private readonly IConfiguration _configuration;
    private readonly IConsumerPlanningStore _planning;private readonly IConsumerMemoryService _memory;private readonly MandateLimitChangeService _limitChanges;private readonly IMandateLimitChangeStore _limitChangeStore;private readonly IAuthorizationService _authorization;
    private readonly IReadOnlyList<IObjectiveExpansionCapability> _objectiveCapabilities;private readonly ICustomerRequestUnderstandingAgent _understanding;
    public ConsumerController(IConsumerTaskStore tasks, IPurchaseExecutionStore purchases,
        IPaymentMethodStore paymentMethods, AgentPurchaseOrchestrator orchestrator, MerchantConnectorRegistry connectors,
        IMandateStore mandates, ICommerceDurability durability, IPurchaseAuditSink audit,IScheduledOccurrenceStore occurrences,
        IAgentRegistry agents,IPrincipalBindingStore bindings,IPrincipalStore principals,ConsumerCommerceAgent commerceAgent,ConsumerCommerceOperator commerceOperator,IConfiguration configuration,IConsumerPlanningStore planning,IConsumerMemoryService memory,MandateLimitChangeService limitChanges,IMandateLimitChangeStore limitChangeStore,IAuthorizationService authorization,IEnumerable<IObjectiveExpansionCapability> objectiveCapabilities,ICustomerRequestUnderstandingAgent understanding)
    { _tasks = tasks; _purchases = purchases; _paymentMethods = paymentMethods; _orchestrator = orchestrator; _connector = connectors.All.Single(); _mandates=mandates;_durability=durability;_audit=audit;_occurrences=occurrences;_agents=agents;_bindings=bindings;_principals=principals;_commerceAgent=commerceAgent;_commerceOperator=commerceOperator;_configuration=configuration;_planning=planning;_memory=memory;_limitChanges=limitChanges;_limitChangeStore=limitChangeStore;_authorization=authorization;_objectiveCapabilities=objectiveCapabilities.ToArray();_understanding=understanding; }

    [HttpPost("agents"),Authorize(Policy="StepUp")]
    public ActionResult<AgentIdentity> CreateAgent(CreateConsumerAgentRequest request)
    {
        var principal=PrincipalId();if(_agents.Find(request.AgentId)is{} existing)return existing.PrincipalId==principal?Ok(existing):Conflict("Agent already exists.");
        if(_principals.Find(principal)is null)_principals.Register(new Principal(principal,request.DisplayName??principal,DateTimeOffset.UtcNow));
        var now=DateTimeOffset.UtcNow;var agent=new AgentIdentity(request.AgentId,principal,"consumer-purchase","Development",CredentialStatus.Active,now,now.AddYears(1),"authenticated-consumer");
        _agents.Register(agent);_bindings.Bind(new PrincipalBinding(request.AgentId,principal,now,true,"authenticated-api"));return CreatedAtAction(nameof(GetAgent),new{id=agent.AgentId},agent);
    }
    [HttpGet("agents/{id}")]public ActionResult<AgentIdentity> GetAgent(string id)=>_agents.Find(id)is{} agent&&agent.PrincipalId==PrincipalId()?Ok(agent):NotFound();

    /// <summary>Create a recurring agent purchase task bound to an owned mandate and payment method.</summary>
    [HttpPost("tasks"), Authorize(Policy = "StepUp")]
    public ActionResult<ConsumerPurchaseTask> CreateTask(CreateConsumerTaskRequest request)
    {
        var principal = PrincipalId();
        if (_mandates.Find(request.MandateId) is not { } mandate || mandate.PrincipalId != principal) return Forbid();
        if (_paymentMethods.Find(request.PaymentMethodId) is not { } method || method.PrincipalId != principal) return Forbid();
        var merchant=NormalizeMerchant(request.MerchantId);
        if(mandate.AgentId.Length==0||!string.Equals(mandate.Merchant,merchant,StringComparison.OrdinalIgnoreCase)
            ||!string.Equals(mandate.Currency,request.Currency,StringComparison.OrdinalIgnoreCase))return BadRequest("Task scope must match the mandate.");
        if(request.MaximumAmount<=0||request.MaximumAmount>mandate.PerTransactionLimit)return BadRequest("Maximum amount exceeds the standing mandate.");
        var next=NextOccurrence(request.Schedule,request.Timezone,DateTimeOffset.UtcNow);
        var task = new ConsumerPurchaseTask($"ctask_{Guid.NewGuid():N}", principal, mandate.AgentId,
            new HashSet<string>([merchant],StringComparer.OrdinalIgnoreCase), $"Weekly:{request.Schedule.DayOfWeek}:{request.Schedule.LocalTime}", request.Timezone,
            request.MaximumAmount, request.Currency, request.ShoppingList.Select(x=>new ShoppingListItem(x.Query,x.Quantity,x.PreferredProductId,x.MaximumUnitPrice,x.RequiredForOutcome)).ToList(),
            new PurchasePreference(request.DeliveryAddressReference??"dev-address", null,
                request.SubstitutionPolicy.Allowed?SubstitutionPolicy.SameOrLowerPrice:SubstitutionPolicy.Never,new Dictionary<string,string>{{"instruction",request.Instruction}}),
            request.MandateId, request.PaymentMethodId, ConsumerTaskStatus.Active,
            next, DateTimeOffset.UtcNow);
        _tasks.Save(task); return CreatedAtAction(nameof(GetTask), new { id = task.TaskId }, task);
    }
    [HttpGet("tasks")] public ActionResult<IReadOnlyList<ConsumerPurchaseTask>> GetTasks() => Ok(_tasks.FindByPrincipal(PrincipalId()));
    [HttpGet("tasks/{id}")] public ActionResult<ConsumerPurchaseTask> GetTask(string id) => _tasks.FindOwned(id, PrincipalId()) is { } task ? Ok(task) : NotFound();
    /// <summary>Run the purchase task through the deterministic trust boundary. Reusing the same scheduledFor value is idempotent.</summary>
    [HttpPost("tasks/{id}/run")]
    public async Task<ActionResult<PurchaseOrchestrationResult>> Run(string id, RunPurchaseRequest request, CancellationToken cancellationToken)
    { try { var task=_tasks.FindOwned(id,PrincipalId());if(task is null)return NotFound();var scheduled=request.ScheduledFor??task.NextExecutionAt;
        if(!_occurrences.TryClaim(task.TaskId,scheduled,DateTimeOffset.UtcNow,out var occurrence))return Ok(await _orchestrator.RunAsync(id,PrincipalId(),scheduled,_connector,new(request.LiveMode,request.ExplicitLiveConfirmation),cancellationToken));
        try{var result=await _orchestrator.RunAsync(id,PrincipalId(),scheduled,_connector,new(request.LiveMode,request.ExplicitLiveConfirmation),cancellationToken);_occurrences.Complete(occurrence!.OccurrenceId,result.Execution.State is not(PurchaseExecutionState.Failed or PurchaseExecutionState.Unknown));return Ok(result);}catch{_occurrences.Complete(occurrence!.OccurrenceId,false);throw;}
      } catch (UnauthorizedAccessException) { return Forbid(); } }
    [HttpPost("tasks/{id}/cancel"),Authorize(Policy="StepUp")]
    public ActionResult<ConsumerPurchaseTask> CancelTask(string id){var task=_tasks.FindOwned(id,PrincipalId());if(task is null)return NotFound();task=task with{Status=ConsumerTaskStatus.Cancelled};_tasks.Save(task);return Ok(task);}
    [HttpGet("payment-methods")] public ActionResult<IReadOnlyList<AgentTrust.PaymentMethods.PaymentMethod>> PaymentMethods() => Ok(_paymentMethods.FindByPrincipal(PrincipalId()));
    [HttpGet("setup/status")]
    public ActionResult<ConsumerSetupStatus> SetupStatus()=>Ok(BuildSetupStatus(PrincipalId(),DateTimeOffset.UtcNow));
    [HttpPost("memory/corrections")]
    public ActionResult<IReadOnlyList<ConsumerMemoryEntry>> CaptureMemory(ConsumerMemoryCorrectionRequest request)=>Ok(_memory.CaptureCorrections(PrincipalId(),request.Message));
    [HttpGet("memory/export")]
    public ActionResult<IReadOnlyList<ConsumerMemoryEntry>> ExportMemory()=>Ok(_memory.Export(PrincipalId()));
    [HttpDelete("memory/{id}"),Authorize(Policy="StepUp")]
    public IActionResult DeleteMemory(string id)=>_memory.Delete(PrincipalId(),id)?NoContent():NotFound();
    /// <summary>Create a Stripe SetupIntent for securely collecting a reusable off-session payment method.</summary>
    [HttpPost("payment-methods/setup-intent"), Authorize(Policy = "StepUp")]
    public async Task<ActionResult<object>> CreatePaymentMethodSetupIntent(CancellationToken token)
    {
        if(!StripeConfigured())return Problem("Stripe payment setup is not configured.",statusCode:503);
        try
        {
            var client=StripeClient();var customerId=await EnsureStripeCustomer(PrincipalId(),client,token);
            var intent=await new SetupIntentService(client).CreateAsync(new SetupIntentCreateOptions{Customer=customerId,Usage="off_session",PaymentMethodTypes=["card"]},cancellationToken:token);
            return Ok(new{setupIntentId=intent.Id,clientSecret=intent.ClientSecret,publishableKey=_configuration["Stripe:PublishableKey"]});
        }
        catch(StripeException ex){return UnprocessableEntity(new{code="STRIPE_SETUP_FAILED",message=ex.StripeError?.Message??ex.Message});}
    }
    [HttpPost("payment-methods/setup"), Authorize(Policy = "StepUp")]
    public async Task<ActionResult<AgentTrust.PaymentMethods.PaymentMethod>> SetupPaymentMethod(ProviderPaymentMethodRequest request,CancellationToken token)
    {
        var service = new AgentTrust.PaymentMethods.PaymentMethodService(new RejectRawCardTokenizationProvider(), _paymentMethods);
        try
        {
            var method=service.ConnectProviderToken(PrincipalId(),request.Provider,request.ProviderToken,request.CardBrand,request.Last4,request.ExpiryMonth,request.ExpiryYear);
            if(request.Provider.Equals("Stripe",StringComparison.OrdinalIgnoreCase)&&StripeConfigured())
            {
                var client=StripeClient();var customerId=await EnsureStripeCustomer(PrincipalId(),client,token);var stripeMethods=new Stripe.PaymentMethodService(client);
                var providerMethod=await stripeMethods.GetAsync(request.ProviderToken,cancellationToken:token);
                if(string.IsNullOrWhiteSpace(providerMethod.CustomerId))providerMethod=await stripeMethods.AttachAsync(request.ProviderToken,new PaymentMethodAttachOptions{Customer=customerId},cancellationToken:token);
                if(!string.Equals(providerMethod.CustomerId,customerId,StringComparison.Ordinal))return Conflict(new{code="PAYMENT_METHOD_ATTACHED_TO_DIFFERENT_CUSTOMER"});
                method=method with{ProviderCustomerReference=customerId,CardBrand=providerMethod.Card?.Brand??method.CardBrand,Last4=providerMethod.Card?.Last4??method.Last4,ExpiryMonth=(int)(providerMethod.Card?.ExpMonth??method.ExpiryMonth),ExpiryYear=(int)(providerMethod.Card?.ExpYear??method.ExpiryYear)};_paymentMethods.Save(method);
            }
            return Ok(method);
        }
        catch(StripeException ex){return UnprocessableEntity(new{code="STRIPE_PAYMENT_METHOD_SETUP_FAILED",message=ex.StripeError?.Message??ex.Message});}
        catch(InvalidOperationException){return Conflict("The provider payment method is already registered.");}
    }
    [HttpGet("purchases")] public ActionResult<IReadOnlyList<PurchaseExecution>> Purchases() => Ok(_purchases.FindByPrincipal(PrincipalId()));
    /// <summary>Submit one plain-language shopping request. The agent plans the basket; only the deterministic trust layer can authorise payment.</summary>
    [HttpPost("purchases/request"),Consumes("text/plain")]
    public async Task<ActionResult<object>> RequestPurchase([FromBody]string instruction,CancellationToken token)
    {
        var principal=PrincipalId();var now=DateTimeOffset.UtcNow;
        if(TryParseLimitChange(instruction,out var limitKind,out var newLimit))
        {
            var current=_mandates.FindByPrincipal(principal).Where(x=>x.IsActive(now)&&string.Equals(x.Purpose,"groceries",StringComparison.OrdinalIgnoreCase)).OrderByDescending(x=>x.Version).FirstOrDefault();if(current is null)return UnprocessableEntity(new{code="NO_ACTIVE_GROCERY_MANDATE",message="There is no active grocery mandate to change."});
            var proposal=_limitChanges.Propose(current.MandateId,principal,limitKind=="transaction"?newLimit:null,limitKind=="weekly"?newLimit:null,limitKind=="monthly"?newLimit:null,now,"chat");
            return Ok(new{interactionDecision="REQUIRES_STEP_UP",message=$"Changing your {limitKind} spending limit changes the agent's financial authority. Confirm the prepared £{newLimit:0.00} {limitKind} limit using secure verification.",paymentAttempted=false,trustBoundaryInvoked=false,proposal,confirmEndpoint=$"/api/consumer/mandates/{current.MandateId}/limit-change-proposals/{proposal.ProposalId}/confirm"});
        }
        var setup=BuildSetupStatus(principal,now);if(!setup.IsReady)return Conflict(setup);
        var startsNew=ContainsAny(instruction,"new order","new transaction","start again","start over");
        var latestConversation=_planning.FindLatestOpen(principal,now.AddMinutes(-30));
        if(latestConversation is not null&&IsConversationReset(instruction))
        {
            _planning.ReplaceReservations(latestConversation.ConversationId,[]);
            _planning.Save(latestConversation with{Status="RESET",UpdatedAt=now,Version=latestConversation.Version+1});
            _planning.Append(new($"planning_turn_{Guid.NewGuid():N}",latestConversation.ConversationId,_planning.Turns(latestConversation.ConversationId).Count+1,"user","reset",instruction,null,null,null,now));
            var resetPlan=new ConsumerPurchasePlan("RESET","New request started","Your previous request is closed. What would you like me to buy?",0,"GBP",[],[],null,[],InteractionDecision:"CANCELLED");
            return Ok(new{instruction,planning=resetPlan,paymentAttempted=false,trustBoundaryInvoked=false});
        }
        if(latestConversation is not null&&ContainsAny(instruction,"cancel","never mind","nevermind","stop this order"))
        {
            var cancelledPlan=new ConsumerPurchasePlan("CANCELLED","Purchase cancelled","I cancelled the prepared purchase. Nothing was charged.",0,"GBP",[],[],null,[],InteractionDecision:"CANCELLED");
            _planning.ReplaceReservations(latestConversation.ConversationId,[]);
            _planning.Save(latestConversation with{Status="CANCELLED",UpdatedAt=now,Version=latestConversation.Version+1});
            _planning.Append(new($"planning_turn_{Guid.NewGuid():N}",latestConversation.ConversationId,_planning.Turns(latestConversation.ConversationId).Count+1,"user","cancel",instruction,null,null,null,now));
            return Ok(new{instruction,planning=cancelledPlan,paymentAttempted=false,trustBoundaryInvoked=false});
        }
        var understanding=await _understanding.UnderstandAsync(instruction,latestConversation?.Objective,CommerceCapabilityCatalog.Describe(_connector),token);
        var hasExplicitBudget=understanding.ExplicitBudget is not null;
        var explicitContinuation=latestConversation is not null&&(understanding.ContinuesOpenProposal||PurchaseRequestBudgetGate.IsExplicitContinuation(instruction));
        if(!hasExplicitBudget&&!explicitContinuation)
        {
            ObjectiveClarification? suggestion=null;
            foreach(var capability in _objectiveCapabilities)
                if(await capability.ClarifyAsync(instruction,token) is { } candidate){suggestion=candidate;break;}
            var planning=PurchaseRequestBudgetGate.Clarification(suggestion);
            var state=new ConsumerPlanningState(instruction,new(),[],[planning.Questions[0]],[],[],planning.ToolsUsed.ToList(),planning.Status,planning);
            var conversation=_planning.Create(principal,instruction,JsonSerializer.Serialize(state),now);
            planning=planning with{ConversationId=conversation.ConversationId};
            state=state with{LatestPlan=planning};
            _planning.Save(conversation with{Status=PurchasePlanningStatus.NeedsInput,StateJson=JsonSerializer.Serialize(state),UpdatedAt=now,Version=conversation.Version+1});
            _planning.Append(new($"planning_turn_{Guid.NewGuid():N}",conversation.ConversationId,1,"user","message",instruction,null,null,null,now));
            _planning.Append(new($"planning_turn_{Guid.NewGuid():N}",conversation.ConversationId,2,"assistant","clarification",planning.Message,null,null,null,now));
            return Ok(new{instruction,understanding,planning,conversationPolicy=_planning.GetPolicy(principal),paymentAttempted=false,trustBoundaryInvoked=false});
        }
        var resumesOpenConversation=latestConversation is not null&&explicitContinuation;
        var managedConversationId=!startsNew&&resumesOpenConversation?latestConversation?.ConversationId:null;
        var planningContext=new ConsumerActionPlanningContext(principal,managedConversationId,instruction,_connector.MerchantId,_connector.MerchantName,
            CommerceCapabilityCatalog.Describe(_connector),understanding);
        ConsumerCommercePreparation prepared;try{prepared=await _commerceAgent.PrepareAsync(planningContext,now,token);}catch(UnauthorizedAccessException){return NotFound();}
        var plan=prepared.Plan;
        if(prepared.FailureCode is not null)return UnprocessableEntity(new{code=prepared.FailureCode,message=prepared.FailureMessage,planning=plan,paymentAttempted=false,trustBoundaryInvoked=false});
        if(plan.InteractionDecision!=PurchaseInteractionDecision.Execute)return Ok(new{instruction,planning=plan,merchantQuote=prepared.Quote,readiness=prepared.IsExecutable?"READY_FOR_CONFIRMATION":"NEEDS_INPUT",conversationPolicy=_planning.GetPolicy(principal),paymentAttempted=false,trustBoundaryInvoked=false});
        if(!(await _authorization.AuthorizeAsync(User,"StepUp")).Succeeded)return Forbid();
        var preparedTask=_commerceAgent.PreparePurchase(prepared,principal,instruction,now);
        var result=await _commerceOperator.ExecuteAsync(prepared,preparedTask,principal,token);
        return Ok(new{instruction,planning=plan,paymentAttempted=result.Execution.State is PurchaseExecutionState.CheckoutSubmitted or PurchaseExecutionState.Processing or PurchaseExecutionState.Purchased,trustBoundaryInvoked=true,taskId=preparedTask.TaskId,result.Execution,result.Intent,result.Receipt});
    }
    private static bool IsConversationReset(string instruction)=>System.Text.RegularExpressions.Regex.IsMatch(
        instruction.Trim(),@"^(?:start again|start over|new order|new transaction)[.!]?$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    [HttpGet("purchases/{id}")] public ActionResult<PurchaseExecution> Purchase(string id) => _purchases.FindOwned(id, PrincipalId()) is { } item ? Ok(item) : NotFound();
    [HttpGet("purchases/{id}/audit")] public ActionResult<object> PurchaseAudit(string id)
    { var purchase=_purchases.FindOwned(id,PrincipalId());if(purchase is null)return NotFound();var events=_audit.Find(purchase.PurchaseIntentId);var valid=events.Count>0;for(var i=0;i<events.Count;i++){var expectedPrevious=i==0?events[i].PreviousHash:events[i-1].CurrentHash;valid&=events[i].PreviousHash==expectedPrevious&&events[i].CurrentHash==PurchaseAuditHash.Compute(events[i],events[i].PreviousHash);}return Ok(new{isValid=valid,eventCount=events.Count,events}); }
    [HttpGet("purchases/{id}/receipt")] public ActionResult<object> Receipt(string id){var principal=PrincipalId();var purchase=_purchases.FindOwned(id,principal);if(purchase is null)return NotFound();var receipt=_durability.FindReceiptByPurchaseOwned(purchase.PurchaseIntentId,principal);var intent=_durability.FindIntentOwned(purchase.PurchaseIntentId,principal);if(receipt is null||intent is null)return NotFound();var mandate=_mandates.Find(intent.MandateId);return Ok(new{receiptId=receipt.ReceiptId,purchaseId=id,merchant=intent.MerchantName,items=intent.BasketItems,subtotal=intent.Subtotal,deliveryFee=intent.DeliveryFee,total=receipt.TotalAmount,currency=receipt.Currency,paymentIntentId=receipt.ProviderReference,purchasedAt=receipt.PurchasedAt,taskId=intent.TaskId,mandateId=intent.MandateId,mandateVersion=mandate?.Version});}
    [HttpGet("mandates")] public ActionResult<IReadOnlyList<FinancialMandate>> Mandates()=>Ok(_mandates.FindByPrincipal(PrincipalId()));
    [HttpGet("mandates/{id}")] public ActionResult<FinancialMandate> Mandate(string id) => _mandates.Find(id) is { } m&&m.PrincipalId==PrincipalId()?Ok(m):NotFound();
    [HttpGet("mandates/{id}/history")] public ActionResult<IReadOnlyList<FinancialMandate>> MandateHistory(string id)
    { var history=_mandates.GetHistory(id);return history.Count==0||history.Any(x=>x.PrincipalId!=PrincipalId())?NotFound():Ok(history); }
    [HttpPost("mandates/{id}/limit-change-proposals")]
    public ActionResult<MandateLimitChangeProposal> ProposeLimitChange(string id,MandateLimitChangeRequest request)
    {try{return Ok(_limitChanges.Propose(id,PrincipalId(),request.PerTransactionLimit,request.WeeklyLimit,request.MonthlyLimit,DateTimeOffset.UtcNow,"api"));}catch(KeyNotFoundException){return NotFound();}catch(UnauthorizedAccessException){return Forbid();}catch(ArgumentException ex){return BadRequest(ex.Message);}catch(InvalidOperationException ex){return Conflict(ex.Message);}}
    [HttpGet("mandates/{id}/limit-change-proposals/{proposalId}")]
    public ActionResult<MandateLimitChangeProposal> GetLimitChange(string id,string proposalId){var p=_limitChangeStore.FindOwned(proposalId,PrincipalId());return p is not null&&p.MandateId==id?Ok(p):NotFound();}
    [HttpPost("mandates/{id}/limit-change-proposals/{proposalId}/confirm"),Authorize(Policy="StepUp")]
    public ActionResult<object> ConfirmLimitChange(string id,string proposalId)
    {var principal=PrincipalId();var proposal=_limitChangeStore.FindOwned(proposalId,principal);if(proposal is null||proposal.MandateId!=id)return NotFound();if(!_limitChangeStore.TryApply(proposalId,principal,DateTimeOffset.UtcNow,out var mandate,out var reasons))return Conflict(new{code=reasons.FirstOrDefault()??"LIMIT_CHANGE_FAILED",reasons});return Ok(new{status="APPLIED",mandate,message="The new limits are active. The previous mandate version remains in the audit history."});}
    /// <summary>Create a bounded consumer spending mandate. The authenticated principal is always the owner.</summary>
    [HttpPost("mandates"), Authorize(Policy = "StepUp")]
    public ActionResult<FinancialMandate> CreateMandate(CreateMandateRequest request)
    {
        var principal=PrincipalId();
        if(_paymentMethods.Find(request.PaymentMethodId) is not { } payment||payment.PrincipalId!=principal)return Forbid();
        if(request.MerchantIds.Count!=1)return BadRequest("The grocery pilot requires exactly one merchant.");
        if(request.ValidUntil<=request.ValidFrom||request.PerTransactionLimit<=0)return BadRequest("Invalid mandate validity or limit.");
        var now=DateTimeOffset.UtcNow;var mandate=new FinancialMandate($"mandate_{Guid.NewGuid():N}",principal,request.AgentId,NormalizeMerchant(request.MerchantIds[0]),
            "groceries",request.PaymentMethodId,request.PerTransactionLimit,request.WeeklyLimit,null,request.Currency,
            new Dictionary<string,string>(),AboveLimitAction.RequireApproval,MandateStatus.Active,now,request.ValidUntil){EffectiveFrom=request.ValidFrom};
        _mandates.Save(mandate);return CreatedAtAction(nameof(Mandate),new{id=mandate.MandateId},mandate);
    }
    [HttpPost("mandates/{id}/revoke"),Authorize(Policy="StepUp")]
    public ActionResult<FinancialMandate> RevokeMandate(string id){var current=_mandates.Find(id);if(current is null)return NotFound();if(current.PrincipalId!=PrincipalId())return Forbid();var revoked=current with{Version=current.Version+1,Status=MandateStatus.Suspended,SupersedesMandateId=current.MandateId};_mandates.Save(revoked);return Ok(revoked);}
    [HttpPost("purchases/{id}/approve"), Authorize(Policy = "StepUp")] public async Task<ActionResult<PurchaseOrchestrationResult>> Approve(string id, CancellationToken token){var p=_purchases.FindOwned(id,PrincipalId());if(p is null)return NotFound();return Ok(await _orchestrator.ResolveAsync(p.PurchaseIntentId,PrincipalId(),true,PrincipalId(),token,_connector));}
    [HttpPost("purchases/{id}/reject")] public async Task<ActionResult<PurchaseOrchestrationResult>> Reject(string id, CancellationToken token){var p=_purchases.FindOwned(id,PrincipalId());if(p is null)return NotFound();return Ok(await _orchestrator.ResolveAsync(p.PurchaseIntentId,PrincipalId(),false,PrincipalId(),token,_connector));}
    [HttpPost("pilot/execute"), Authorize(Policy = "StepUp")] public Task<ActionResult<PurchaseOrchestrationResult>> Pilot(RunPilotRequest request, CancellationToken token) => Run(request.TaskId, new(request.ScheduledFor, true, request.ExplicitLiveConfirmation), token);
    private string PrincipalId() => User.FindFirst(AgentTrustClaimTypes.PrincipalId)?.Value ?? throw new UnauthorizedAccessException("Linked authenticated principal identifier is required.");
    private ConsumerSetupStatus BuildSetupStatus(string principal,DateTimeOffset now)
    {
        var hasAgent=_agents.FindByPrincipal(principal).Any(x=>x.IsValid(now));
        var usableMethods=_paymentMethods.FindByPrincipal(principal).Where(x=>x.IsUsable(DateOnly.FromDateTime(now.UtcDateTime))).ToList();
        var hasMethod=usableMethods.Count>0;var methodIds=usableMethods.Select(x=>x.PaymentMethodId).ToHashSet();
        var hasMandate=_mandates.FindByPrincipal(principal).Any(x=>x.IsActive(now)&&methodIds.Contains(x.PaymentMethodId)&&string.Equals(x.Merchant,_connector.MerchantId,StringComparison.OrdinalIgnoreCase));
        var steps=new[]{new ConsumerSetupStep(1,"AGENT",hasAgent,"POST /api/consumer/agents"),new ConsumerSetupStep(2,"PAYMENT_METHOD",hasMethod,"POST /api/consumer/payment-methods/setup"),new ConsumerSetupStep(3,"MANDATE",hasMandate,"POST /api/consumer/mandates")};
        return new(steps.All(x=>x.Complete),steps.Where(x=>!x.Complete).ToArray(),steps);
    }
    private static string NormalizeMerchant(string value)=>value.Equals("demo-grocery",StringComparison.OrdinalIgnoreCase)?"GroceryDemo":value;
    private static bool ContainsAny(string value,params string[] phrases)=>phrases.Any(x=>value.Contains(x,StringComparison.OrdinalIgnoreCase));
    private static bool TryParseLimitChange(string value,out string kind,out decimal amount)
    {kind="";amount=0;if(!System.Text.RegularExpressions.Regex.IsMatch(value,@"\b(?:increase|raise|change|set|update)\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return false;var limit=System.Text.RegularExpressions.Regex.Match(value,@"\b(?<kind>weekly|monthly|per[- ]transaction|transaction)\b[^£\d]*(?:to\s*)?(?:£|GBP\s*)?(?<amount>\d+(?:\.\d{1,2})?)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);if(!limit.Success||!decimal.TryParse(limit.Groups["amount"].Value,System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out amount)||amount<=0)return false;kind=limit.Groups["kind"].Value.ToLowerInvariant().StartsWith("per")?"transaction":limit.Groups["kind"].Value.ToLowerInvariant();return true;}
    private bool StripeConfigured()=>string.Equals(_configuration["Payments:Provider"],"Stripe",StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(_configuration["Stripe:SecretKey"]??Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY"));
    private StripeClient StripeClient()=>new(_configuration["Stripe:SecretKey"]??Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY")??throw new InvalidOperationException("Stripe secret key missing."));
    private async Task<string> EnsureStripeCustomer(string principal,StripeClient client,CancellationToken token)
    {
        var existing=_paymentMethods.FindByPrincipal(principal).Select(x=>x.ProviderCustomerReference).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x));if(existing is not null)return existing;
        var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(principal))).ToLowerInvariant();
        var customer=await new CustomerService(client).CreateAsync(new CustomerCreateOptions{Metadata=new Dictionary<string,string>{{"principal_id",principal}}},new RequestOptions{IdempotencyKey=$"agenttrust-customer-{digest}"},token);return customer.Id;
    }
    private static DateTimeOffset NextOccurrence(TaskScheduleRequest schedule,string timezone,DateTimeOffset now){if(!schedule.Frequency.Equals("Weekly",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Only Weekly is supported.");var zone=TimeZoneInfo.FindSystemTimeZoneById(timezone);var local=TimeZoneInfo.ConvertTime(now,zone);if(!Enum.TryParse<DayOfWeek>(schedule.DayOfWeek,true,out var day)||!TimeOnly.TryParse(schedule.LocalTime,out var time))throw new ArgumentException("Invalid weekly schedule.");var days=((int)day-(int)local.DayOfWeek+7)%7;var candidate=local.Date.AddDays(days).Add(time.ToTimeSpan());if(candidate<=local.DateTime)candidate=candidate.AddDays(7);return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate,DateTimeKind.Unspecified),zone);}
    private sealed class RejectRawCardTokenizationProvider : ICardTokenizationProvider
    { public TokenizationResult Tokenize(string cardNumber, string cvv, int expiryMonth, int expiryYear) => throw new NotSupportedException("Raw card data is not accepted by this endpoint."); }
}

public sealed record CreateConsumerTaskRequest(string Instruction,string MerchantId,string MandateId,string PaymentMethodId,string Currency,decimal MaximumAmount,string Timezone,TaskScheduleRequest Schedule,IReadOnlyList<TaskShoppingItemRequest> ShoppingList,TaskSubstitutionRequest SubstitutionPolicy,string? DeliveryAddressReference=null);
public sealed record TaskScheduleRequest(string Frequency,string DayOfWeek,string LocalTime);
public sealed record TaskShoppingItemRequest(string Query,int Quantity,string? PreferredProductId=null,decimal? MaximumUnitPrice=null,bool RequiredForOutcome=true);
public sealed record TaskSubstitutionRequest(bool Allowed,decimal MaximumAdditionalAmount=0);
public sealed record RunPurchaseRequest(DateTimeOffset? ScheduledFor=null, bool LiveMode = false, bool ExplicitLiveConfirmation = false);
public sealed record RunPilotRequest(string TaskId, DateTimeOffset ScheduledFor, bool ExplicitLiveConfirmation);
public sealed record ProviderPaymentMethodRequest(string Provider, string ProviderToken, string CardBrand,
    string Last4, int ExpiryMonth, int ExpiryYear);
public sealed record CreateMandateRequest(string AgentId,IReadOnlyList<string> MerchantIds,string PaymentMethodId,string Currency,
    decimal PerTransactionLimit,decimal? WeeklyLimit,decimal? HumanApprovalThreshold,DateTimeOffset ValidFrom,DateTimeOffset ValidUntil);
public sealed record CreateConsumerAgentRequest(string AgentId,string? DisplayName=null);
public sealed record ConsumerSetupStep(int Order,string Resource,bool Complete,string Endpoint);
public sealed record ConsumerSetupStatus(bool IsReady,IReadOnlyList<ConsumerSetupStep> RequiredSetupSteps,IReadOnlyList<ConsumerSetupStep> AllSteps);
public sealed record ConsumerMemoryCorrectionRequest(string Message);
public sealed record MandateLimitChangeRequest(decimal? PerTransactionLimit=null,decimal? WeeklyLimit=null,decimal? MonthlyLimit=null);
