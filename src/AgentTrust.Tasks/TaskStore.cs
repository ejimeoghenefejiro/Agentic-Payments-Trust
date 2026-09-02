namespace AgentTrust.Tasks;

public interface ITaskStore
{
    void Save(AgentTask task);
    AgentTask? Find(string taskId);
    IReadOnlyList<AgentTask> FindActiveByAgent(string agentId);
}

public sealed class InMemoryTaskStore : ITaskStore
{
    private readonly Dictionary<string, AgentTask> _tasks = new();

    public void Save(AgentTask task) => _tasks[task.TaskId] = task;

    public AgentTask? Find(string taskId) => _tasks.GetValueOrDefault(taskId);

    public IReadOnlyList<AgentTask> FindActiveByAgent(string agentId) =>
        _tasks.Values.Where(t => t.AgentId == agentId && t.Status == AgentTaskStatus.Active).ToList();
}
