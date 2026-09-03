using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
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

namespace AgentTrust.Api.Controllers;

[ApiController, Route("api/consumer"), Authorize(Policy = "Consumer")]
public sealed class ConsumerController : ControllerBase
{
    private readonly IConsumerTaskStore _tasks; private readonly IPurchaseExecutionStore _purchases;
    private readonly IPaymentMethodStore _paymentMethods; private readonly AgentPurchaseOrchestrator _orchestrator;
    private readonly DemoGroceryConnector _connector;
    private readonly IMandateStore _mandates; private readonly ICommerceDurability _durability; private readonly IPurchaseAuditSink _audit;
    private readonly IScheduledOccurrenceStore _occurrences;
    private readonly IAgentRegistry _agents;private readonly IPrincipalBindingStore _bindings;private readonly IPrincipalStore _principals;
    public ConsumerController(IConsumerTaskStore tasks, IPurchaseExecutionStore purchases,
        IPaymentMethodStore paymentMethods, AgentPurchaseOrchestrator orchestrator, DemoGroceryConnector connector,
        IMandateStore mandates, ICommerceDurability durability, IPurchaseAuditSink audit,IScheduledOccurrenceStore occurrences,
        IAgentRegistry agents,IPrincipalBindingStore bindings,IPrincipalStore principals)
    { _tasks = tasks; _purchases = purchases; _paymentMethods = paymentMethods; _orchestrator = orchestrator; _connector = connector; _mandates=mandates;_durability=durability;_audit=audit;_occurrences=occurrences;_agents=agents;_bindings=bindings;_principals=principals; }

    [HttpPost("agents"),Authorize(Policy="StepUp")]
    public ActionResult<AgentIdentity> CreateAgent(CreateConsumerAgentRequest request)
    {
        var principal=PrincipalId();if(_agents.Find(request.AgentId)is not null)return Conflict("Agent already exists.");
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
            request.MaximumAmount, request.Currency, request.ShoppingList.Select(x=>new ShoppingListItem(x.Query,x.Quantity,x.PreferredProductId,x.MaximumUnitPrice)).ToList(),
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
    [HttpGet("payment-methods")] public ActionResult<IReadOnlyList<PaymentMethod>> PaymentMethods() => Ok(_paymentMethods.FindByPrincipal(PrincipalId()));
    [HttpPost("payment-methods/setup"), Authorize(Policy = "StepUp")]
    public ActionResult<PaymentMethod> SetupPaymentMethod(ProviderPaymentMethodRequest request)
    {
        var service = new PaymentMethodService(new RejectRawCardTokenizationProvider(), _paymentMethods);
        return Ok(service.ConnectProviderToken(PrincipalId(), request.Provider, request.ProviderToken,
            request.CardBrand, request.Last4, request.ExpiryMonth, request.ExpiryYear));
    }
    [HttpGet("purchases")] public ActionResult<IReadOnlyList<PurchaseExecution>> Purchases() => Ok(_purchases.FindByPrincipal(PrincipalId()));
    [HttpGet("purchases/{id}")] public ActionResult<PurchaseExecution> Purchase(string id) => _purchases.FindOwned(id, PrincipalId()) is { } item ? Ok(item) : NotFound();
    [HttpGet("purchases/{id}/audit")] public ActionResult<object> PurchaseAudit(string id)
    { var purchase=_purchases.FindOwned(id,PrincipalId());if(purchase is null)return NotFound();var events=_audit.Find(purchase.PurchaseIntentId);var valid=events.Count>0;for(var i=0;i<events.Count;i++){var expectedPrevious=i==0?events[i].PreviousHash:events[i-1].CurrentHash;valid&=events[i].PreviousHash==expectedPrevious&&events[i].CurrentHash==PurchaseAuditHash.Compute(events[i],events[i].PreviousHash);}return Ok(new{isValid=valid,eventCount=events.Count,events}); }
    [HttpGet("purchases/{id}/receipt")] public ActionResult<object> Receipt(string id){var principal=PrincipalId();var purchase=_purchases.FindOwned(id,principal);if(purchase is null)return NotFound();var receipt=_durability.FindReceiptByPurchaseOwned(purchase.PurchaseIntentId,principal);var intent=_durability.FindIntentOwned(purchase.PurchaseIntentId,principal);if(receipt is null||intent is null)return NotFound();var mandate=_mandates.Find(intent.MandateId);return Ok(new{receiptId=receipt.ReceiptId,purchaseId=id,merchant=intent.MerchantName,items=intent.BasketItems,subtotal=intent.Subtotal,deliveryFee=intent.DeliveryFee,total=receipt.TotalAmount,currency=receipt.Currency,paymentIntentId=receipt.ProviderReference,purchasedAt=receipt.PurchasedAt,taskId=intent.TaskId,mandateId=intent.MandateId,mandateVersion=mandate?.Version});}
    [HttpGet("mandates")] public ActionResult<IReadOnlyList<FinancialMandate>> Mandates()=>Ok(_mandates.FindByPrincipal(PrincipalId()));
    [HttpGet("mandates/{id}")] public ActionResult<FinancialMandate> Mandate(string id) => _mandates.Find(id) is { } m&&m.PrincipalId==PrincipalId()?Ok(m):NotFound();
    [HttpGet("mandates/{id}/history")] public ActionResult<IReadOnlyList<FinancialMandate>> MandateHistory(string id)
    { var history=_mandates.GetHistory(id);return history.Count==0||history.Any(x=>x.PrincipalId!=PrincipalId())?NotFound():Ok(history); }
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
    private static string NormalizeMerchant(string value)=>value.Equals("demo-grocery",StringComparison.OrdinalIgnoreCase)?"GroceryDemo":value;
    private static DateTimeOffset NextOccurrence(TaskScheduleRequest schedule,string timezone,DateTimeOffset now){if(!schedule.Frequency.Equals("Weekly",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Only Weekly is supported.");var zone=TimeZoneInfo.FindSystemTimeZoneById(timezone);var local=TimeZoneInfo.ConvertTime(now,zone);if(!Enum.TryParse<DayOfWeek>(schedule.DayOfWeek,true,out var day)||!TimeOnly.TryParse(schedule.LocalTime,out var time))throw new ArgumentException("Invalid weekly schedule.");var days=((int)day-(int)local.DayOfWeek+7)%7;var candidate=local.Date.AddDays(days).Add(time.ToTimeSpan());if(candidate<=local.DateTime)candidate=candidate.AddDays(7);return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate,DateTimeKind.Unspecified),zone);}
    private sealed class RejectRawCardTokenizationProvider : ICardTokenizationProvider
    { public TokenizationResult Tokenize(string cardNumber, string cvv, int expiryMonth, int expiryYear) => throw new NotSupportedException("Raw card data is not accepted by this endpoint."); }
}

public sealed record CreateConsumerTaskRequest(string Instruction,string MerchantId,string MandateId,string PaymentMethodId,string Currency,decimal MaximumAmount,string Timezone,TaskScheduleRequest Schedule,IReadOnlyList<TaskShoppingItemRequest> ShoppingList,TaskSubstitutionRequest SubstitutionPolicy,string? DeliveryAddressReference=null);
public sealed record TaskScheduleRequest(string Frequency,string DayOfWeek,string LocalTime);
public sealed record TaskShoppingItemRequest(string Query,int Quantity,string? PreferredProductId=null,decimal? MaximumUnitPrice=null);
public sealed record TaskSubstitutionRequest(bool Allowed,decimal MaximumAdditionalAmount=0);
public sealed record RunPurchaseRequest(DateTimeOffset? ScheduledFor=null, bool LiveMode = false, bool ExplicitLiveConfirmation = false);
public sealed record RunPilotRequest(string TaskId, DateTimeOffset ScheduledFor, bool ExplicitLiveConfirmation);
public sealed record ProviderPaymentMethodRequest(string Provider, string ProviderToken, string CardBrand,
    string Last4, int ExpiryMonth, int ExpiryYear);
public sealed record CreateMandateRequest(string AgentId,IReadOnlyList<string> MerchantIds,string PaymentMethodId,string Currency,
    decimal PerTransactionLimit,decimal? WeeklyLimit,decimal? HumanApprovalThreshold,DateTimeOffset ValidFrom,DateTimeOffset ValidUntil);
public sealed record CreateConsumerAgentRequest(string AgentId,string? DisplayName=null);
