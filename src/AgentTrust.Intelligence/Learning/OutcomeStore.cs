namespace AgentTrust.Intelligence.Learning;

public interface IOutcomeStore
{
    void Record(DecisionFeedback feedback);
    void SetValidation(string transactionId, OutcomeValidationStatus status, string validatorId, DateTimeOffset validatedAt);
    IReadOnlyList<DecisionFeedback> GetAll();
    IReadOnlyList<DecisionFeedback> GetCurated();
}

public sealed class InMemoryOutcomeStore : IOutcomeStore
{
    private readonly object _gate = new();
    private readonly List<DecisionFeedback> _feedback = new();
    public void Record(DecisionFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        feedback.Validate();
        lock (_gate)
        {
            if (_feedback.Any(f => f.TransactionId == feedback.TransactionId))
                throw new InvalidOperationException($"Feedback already exists for transaction '{feedback.TransactionId}'.");
            _feedback.Add(feedback);
        }
    }

    public void SetValidation(string transactionId, OutcomeValidationStatus status, string validatorId, DateTimeOffset validatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(validatorId);
        if (status == OutcomeValidationStatus.Pending)
            throw new ArgumentException("A validation decision cannot restore Pending status.", nameof(status));
        lock (_gate)
        {
            var index = _feedback.FindIndex(f => f.TransactionId == transactionId);
            if (index < 0) throw new KeyNotFoundException($"No feedback exists for transaction '{transactionId}'.");
            var existing = _feedback[index];
            var updated = existing with { ValidationStatus = status, ValidatedBy = validatorId, ValidatedAt = validatedAt };
            updated.Validate();
            _feedback[index] = updated;
        }
    }

    public IReadOnlyList<DecisionFeedback> GetAll()
    {
        lock (_gate) return _feedback.ToList();
    }

    public IReadOnlyList<DecisionFeedback> GetCurated()
    {
        lock (_gate) return _feedback.Where(f => f.ValidationStatus == OutcomeValidationStatus.Validated).ToList();
    }
}
