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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController, Route("api/consumer"), Authorize]
public sealed class ConsumerController : ControllerBase
{
    private readonly IConsumerTaskStore _tasks; private readonly IPurchaseExecutionStore _purchases;
    private readonly IPaymentMethodStore _paymentMethods; private readonly AgentPurchaseOrchestrator _orchestrator;
    private readonly DemoGroceryConnector _connector;
    public ConsumerController(IConsumerTaskStore tasks, IPurchaseExecutionStore purchases,
        IPaymentMethodStore paymentMethods, AgentPurchaseOrchestrator orchestrator, DemoGroceryConnector connector)
    { _tasks = tasks; _purchases = purchases; _paymentMethods = paymentMethods; _orchestrator = orchestrator; _connector = connector; }

    [HttpPost("tasks")]
    public ActionResult<ConsumerPurchaseTask> CreateTask(CreateConsumerTaskRequest request)
    {
        var principal = PrincipalId();
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
    [HttpPost("payment-methods/setup")]
    public ActionResult<PaymentMethod> SetupPaymentMethod(ProviderPaymentMethodRequest request)
    {
        var service = new PaymentMethodService(new RejectRawCardTokenizationProvider(), _paymentMethods);
        return Ok(service.ConnectProviderToken(PrincipalId(), request.Provider, request.ProviderToken,
            request.CardBrand, request.Last4, request.ExpiryMonth, request.ExpiryYear));
    }
    [HttpGet("purchases")] public ActionResult<IReadOnlyList<PurchaseExecution>> Purchases() => Ok(_purchases.FindByPrincipal(PrincipalId()));
    [HttpGet("purchases/{id}")] public ActionResult<PurchaseExecution> Purchase(string id) => _purchases.FindOwned(id, PrincipalId()) is { } item ? Ok(item) : NotFound();
    [HttpPost("purchases/{id}/approve")] public async Task<ActionResult<PurchaseOrchestrationResult>> Approve(string id, CancellationToken token) => Ok(await _orchestrator.ResolveAsync(id, PrincipalId(), true, PrincipalId(), token));
    [HttpPost("purchases/{id}/reject")] public async Task<ActionResult<PurchaseOrchestrationResult>> Reject(string id, CancellationToken token) => Ok(await _orchestrator.ResolveAsync(id, PrincipalId(), false, PrincipalId(), token));
    [HttpPost("pilot/execute")] public Task<ActionResult<PurchaseOrchestrationResult>> Pilot(RunPilotRequest request, CancellationToken token) => Run(request.TaskId, new(request.ScheduledFor, true, request.ExplicitLiveConfirmation), token);
    private string PrincipalId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new UnauthorizedAccessException("Authenticated principal identifier is required.");
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
