namespace AgentTrust.Intelligence.Investigation;

public interface ITextEmbeddingService
{
    string Provider { get; }
    string Model { get; }
    string? ModelVersion { get; }
    int Dimensions { get; }
    ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record EmbeddingProvenance(
    string Provider,
    string Model,
    string? ModelVersion,
    int Dimensions,
    DateTimeOffset CreatedAt);

public sealed record SemanticCaseRecord(
    HistoricalCaseMemory Case,
    IReadOnlyList<float> Embedding,
    EmbeddingProvenance? Provenance = null);

public interface ISemanticCaseStore
{
    void Upsert(SemanticCaseRecord record);
    IReadOnlyList<SemanticCaseRecord> GetByScope(string scopeId);
}

public sealed class InMemorySemanticCaseStore : ISemanticCaseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SemanticCaseRecord> _cases = new(StringComparer.Ordinal);

    public void Upsert(SemanticCaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate) _cases[record.Case.CaseId] = record;
    }

    public IReadOnlyList<SemanticCaseRecord> GetByScope(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        lock (_gate)
            return _cases.Values.Where(c => c.Case.ScopeId == "global" || c.Case.ScopeId == scopeId).ToList();
    }
}

/// <summary>
/// B3 semantic memory. The embedding provider is injected (hosted API, local model, or test
/// implementation); vectors and case metadata are stored separately from structured transaction
/// history. Retrieved content remains untrusted when exposed through InvestigationTools.
/// </summary>
public sealed class SemanticInvestigationMemory : IScopedInvestigationMemory
{
    private readonly ITextEmbeddingService _embeddings;
    private readonly ISemanticCaseStore _cases;
    private readonly IInvestigationMemory _nonSemanticMemory;
    private readonly double _minimumSimilarity;

    public SemanticInvestigationMemory(
        ITextEmbeddingService embeddings,
        ISemanticCaseStore cases,
        IInvestigationMemory? nonSemanticMemory = null,
        double minimumSimilarity = .2)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(cases);
        if (!double.IsFinite(minimumSimilarity) || minimumSimilarity is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumSimilarity));
        _embeddings = embeddings;
        _cases = cases;
        _nonSemanticMemory = nonSemanticMemory ?? new InMemoryInvestigationMemory();
        _minimumSimilarity = minimumSimilarity;
    }

    public async ValueTask IngestAsync(HistoricalCaseMemory historicalCase, CancellationToken cancellationToken = default)
    {
        ValidateCase(historicalCase);
        var vector = await _embeddings.EmbedAsync(CaseText(historicalCase), cancellationToken).ConfigureAwait(false);
        ValidateVector(vector.Span, _embeddings.Dimensions);
        _cases.Upsert(new SemanticCaseRecord(historicalCase, vector.ToArray(), new EmbeddingProvenance(
            _embeddings.Provider, _embeddings.Model, _embeddings.ModelVersion, _embeddings.Dimensions, DateTimeOffset.UtcNow)));
    }

    public IReadOnlyList<HistoricalCaseMemory> SearchHistoricalCases(string query) =>
        SearchHistoricalCases(query, "global");

    public IReadOnlyList<HistoricalCaseMemory> SearchHistoricalCases(string query, string scopeId, int maxResults = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        maxResults = Math.Clamp(maxResults, 1, 20);
        var queryVector = _embeddings.EmbedAsync(query).AsTask().GetAwaiter().GetResult();
        ValidateVector(queryVector.Span, _embeddings.Dimensions);

        return _cases.GetByScope(scopeId)
            .Where(record => record.Provenance is null ||
                (record.Provenance.Provider == _embeddings.Provider && record.Provenance.Model == _embeddings.Model &&
                 record.Provenance.Dimensions == _embeddings.Dimensions))
            .Select(record => (record.Case, Score: Cosine(queryVector.Span, record.Embedding)))
            .Where(match => match.Score >= _minimumSimilarity)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Case.ResolvedAt)
            .ThenBy(match => match.Case.CaseId, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(match => match.Case).ToList();
    }

    public IReadOnlyList<HumanReviewMemory> GetPreviousHumanReviews(string customerId) =>
        _nonSemanticMemory.GetPreviousHumanReviews(customerId);

    public RetrievedEvidence? RetrieveEvidence(string evidenceId) =>
        _nonSemanticMemory.RetrieveEvidence(evidenceId);

    private static string CaseText(HistoricalCaseMemory c) =>
        $"{c.Title}\n{c.Narrative}\nOutcome: {c.Outcome}\nTags: {string.Join(' ', c.Tags)}";

    private static void ValidateCase(HistoricalCaseMemory c)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentException.ThrowIfNullOrWhiteSpace(c.CaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(c.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(c.Narrative);
        ArgumentException.ThrowIfNullOrWhiteSpace(c.Outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(c.ScopeId);
    }

    private static void ValidateVector(ReadOnlySpan<float> vector, int expectedDimensions)
    {
        if (expectedDimensions <= 0 || vector.Length != expectedDimensions)
            throw new InvalidOperationException("Embedding dimensions do not match the configured provider.");
        foreach (var value in vector)
            if (!float.IsFinite(value)) throw new InvalidOperationException("Embedding contains a non-finite value.");
        if (Magnitude(vector) == 0) throw new InvalidOperationException("Embedding must not be a zero vector.");
    }

    private static double Cosine(ReadOnlySpan<float> left, IReadOnlyList<float> right)
    {
        if (left.Length != right.Count) throw new InvalidOperationException("Stored embedding dimensions are inconsistent.");
        double dot = 0, rightMagnitude = 0;
        for (var i = 0; i < left.Length; i++)
        {
            if (!float.IsFinite(right[i])) throw new InvalidOperationException("Stored embedding contains a non-finite value.");
            dot += left[i] * right[i];
            rightMagnitude += right[i] * right[i];
        }
        var denominator = Magnitude(left) * Math.Sqrt(rightMagnitude);
        return denominator == 0 ? 0 : dot / denominator;
    }

    private static double Magnitude(ReadOnlySpan<float> vector)
    {
        double sum = 0;
        foreach (var value in vector) sum += value * value;
        return Math.Sqrt(sum);
    }
}
