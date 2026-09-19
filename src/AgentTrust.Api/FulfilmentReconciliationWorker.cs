using AgentTrust.Commerce;

namespace AgentTrust.Api;

public sealed class FulfilmentReconciliationWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<FulfilmentReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Operations:ReconciliationIntervalSeconds", 30), 5, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileBatch(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Fulfilment reconciliation batch failed"); }
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task ReconcileBatch(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFulfilmentStore>();
        var reconciler = scope.ServiceProvider.GetRequiredService<IFulfilmentReconciliationService>();
        foreach (var intent in store.FindReconciliationCandidates(DateTimeOffset.UtcNow, 50))
        {
            try
            {
                var result = await reconciler.ReconcileAsync(intent.FulfilmentIntentId, token);
                if (result is not null) store.SaveExecution(result, intent.FulfilmentIntentId);
                else store.MarkUnknown(intent.FulfilmentIntentId, "PROVIDER_OUTCOME_NOT_YET_AVAILABLE", DateTimeOffset.UtcNow.AddMinutes(2));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fulfilment reconciliation remains unknown for intent {IntentId} provider {ProviderId}", intent.FulfilmentIntentId, intent.ProviderId);
                store.MarkUnknown(intent.FulfilmentIntentId, "RECONCILIATION_REQUIRED", DateTimeOffset.UtcNow.AddMinutes(2));
            }
        }
    }
}
