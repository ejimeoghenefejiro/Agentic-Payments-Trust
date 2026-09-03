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
    public ConsumerController(IConsumerTaskStore tasks, IPurchaseExecutionStore purchases,
        IPaymentMethodStore paymentMethods, AgentPurchaseOrchestrator orchestrator, DemoGroceryConnector connector,
        IMandateStore mandates, ICommerceDurability durability, IPurchaseAuditSink audit)
    { _tasks = tasks; _purchases = purchases; _paymentMethods = paymentMethods; _orchestrator = orchestrator; _connector = connector; _mandates=mandates;_durability=durability;_audit=audit; }

    [HttpPost("tasks"), Authorize(Policy = "StepUp")]
    public ActionResult<ConsumerPurchaseTask> CreateTask(CreateConsumerTaskRequest request)
    {
        var principal = PrincipalId();
        if (_mandates.Find(request.MandateId) is not { } mandate || mandate.PrincipalId != principal) return Forbid();
        if (_paymentMethods.Find(request.PaymentMethodId) is not { } method || method.PrincipalId != principal) return Forbid();
        var task = new ConsumerPurchaseTask($"ctask_{Guid.NewGuid():N}", principal, request.AgentId,
            request.MerchantScope.ToHashSet(StringComparer.OrdinalIgnoreCase), request.Schedule, request.Timezone,
            request.MaximumAmount, request.Currency, request.ShoppingList,
            new PurchasePreference(request.DeliveryAddressReference, request.RequestedDeliveryWindow,
                request.SubstitutionPolicy, request.DeliveryPreferences ?? new Dictionary<string,string>()),
            request.MandateId, request.PaymentMethodId, ConsumerTaskStatus.Active,
            request.NextExecutionAt, DateTimeOffset.UtcNow);
        _tasks.Save(task); return CreatedAtAction(nameof(GetTask), new { id = task.TaskId }, task);
    }
    [HttpGet("tasks")] public ActionResult<IReadOnlyList<ConsumerPurchaseTask>> GetTasks() => Ok(_tasks.FindByPrincipal(PrincipalId()));
    [HttpGet("tasks/{id}")] public ActionResult<ConsumerPurchaseTask> GetTask(string id) => _tasks.FindOwned(id, PrincipalId()) is { } task ? Ok(task) : NotFound();
    [HttpPost("tasks/{id}/run")]
    public async Task<ActionResult<PurchaseOrchestrationResult>> Run(string id, RunPurchaseRequest request, CancellationToken cancellationToken)
    { try { return Ok(await _orchestrator.RunAsync(id, PrincipalId(), request.ScheduledFor, _connector, new(request.LiveMode, request.ExplicitLiveConfirmation), cancellationToken)); } catch (UnauthorizedAccessException) { return Forbid(); } }
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
    [HttpGet("purchases/{id}/audit")] public ActionResult<IReadOnlyList<PurchaseAuditEvent>> PurchaseAudit(string id)
    { var purchase=_purchases.FindOwned(id,PrincipalId());return purchase is null?NotFound():Ok(_audit.Find(purchase.PurchaseIntentId)); }
    [HttpGet("receipts/{id}")] public ActionResult<PurchaseReceipt> Receipt(string id) => _durability.FindReceiptOwned(id,PrincipalId()) is { } receipt?Ok(receipt):NotFound();
    [HttpGet("mandates/{id}")] public ActionResult<FinancialMandate> Mandate(string id) => _mandates.Find(id) is { } m&&m.PrincipalId==PrincipalId()?Ok(m):NotFound();
    [HttpGet("mandates/{id}/history")] public ActionResult<IReadOnlyList<FinancialMandate>> MandateHistory(string id)
    { var history=_mandates.GetHistory(id);return history.Count==0||history.Any(x=>x.PrincipalId!=PrincipalId())?NotFound():Ok(history); }
    [HttpPost("mandates"), Authorize(Policy = "StepUp")]
    public ActionResult<FinancialMandate> CreateMandate(CreateMandateRequest request)
    {
        var principal=PrincipalId();
        if(_paymentMethods.Find(request.PaymentMethodId) is not { } payment||payment.PrincipalId!=principal)return Forbid();
        var now=DateTimeOffset.UtcNow;var mandate=new FinancialMandate($"mandate_{Guid.NewGuid():N}",principal,request.AgentId,request.Merchant,
            request.Purpose,request.PaymentMethodId,request.PerTransactionLimit,request.WeeklyLimit,request.MonthlyLimit,request.Currency,
            request.TaskParameters??new Dictionary<string,string>(),request.AboveLimit,MandateStatus.Active,now,request.ExpiresAt){DailyLimit=request.DailyLimit};
        _mandates.Save(mandate);return CreatedAtAction(nameof(Mandate),new{id=mandate.MandateId},mandate);
    }
    [HttpPost("purchases/{id}/approve"), Authorize(Policy = "StepUp")] public async Task<ActionResult<PurchaseOrchestrationResult>> Approve(string id, CancellationToken token) => Ok(await _orchestrator.ResolveAsync(id, PrincipalId(), true, PrincipalId(), token));
    [HttpPost("purchases/{id}/reject")] public async Task<ActionResult<PurchaseOrchestrationResult>> Reject(string id, CancellationToken token) => Ok(await _orchestrator.ResolveAsync(id, PrincipalId(), false, PrincipalId(), token));
    [HttpPost("pilot/execute"), Authorize(Policy = "StepUp")] public Task<ActionResult<PurchaseOrchestrationResult>> Pilot(RunPilotRequest request, CancellationToken token) => Run(request.TaskId, new(request.ScheduledFor, true, request.ExplicitLiveConfirmation), token);
    private string PrincipalId() => User.FindFirst(AgentTrustClaimTypes.PrincipalId)?.Value ?? throw new UnauthorizedAccessException("Linked authenticated principal identifier is required.");
    private sealed class RejectRawCardTokenizationProvider : ICardTokenizationProvider
    { public TokenizationResult Tokenize(string cardNumber, string cvv, int expiryMonth, int expiryYear) => throw new NotSupportedException("Raw card data is not accepted by this endpoint."); }
}

public sealed record CreateConsumerTaskRequest(string AgentId, IReadOnlyList<string> MerchantScope,
    string Schedule, string Timezone, decimal MaximumAmount, string Currency,
    IReadOnlyList<ShoppingListItem> ShoppingList, SubstitutionPolicy SubstitutionPolicy,
    string DeliveryAddressReference, string? RequestedDeliveryWindow,
    IReadOnlyDictionary<string,string>? DeliveryPreferences, string MandateId, string PaymentMethodId,
    DateTimeOffset NextExecutionAt);
public sealed record RunPurchaseRequest(DateTimeOffset ScheduledFor, bool LiveMode = false, bool ExplicitLiveConfirmation = false);
public sealed record RunPilotRequest(string TaskId, DateTimeOffset ScheduledFor, bool ExplicitLiveConfirmation);
public sealed record ProviderPaymentMethodRequest(string Provider, string ProviderToken, string CardBrand,
    string Last4, int ExpiryMonth, int ExpiryYear);
public sealed record CreateMandateRequest(string AgentId,string Merchant,string Purpose,string PaymentMethodId,
    decimal PerTransactionLimit,decimal? DailyLimit,decimal? WeeklyLimit,decimal? MonthlyLimit,string Currency,
    IReadOnlyDictionary<string,string>? TaskParameters,AboveLimitAction AboveLimit,DateTimeOffset ExpiresAt);
