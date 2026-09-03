using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Consumer;
using AgentTrust.Data;
using AgentTrust.Scheduling;
using Microsoft.EntityFrameworkCore;
using Stripe;
using System.Text.Json;

namespace AgentTrust.Api;

/// <summary>Database-claimed scheduler plus recovery sweep. Disabled by default so API replicas
/// do not begin executing purchases until the operator explicitly enables the worker.</summary>
public sealed class ConsumerPilotWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<ConsumerPilotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("ConsumerPilot:Worker:Enabled", false)) return;
        var interval = TimeSpan.FromSeconds(Math.Max(10, configuration.GetValue("ConsumerPilot:Worker:IntervalSeconds", 30)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnce(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Consumer pilot scheduler/recovery iteration failed."); }
            await Task.Delay(interval, stoppingToken);
        }
    }

    internal async Task RunOnce(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var now = DateTimeOffset.UtcNow;
        var tasks = services.GetRequiredService<IConsumerTaskStore>();
        var occurrences = services.GetRequiredService<IScheduledOccurrenceStore>();
        var orchestrator = services.GetRequiredService<AgentPurchaseOrchestrator>();
        var connector = services.GetRequiredService<DemoGroceryConnector>();
        foreach (var task in tasks.FindDue(now))
        {
            if (!occurrences.TryClaim(task.TaskId, task.NextExecutionAt, now, out var occurrence)) continue;
            var succeeded = false;
            try
            {
                var result = await orchestrator.RunAsync(task.TaskId, task.PrincipalId, task.NextExecutionAt,
                    connector, new(false, false), token);
                succeeded = result.Execution.State is PurchaseExecutionState.Purchased or PurchaseExecutionState.AwaitingHumanApproval;
                tasks.Save(task with { NextExecutionAt = task.NextExecutionAt.AddDays(7) });
            }
            catch (Exception ex) { logger.LogError(ex, "Scheduled consumer task {TaskId} failed.", task.TaskId); }
            finally { occurrences.Complete(occurrence!.OccurrenceId, succeeded); }
        }

        // Release abandoned reservations and leases. Unknown provider outcomes remain reserved
        // until webhook/reconciliation resolves them; only genuinely expired rows are released.
        var db = services.GetRequiredService<AgentTrustDbContext>();
        await db.SpendReservations.Where(x => x.Status == "Reserved" && x.ExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Released").SetProperty(x => x.FinalisedAt, now), token);
        await db.TaskOccurrences.Where(x => x.Status == "Claimed" && x.LeaseExpiresAt < now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Failed").SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), token);

        await ReconcileStripe(services, db, now, token);
    }

    private async Task ReconcileStripe(IServiceProvider services,AgentTrustDbContext db, DateTimeOffset now, CancellationToken token)
    {
        if (!string.Equals(configuration["Payments:Provider"], "Stripe", StringComparison.OrdinalIgnoreCase)) return;
        var key = configuration["Stripe:SecretKey"] ?? Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;
        var service = new PaymentIntentService(new StripeClient(key));
        var attempts = await db.ConsumerPaymentAttempts.Where(x =>
            (x.LatestStatus == "Unknown" || x.LatestStatus == "Processing" || x.LatestStatus == "Submitted") && x.UpdatedAt < now.AddMinutes(-1))
            .Take(50).ToListAsync(token);
        foreach (var attempt in attempts)
        {
            var currentAttempt=attempt;
            try
            {
                string state;
                if(currentAttempt.ProviderPaymentId is null)
                {
                    // A timeout may happen after Stripe accepted the request but before its id was
                    // returned. Retry only an already-authorised immutable intent and reuse the
                    // original idempotency key; Stripe will return the first PaymentIntent.
                    var stored=await db.PurchaseIntents.AsNoTracking().SingleAsync(x=>x.PurchaseIntentId==currentAttempt.PurchaseIntentId,token);
                    var authorised=await db.PurchaseAuthorisations.AsNoTracking().AnyAsync(x=>x.PurchaseIntentId==stored.PurchaseIntentId&&x.Status=="Active"&&x.ExpiresAt>now&&x.IntentHash==stored.IntentHash,token);
                    if(!authorised){logger.LogWarning("Recovery skipped unauthorised or expired purchase {PurchaseIntentId}.",stored.PurchaseIntentId);continue;}
                    var intent=new PurchaseIntent(stored.PurchaseIntentId,stored.PrincipalId,stored.AgentId,stored.MandateId,stored.TaskId,stored.MerchantId,stored.MerchantName,stored.Currency,
                        JsonSerializer.Deserialize<List<BasketItem>>(stored.BasketJson)??[],stored.Subtotal,stored.DeliveryFee,stored.TotalAmount,stored.DeliveryAddressReference,stored.RequestedDeliveryWindow,
                        stored.PaymentMethodReference,stored.CreatedAt,stored.QuoteExpiresAt,stored.PaymentIdempotencyKey);
                    var durability=services.GetRequiredService<ICommerceDurability>();var processor=services.GetRequiredService<IPlatformPaymentProcessor>();
                    durability.BeginPaymentSubmission(intent,processor.ProviderName);
                    var recovered=await processor.ProcessAsync(intent,token);durability.RecordPaymentResult(intent,recovered);
                    currentAttempt=await db.ConsumerPaymentAttempts.SingleAsync(x=>x.PaymentIdempotencyKey==stored.PaymentIdempotencyKey,token);
                    state=currentAttempt.LatestStatus;
                }
                else
                {
                    var payment = await service.GetAsync(currentAttempt.ProviderPaymentId, cancellationToken: token);
                    state = payment.Status switch { "succeeded" => "Captured", "processing" => "Processing",
                        "requires_action" => "RequiresAction", "requires_payment_method" or "canceled" => "Declined", _ => "Unknown" };
                }
                currentAttempt.LatestStatus = state; currentAttempt.UpdatedAt = now; currentAttempt.Version++;
                var checkout=await db.CheckoutExecutions.SingleOrDefaultAsync(x=>x.PaymentIdempotencyKey==currentAttempt.PaymentIdempotencyKey,token);
                if(checkout is not null){checkout.Status=state switch{"Captured"=>"Succeeded","Declined"=>"Failed",_=>state};checkout.UpdatedAt=now;checkout.Version++;}
                var execution = await db.PurchaseExecutions.SingleOrDefaultAsync(x => x.PurchaseIntentId == currentAttempt.PurchaseIntentId, token);
                if (execution is not null)
                {
                    execution.State = state switch { "Captured" => "Purchased", "Declined" => "Failed", _ => state };
                    execution.ProviderPaymentId=currentAttempt.ProviderPaymentId;
                    execution.UpdatedAt = now; execution.Version++;
                    var reservation=await db.SpendReservations.SingleOrDefaultAsync(x=>x.ExecutionId==currentAttempt.PurchaseIntentId&&x.Status=="Reserved",token);
                    if(reservation is not null&&state is "Captured" or "Declined"){reservation.Status=state=="Captured"?"Committed":"Released";reservation.FinalisedAt=now;reservation.Version++;}
                    if(state=="Captured"&&!await db.PurchaseReceipts.AnyAsync(x=>x.PurchaseIntentId==currentAttempt.PurchaseIntentId,token))
                    {
                        var intent=await db.PurchaseIntents.SingleAsync(x=>x.PurchaseIntentId==currentAttempt.PurchaseIntentId,token);
                        db.PurchaseReceipts.Add(new(){ReceiptId=$"receipt_{Guid.NewGuid():N}",PurchaseIntentId=intent.PurchaseIntentId,PrincipalId=intent.PrincipalId,MerchantId=intent.MerchantId,TotalAmount=intent.TotalAmount,Currency=intent.Currency,ProviderPaymentId=currentAttempt.ProviderPaymentId!,PurchasedAt=now});
                    }
                }
            }
            catch (Exception ex) when(ex is StripeException or HttpRequestException or TaskCanceledException)
            { currentAttempt.LatestStatus="Unknown";currentAttempt.FailureCode="RECONCILIATION_RETRY_FAILED";currentAttempt.UpdatedAt=now;currentAttempt.Version++;logger.LogWarning(ex, "Stripe reconciliation failed for {PaymentId}.", currentAttempt.ProviderPaymentId); }
        }
        await db.SaveChangesAsync(token);
    }
}
