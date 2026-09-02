using System.Text.Json;
using AgentTrust.Intelligence.Investigation;

namespace AgentTrust.Runner.Experiments;

public sealed record SemanticCorpusCase(
    string QueryId,
    string ScopeId,
    string Query,
    IReadOnlyList<string> RelevantCaseIds,
    int K = 5);

public sealed record SemanticExperimentCorpus(
    string DatasetId,
    string Version,
    IReadOnlyList<HistoricalCaseMemory> MemoryCases,
    IReadOnlyList<SemanticCorpusCase> Queries)
{
    public static SemanticExperimentCorpus Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var corpus = JsonSerializer.Deserialize<SemanticExperimentCorpus>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Semantic corpus is empty or invalid.");
        corpus.Validate();
        return corpus;
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        if (MemoryCases.Count == 0 || Queries.Count == 0) throw new InvalidDataException("Corpus requires memory cases and queries.");
        if (MemoryCases.Select(c => c.CaseId).Distinct(StringComparer.Ordinal).Count() != MemoryCases.Count)
            throw new InvalidDataException("Memory case IDs must be unique.");
        if (Queries.Select(q => q.QueryId).Distinct(StringComparer.Ordinal).Count() != Queries.Count)
            throw new InvalidDataException("Query IDs must be unique.");
        var caseIds = MemoryCases.Select(c => c.CaseId).ToHashSet(StringComparer.Ordinal);
        foreach (var query in Queries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(query.ScopeId);
            ArgumentException.ThrowIfNullOrWhiteSpace(query.Query);
            if (query.K is < 1 or > 20) throw new InvalidDataException("K must be between 1 and 20.");
            if (query.RelevantCaseIds.Count == 0 || query.RelevantCaseIds.Any(id => !caseIds.Contains(id)))
                throw new InvalidDataException($"Query '{query.QueryId}' has missing or unknown relevance labels.");
            if (query.RelevantCaseIds.Any(id =>
                MemoryCases.Single(c => c.CaseId == id).ScopeId is var scope && scope != "global" && scope != query.ScopeId))
                throw new InvalidDataException($"Query '{query.QueryId}' labels a cross-scope case as relevant.");
        }
    }
}
