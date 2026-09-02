namespace AgentTrust.Scheduling;

public enum ScheduledOccurrenceStatus { Claimed, Completed, Failed }
public sealed record ScheduledOccurrence(string OccurrenceId, string TaskId, DateTimeOffset ScheduledFor,
    ScheduledOccurrenceStatus Status, DateTimeOffset ClaimedAt);

public interface IScheduledOccurrenceStore
{
    bool TryClaim(string taskId, DateTimeOffset scheduledFor, DateTimeOffset claimedAt, out ScheduledOccurrence? occurrence);
    void Complete(string occurrenceId, bool success);
}

/// <summary>The task/scheduled-time pair is the idempotency boundary. The lock models the unique
/// database constraint required by a durable implementation.</summary>
public sealed class InMemoryScheduledOccurrenceStore : IScheduledOccurrenceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string TaskId, DateTimeOffset ScheduledFor), ScheduledOccurrence> _items = new();
    public bool TryClaim(string taskId, DateTimeOffset scheduledFor, DateTimeOffset claimedAt, out ScheduledOccurrence? occurrence)
    {
        lock (_gate)
        {
            var key = (taskId, scheduledFor);
            if (_items.ContainsKey(key)) { occurrence = null; return false; }
            occurrence = new ScheduledOccurrence($"occ_{Guid.NewGuid():N}", taskId, scheduledFor,
                ScheduledOccurrenceStatus.Claimed, claimedAt);
            _items[key] = occurrence; return true;
        }
    }
    public void Complete(string occurrenceId, bool success)
    {
        lock (_gate)
        {
            var pair = _items.FirstOrDefault(kv => kv.Value.OccurrenceId == occurrenceId);
            if (pair.Value is not null)
                _items[pair.Key] = pair.Value with { Status = success ? ScheduledOccurrenceStatus.Completed : ScheduledOccurrenceStatus.Failed };
        }
    }
}
