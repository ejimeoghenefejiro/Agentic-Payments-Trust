namespace AgentTrust.Intelligence.Learning;

public interface IOutcomeStore
{
    void Record(DecisionFeedback feedback);
    IReadOnlyList<DecisionFeedback> GetAll();
}

public sealed class InMemoryOutcomeStore : IOutcomeStore
{
    private readonly List<DecisionFeedback> _feedback = new();
    public void Record(DecisionFeedback feedback) => _feedback.Add(feedback);
    public IReadOnlyList<DecisionFeedback> GetAll() => _feedback.ToList();
}
