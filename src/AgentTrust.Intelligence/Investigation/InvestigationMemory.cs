namespace AgentTrust.Intelligence.Investigation;

public sealed record HumanReviewMemory(string ReviewId, string TransactionId, string CustomerId, string Outcome, string Notes, DateTimeOffset ReviewedAt);
public sealed record HistoricalCaseMemory(string CaseId, string Title, string Narrative, string Outcome, IReadOnlyList<string> Tags);
public sealed record RetrievedEvidence(string EvidenceId, string SubjectId, string Type, string Summary, string PayloadJson);

public interface IInvestigationMemory
{
    IReadOnlyList<HumanReviewMemory> GetPreviousHumanReviews(string customerId);
    IReadOnlyList<HistoricalCaseMemory> SearchHistoricalCases(string query);
    RetrievedEvidence? RetrieveEvidence(string evidenceId);
}

public sealed class InMemoryInvestigationMemory : IInvestigationMemory
{
    private readonly List<HumanReviewMemory> _reviews = new();
    private readonly List<HistoricalCaseMemory> _cases = new();
    private readonly Dictionary<string, RetrievedEvidence> _evidence = new();

    public void Add(HumanReviewMemory review) => _reviews.Add(review);
    public void Add(HistoricalCaseMemory historicalCase) => _cases.Add(historicalCase);
    public void Add(RetrievedEvidence evidence) => _evidence[evidence.EvidenceId] = evidence;

    public IReadOnlyList<HumanReviewMemory> GetPreviousHumanReviews(string customerId) =>
        _reviews.Where(r => r.CustomerId == customerId).OrderByDescending(r => r.ReviewedAt).ToList();

    public IReadOnlyList<HistoricalCaseMemory> SearchHistoricalCases(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return _cases.Where(c => terms.Any(t => c.Title.Contains(t, StringComparison.OrdinalIgnoreCase)
            || c.Narrative.Contains(t, StringComparison.OrdinalIgnoreCase)
            || c.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase)))).ToList();
    }

    public RetrievedEvidence? RetrieveEvidence(string evidenceId) => _evidence.GetValueOrDefault(evidenceId);
}
