using AgentTrust.Consumer;
using AgentTrust.Scheduling;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentTrust.Commerce;

/// <summary>Reuses the existing taskId+scheduledFor occurrence claim as the recurring-purchase
/// idempotency boundary. Concurrent scheduler invocations can produce only one purchase run.</summary>
public sealed class ConsumerPurchaseScheduler
{
    private readonly IScheduledOccurrenceStore _occurrences;
    private readonly AgentPurchaseOrchestrator _orchestrator;
    public ConsumerPurchaseScheduler(IScheduledOccurrenceStore occurrences, AgentPurchaseOrchestrator orchestrator)
    { _occurrences = occurrences; _orchestrator = orchestrator; }
    public async Task<PurchaseOrchestrationResult?> RunOccurrenceAsync(string taskId, string principalId,
        DateTimeOffset scheduledFor, ICommerceConnector connector, LiveExecutionContext liveContext,
        CancellationToken cancellationToken = default)
    {
        if (!_occurrences.TryClaim(taskId, scheduledFor, DateTimeOffset.UtcNow, out var occurrence)) return null;
        try
        {
            var result = await _orchestrator.RunAsync(taskId, principalId, scheduledFor, connector, liveContext, cancellationToken);
            _occurrences.Complete(occurrence!.OccurrenceId, result.Execution.State == PurchaseExecutionState.Purchased);
            return result;
        }
        catch { _occurrences.Complete(occurrence!.OccurrenceId, false); throw; }
    }
}
