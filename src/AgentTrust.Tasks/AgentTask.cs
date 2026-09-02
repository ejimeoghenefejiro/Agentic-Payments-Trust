namespace AgentTrust.Tasks;

public enum AgentTaskStatus
{
    Active,
    Paused,
    Cancelled
}

/// <summary>
/// A standing instruction an agent executes repeatedly under a mandate — the doc's "Book an
/// Uber for my girlfriend every Monday at 7:30am" example. Parameters carries the task's own
/// context (pickup/destination/recipient for a ride) generically, matched against the linked
/// mandate's TaskParameters on every run by MandateEvaluationService.
/// </summary>
public sealed record AgentTask(
    string TaskId,
    string AgentId,
    string PrincipalId,
    string MandateId,
    string TaskType,
    IReadOnlyDictionary<string, string> Parameters,
    AgentTaskStatus Status,
    DateTimeOffset CreatedAt);
