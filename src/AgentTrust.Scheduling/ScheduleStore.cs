namespace AgentTrust.Scheduling;

public interface IScheduleStore
{
    void Attach(string taskId, RecurringSchedule schedule);
    RecurringSchedule? Find(string taskId);
}

public sealed class InMemoryScheduleStore : IScheduleStore
{
    private readonly Dictionary<string, RecurringSchedule> _schedules = new();
    public void Attach(string taskId, RecurringSchedule schedule) => _schedules[taskId] = schedule;
    public RecurringSchedule? Find(string taskId) => _schedules.GetValueOrDefault(taskId);
}
