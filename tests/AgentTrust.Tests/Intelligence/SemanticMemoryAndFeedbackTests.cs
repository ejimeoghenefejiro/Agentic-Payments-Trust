using AgentTrust.Data;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Learning;
using AgentTrust.Intelligence.Risk;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Tests.Intelligence;

public sealed class SemanticMemoryAndFeedbackTests
{
    [Fact]
    public async Task SemanticMemoryFindsConceptualMatchWithoutSharedTerms()
    {
        var memory = new SemanticInvestigationMemory(new ConceptEmbeddingService(), new InMemorySemanticCaseStore(), minimumSimilarity: .5);
        await memory.IngestAsync(new HistoricalCaseMemory("travel", "Changed telephone overseas",
            "Customer replaced mobile while on holiday.", "Legitimate", ["device-change"], "customer-1"));
        await memory.IngestAsync(new HistoricalCaseMemory("fraud", "Credential theft",
            "Attacker added a payee after taking over the account.", "Suspicious", ["compromise"], "customer-1"));

        var matches = memory.SearchHistoricalCases("new handset while travelling abroad", "customer-1");

        Assert.Equal("travel", Assert.Single(matches).CaseId);
    }

    [Fact]
    public async Task SemanticMemoryEnforcesScopeBeforeRanking()
    {
        var memory = new SemanticInvestigationMemory(new ConceptEmbeddingService(), new InMemorySemanticCaseStore(), minimumSimilarity: .5);
        await memory.IngestAsync(new HistoricalCaseMemory("allowed", "Phone abroad", "New mobile on holiday", "Legitimate", [], "customer-1"));
        await memory.IngestAsync(new HistoricalCaseMemory("private", "Phone abroad", "New mobile on holiday", "Legitimate", [], "customer-2"));

        var matches = memory.SearchHistoricalCases("handset travel", "customer-1");

        Assert.Equal("allowed", Assert.Single(matches).CaseId);
    }

    [Fact]
    public void FeedbackMustBeCuratedBeforeItBecomesEvaluationGroundTruth()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryOutcomeStore();
        store.Record(new DecisionFeedback("tx-1", IntelligenceRecommendation.Escalate, ActualOutcome.Legitimate,
            "Customer confirmed", now, "inv-1", .72, .95, ["NEW_DEVICE"], ["device-history"], [], OutcomeSource.CustomerConfirmation));

        Assert.Empty(store.GetCurated());
        Assert.Equal(0, ModelEvaluation.EvaluateCurated(store).TotalCases);

        store.SetValidation("tx-1", OutcomeValidationStatus.Validated, "senior-reviewer", now.AddMinutes(1));

        var curated = Assert.Single(store.GetCurated());
        Assert.Equal("senior-reviewer", curated.ValidatedBy);
        Assert.Equal(1, ModelEvaluation.EvaluateCurated(store).TotalCases);
        Assert.Throws<InvalidOperationException>(() => store.Record(curated));
    }

    [Fact]
    public void SemanticCasesPersistAndRemainScopeFiltered()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AgentTrustDbContext>().UseSqlite(connection).Options;
        using (var writeDb = new AgentTrustDbContext(options))
        {
            writeDb.Database.EnsureCreated();
            var store = new EfSemanticCaseStore(writeDb);
            store.Upsert(new SemanticCaseRecord(
                new HistoricalCaseMemory("case-1", "Travel", "New phone abroad", "Legitimate", ["travel"], "customer-1"),
                new[] { 1f, 0f, 0f }, new EmbeddingProvenance("Test", "ConceptEmbedding", "1", 3, DateTimeOffset.UtcNow)));
            store.Upsert(new SemanticCaseRecord(
                new HistoricalCaseMemory("case-2", "Travel", "Other customer", "Legitimate", [], "customer-2"),
                new[] { 1f, 0f, 0f }));
        }

        using var readDb = new AgentTrustDbContext(options);
        var result = new EfSemanticCaseStore(readDb).GetByScope("customer-1");
        Assert.Equal("case-1", Assert.Single(result).Case.CaseId);
        Assert.Equal(new[] { 1f, 0f, 0f }, result[0].Embedding);
        Assert.Equal("ConceptEmbedding", result[0].Provenance!.Model);
    }

    [Fact]
    public void CuratedFeedbackPersistsAcrossDbContexts()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AgentTrustDbContext>().UseSqlite(connection).Options;
        var now = DateTimeOffset.UtcNow;
        using (var writeDb = new AgentTrustDbContext(options))
        {
            writeDb.Database.EnsureCreated();
            var store = new EfOutcomeStore(writeDb);
            store.Record(new DecisionFeedback("tx-persist", IntelligenceRecommendation.Escalate, ActualOutcome.Suspicious,
                "confirmed", now, "inv-persist", .8, .9, ["NEW_DEVICE"], ["ev-1"], [], OutcomeSource.Chargeback));
            store.SetValidation("tx-persist", OutcomeValidationStatus.Validated, "validator-1", now.AddMinutes(1));
        }

        using var readDb = new AgentTrustDbContext(options);
        var feedback = Assert.Single(new EfOutcomeStore(readDb).GetCurated());
        Assert.Equal("inv-persist", feedback.InvestigationId);
        Assert.Equal(OutcomeSource.Chargeback, feedback.Source);
        Assert.Equal(["ev-1"], feedback.UsefulEvidenceIds);
        Assert.Equal("validator-1", feedback.ValidatedBy);
    }

    private sealed class ConceptEmbeddingService : ITextEmbeddingService
    {
        public string Provider => "Test";
        public string Model => "ConceptEmbedding";
        public string? ModelVersion => "1";
        public int Dimensions => 3;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var travel = ContainsAny(text, "telephone", "phone", "mobile", "handset", "holiday", "travel", "abroad", "overseas") ? 1f : 0f;
            var fraud = ContainsAny(text, "theft", "attacker", "compromise", "takeover", "payee") ? 1f : 0f;
            return ValueTask.FromResult<ReadOnlyMemory<float>>(new[] { travel, fraud, .01f });
        }

        private static bool ContainsAny(string text, params string[] terms) =>
            terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
