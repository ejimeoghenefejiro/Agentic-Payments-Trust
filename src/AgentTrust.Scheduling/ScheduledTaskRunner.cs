using AgentTrust.Mandates;
using AgentTrust.Tasks;

namespace AgentTrust.Scheduling;

/// <summary>Requests the provider's current price at execution time.</summary>
public interface IPriceQuoteProvider
{
    decimal GetQuote(string merchant, IReadOnlyDictionary<string, string> context);
}

/// <summary>Checks every active task's schedule and, when due, requests a live quote and
/// executes it through the TaskExecutionOrchestrator.</summary>
public sealed class ScheduledTaskRunner
{
    private readonly ITaskStore _tasks;
    private readonly IScheduleStore _schedules;
    private readonly IMandateStore _mandates;
    private readonly TaskExecutionOrchestrator _orchestrator;
    private readonly IPriceQuoteProvider _quoteProvider;
    private readonly IScheduledOccurrenceStore _occurrences;

    public ScheduledTaskRunner(ITaskStore tasks, IScheduleStore schedules, IMandateStore mandates,
        TaskExecutionOrchestrator orchestrator, IPriceQuoteProvider quoteProvider,
        IScheduledOccurrenceStore? occurrences = null)
    {
        _tasks = tasks;
        _schedules = schedules;
        _mandates = mandates;
        _orchestrator = orchestrator;
        _quoteProvider = quoteProvider;
        _occurrences = occurrences ?? new InMemoryScheduledOccurrenceStore();
    }

    public IReadOnlyList<TaskExecutionResult> RunDueTasks(string agentId, DateTimeOffset now)
    {
        var results = new List<TaskExecutionResult>();
        foreach (var task in _tasks.FindActiveByAgent(agentId))
        {
            var schedule = _schedules.Find(task.TaskId);
            if (schedule is null || !schedule.IsDue(now))
            {
                continue;
            }

            var mandate = _mandates.Find(task.MandateId);
            if (mandate is null)
            {
                continue;
            }

            if (!_occurrences.TryClaim(task.TaskId, schedule.ScheduledOccurrence(now), now, out var occurrence))
                continue;

            var quote = _quoteProvider.GetQuote(mandate.Merchant, task.Parameters);
            var result = _orchestrator.Execute(task, quote, task.Parameters, now);
            _occurrences.Complete(occurrence!.OccurrenceId, result.PaymentStatus == AgentTrust.Core.Models.PaymentStatus.Success);
            results.Add(result);
        }
        return results;
    }
}
