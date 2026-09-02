using AgentTrust.Core.Models;
using AgentTrust.Runner.Experiments;

namespace AgentTrust.Tests;

public sealed class ComparativeResearchEvaluationTests
{
    private static readonly IReadOnlyList<ResearchCase> Cases =
    [
        new("safe", Decision.Approve, new HashSet<string> { "known-device" }, new object()),
        new("unsafe-a", Decision.Deny, new HashSet<string> { "new-device", "new-beneficiary" }, new object()),
        new("unsafe-b", Decision.Escalate, new HashSet<string> { "amount-anomaly" }, new object()),
        new("safe-unusual", Decision.Approve, new HashSet<string> { "travel-confirmed" }, new object())
    ];

    [Fact]
    public async Task RequiresDeterministicBaseline()
    {
        var agentic = System("agentic", ResearchConfiguration.B2Level3AgenticInvestigation, _ =>
            Observation(Decision.Approve, .1));

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            ComparativeResearchEvaluator.RunAsync(Protocol(), Cases, [agentic, agentic.WithId("agentic-2")]));

        Assert.Contains("baseline", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunsPairedAblationAndCalculatesResearchMetrics()
    {
        var baseline = System("b0", ResearchConfiguration.B0DeterministicTrust, researchCase =>
            researchCase.CaseId switch
            {
                "unsafe-a" => Observation(Decision.Deny, .8, ["new-device"]),
                "unsafe-b" => Observation(Decision.Approve, .3),
                _ => Observation(Decision.Approve, .1, researchCase.ReferenceEvidenceIds)
            });
        var agentic = System("b2", ResearchConfiguration.B2Level3AgenticInvestigation, researchCase =>
            researchCase.ExpectedDecision switch
            {
                Decision.Deny => Observation(Decision.Deny, .9, researchCase.ReferenceEvidenceIds, ["GetDeviceHistory", "GetBeneficiaryHistory"], 2, 1),
                Decision.Escalate => Observation(Decision.Escalate, .75, researchCase.ReferenceEvidenceIds, ["CalculateRiskSignals"], 2, 2),
                _ => Observation(Decision.Approve, .05, researchCase.ReferenceEvidenceIds, ["GetCustomerHistory"], 2, 1)
            });

        var report = await ComparativeResearchEvaluator.RunAsync(Protocol(), Cases, [baseline, agentic]);

        Assert.Equal(8, report.Trials.Count);
        var b0 = Assert.Single(report.Systems, m => m.SystemId == "b0");
        var b2 = Assert.Single(report.Systems, m => m.SystemId == "b2");
        Assert.Equal(.75, b0.DecisionAccuracy, 3);
        Assert.Equal(1, b2.DecisionAccuracy, 3);
        Assert.Equal(1, b2.EvidencePrecision, 3);
        Assert.Equal(1, b2.EvidenceRecall, 3);
        Assert.Equal(1, b2.CounterEvidenceRate, 3);
        Assert.Equal(0, b2.UnauthorizedExecutions);
        Assert.InRange(b2.BrierScore, 0, 1);
        Assert.InRange(b2.Accuracy95Ci.Lower, 0, 1);
        Assert.InRange(b2.Accuracy95Ci.Upper, 0, 1);

        var comparison = Assert.Single(report.Comparisons);
        Assert.Equal(0, comparison.BaselineOnlyCorrect);
        Assert.Equal(1, comparison.ComparatorOnlyCorrect);
        Assert.Equal(.25, comparison.AccuracyDifference, 3);
        Assert.InRange(comparison.McNemarExactPValue, 0, 1);
    }

    [Fact]
    public async Task RejectsInvalidProbabilitiesAndDuplicateCaseIds()
    {
        var baseline = System("b0", ResearchConfiguration.B0DeterministicTrust, _ => Observation(Decision.Approve, .1));
        var invalid = System("b2", ResearchConfiguration.B2Level3AgenticInvestigation, _ => Observation(Decision.Approve, 1.1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ComparativeResearchEvaluator.RunAsync(Protocol(), Cases, [baseline, invalid]));

        var duplicateCases = new[] { Cases[0], Cases[0] };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ComparativeResearchEvaluator.RunAsync(Protocol(), duplicateCases, [baseline, invalid]));
    }

    [Fact]
    public async Task WritesMachineReadableReproducibilityArtefacts()
    {
        var baseline = System("b0", ResearchConfiguration.B0DeterministicTrust, researchCase =>
            Observation(researchCase.ExpectedDecision, researchCase.ExpectedDecision == Decision.Approve ? .1 : .9,
                researchCase.ReferenceEvidenceIds));
        var comparator = System("b2", ResearchConfiguration.B2Level3AgenticInvestigation, researchCase =>
            Observation(researchCase.ExpectedDecision, researchCase.ExpectedDecision == Decision.Approve ? .1 : .9,
                researchCase.ReferenceEvidenceIds));
        var report = await ComparativeResearchEvaluator.RunAsync(Protocol(), Cases, [baseline, comparator]);
        var directory = Path.Combine(Path.GetTempPath(), $"agenttrust-research-{Guid.NewGuid():N}");

        try
        {
            ExperimentReportWriter.WriteComparative(directory, report);
            Assert.True(File.Exists(Path.Combine(directory, "comparative_report.json")));
            var csv = await File.ReadAllTextAsync(Path.Combine(directory, "comparative_trials.csv"));
            Assert.Contains("case_id,system_id", csv);
            Assert.Contains("part4", await File.ReadAllTextAsync(Path.Combine(directory, "comparative_report.json")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static ResearchProtocol Protocol() =>
        new("part4-ablation", "seeded-scenarios", "1.0", 42, "trust-policy-v1", DateTimeOffset.UtcNow);

    private static ResearchSystemAdapter System(
        string id,
        ResearchConfiguration configuration,
        Func<ResearchCase, ResearchObservation> evaluate) =>
        new(id, "1.0", configuration, (researchCase, _) => Task.FromResult(evaluate(researchCase)));

    private static ResearchObservation Observation(
        Decision decision,
        double probability,
        IEnumerable<string>? evidence = null,
        IReadOnlyList<string>? tools = null,
        int hypotheses = 0,
        int counter = 0) =>
        new(decision, probability, (evidence ?? []).ToHashSet(), tools ?? [], hypotheses, counter, true);
}

internal static class ResearchSystemTestExtensions
{
    public static ResearchSystemAdapter WithId(this ResearchSystemAdapter source, string id) =>
        new(id, source.Version, source.Configuration,
            (researchCase, cancellationToken) => source.EvaluateAsync(researchCase, cancellationToken));
}
