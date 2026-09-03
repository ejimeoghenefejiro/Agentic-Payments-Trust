using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Consumer;
using AgentTrust.Data;
using AgentTrust.Scheduling;
using Microsoft.EntityFrameworkCore;
using Stripe;

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

        await ReconcileStripe(db, now, token);
    }

    private async Task ReconcileStripe(AgentTrustDbContext db, DateTimeOffset now, CancellationToken token)
    {
        if (!string.Equals(configuration["Payments:Provider"], "Stripe", StringComparison.OrdinalIgnoreCase)) return;
        var key = configuration["Stripe:SecretKey"] ?? Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;
        var service = new PaymentIntentService(new StripeClient(key));
        var attempts = await db.ConsumerPaymentAttempts.Where(x => x.ProviderPaymentId != null &&
            (x.LatestStatus == "Unknown" || x.LatestStatus == "Processing" || x.LatestStatus == "Submitted") && x.UpdatedAt < now.AddMinutes(-1))
            .Take(50).ToListAsync(token);
        foreach (var attempt in attempts)
        {
            try
            {
                var payment = await service.GetAsync(attempt.ProviderPaymentId!, cancellationToken: token);
                var state = payment.Status switch { "succeeded" => "Captured", "processing" => "Processing",
                    "requires_action" => "RequiresAction", "requires_payment_method" or "canceled" => "Declined", _ => "Unknown" };
                attempt.LatestStatus = state; attempt.UpdatedAt = now; attempt.Version++;
                var execution = await db.PurchaseExecutions.SingleOrDefaultAsync(x => x.ProviderPaymentId == attempt.ProviderPaymentId, token);
                if (execution is not null)
                {
                    execution.State = state switch { "Captured" => "Purchased", "Declined" => "Failed", _ => state };
                    execution.UpdatedAt = now; execution.Version++;
                }
            }
            catch (StripeException ex) { logger.LogWarning(ex, "Stripe reconciliation failed for {PaymentId}.", attempt.ProviderPaymentId); }
        }
        await db.SaveChangesAsync(token);
    }
}
