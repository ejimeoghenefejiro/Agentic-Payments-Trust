using System.Diagnostics;
using AgentTrust.Core.Models;

namespace AgentTrust.Runner.Experiments;

/// <summary>
/// Configurations used in the thesis ablation. The deterministic engine is deliberately retained
/// as B0: later intelligence configurations augment the experiment; they do not replace the
/// authoritative trust layer.
/// </summary>
public enum ResearchConfiguration
{
    B0DeterministicTrust,
    B1DeterministicAnalytics,
    B2Level3AgenticInvestigation,
    B3Level3WithSemanticMemory,
    B4Level3WithCalibratedMl
}

public sealed record ResearchProtocol(
    string StudyId,
    string DatasetId,
    string DatasetVersion,
    int Seed,
    string PolicyVersion,
    DateTimeOffset StartedAtUtc,
    string? ModelId = null,
    string? ModelVersion = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StudyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(DatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(DatasetVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(PolicyVersion);
        if (StartedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("StartedAtUtc must be expressed in UTC.", nameof(StartedAtUtc));
    }
}

/// <summary>Ground truth and reference evidence are fixed before any system is evaluated.</summary>
public sealed record ResearchCase(
    string CaseId,
    Decision ExpectedDecision,
    IReadOnlySet<string> ReferenceEvidenceIds,
    object Input)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(CaseId);
        ArgumentNullException.ThrowIfNull(ReferenceEvidenceIds);
        ArgumentNullException.ThrowIfNull(Input);
    }
}

/// <summary>
/// Intelligence output only. It is a recommendation and contains no payment capability.
/// PaymentExecuted is observed after the external deterministic trust layer, allowing the study
/// to test that model variability never changes the authority boundary.
/// </summary>
public sealed record ResearchObservation(
    Decision Recommendation,
    double UnsafeProbability,
    IReadOnlySet<string> EvidenceIds,
    IReadOnlyList<string> ToolsUsed,
    int HypothesesFormed,
    int HypothesesWithCounterEvidence,
    bool StopCriterionSatisfied,
    bool PaymentExecuted = false)
{
    public void Validate()
    {
        if (!double.IsFinite(UnsafeProbability) || UnsafeProbability is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(UnsafeProbability), "Probability must be finite and between 0 and 1.");
        ArgumentNullException.ThrowIfNull(EvidenceIds);
        ArgumentNullException.ThrowIfNull(ToolsUsed);
        if (HypothesesFormed < 0 || HypothesesWithCounterEvidence < 0 || HypothesesWithCounterEvidence > HypothesesFormed)
            throw new ArgumentOutOfRangeException(nameof(HypothesesFormed), "Hypothesis counts are inconsistent.");
    }
}

public interface IResearchIntelligenceSystem
{
    string SystemId { get; }
    string Version { get; }
    ResearchConfiguration Configuration { get; }
    Task<ResearchObservation> EvaluateAsync(ResearchCase researchCase, CancellationToken cancellationToken);
}

/// <summary>Small adapter that makes existing deterministic, agentic, and future ML pipelines
/// first-class experimental subjects without coupling the runner to a particular implementation.</summary>
public sealed class ResearchSystemAdapter : IResearchIntelligenceSystem
{
    private readonly Func<ResearchCase, CancellationToken, Task<ResearchObservation>> _evaluate;

    public ResearchSystemAdapter(
        string systemId,
        string version,
        ResearchConfiguration configuration,
        Func<ResearchCase, CancellationToken, Task<ResearchObservation>> evaluate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(evaluate);
        SystemId = systemId;
        Version = version;
        Configuration = configuration;
        _evaluate = evaluate;
    }

    public string SystemId { get; }
    public string Version { get; }
    public ResearchConfiguration Configuration { get; }

    public Task<ResearchObservation> EvaluateAsync(ResearchCase researchCase, CancellationToken cancellationToken) =>
        _evaluate(researchCase, cancellationToken);
}

public sealed record ResearchTrialResult(
    string CaseId,
    string SystemId,
    string SystemVersion,
    ResearchConfiguration Configuration,
    Decision ExpectedDecision,
    Decision Recommendation,
    double UnsafeProbability,
    IReadOnlySet<string> ReferenceEvidenceIds,
    IReadOnlySet<string> EvidenceIds,
    IReadOnlyList<string> ToolsUsed,
    int HypothesesFormed,
    int HypothesesWithCounterEvidence,
    bool StopCriterionSatisfied,
    bool PaymentExecuted,
    double WallLatencyMs)
{
    public bool Correct => ExpectedDecision == Recommendation;
    public bool ExpectedUnsafe => ExpectedDecision != Decision.Approve;
    public bool PredictedUnsafe => Recommendation != Decision.Approve;
}

public sealed record ConfidenceInterval(double Lower, double Upper, double ConfidenceLevel);

public sealed record SystemResearchMetrics(
    string SystemId,
    string SystemVersion,
    ResearchConfiguration Configuration,
    int CaseCount,
    double DecisionAccuracy,
    ConfidenceInterval Accuracy95Ci,
    double UnsafePrecision,
    double UnsafeRecall,
    double UnsafeF1,
    double BrierScore,
    double ExpectedCalibrationError,
    double EvidencePrecision,
    double EvidenceRecall,
    double EvidenceF1,
    double CounterEvidenceRate,
    double StopCriterionRate,
    double MeanToolCalls,
    double MedianLatencyMs,
    double P95LatencyMs,
    int UnauthorizedExecutions);

public sealed record PairedComparison(
    string BaselineSystemId,
    string ComparatorSystemId,
    int BaselineOnlyCorrect,
    int ComparatorOnlyCorrect,
    double AccuracyDifference,
    double McNemarExactPValue);

public sealed record ComparativeResearchReport(
    ResearchProtocol Protocol,
    IReadOnlyList<ResearchTrialResult> Trials,
    IReadOnlyList<SystemResearchMetrics> Systems,
    IReadOnlyList<PairedComparison> Comparisons);

/// <summary>
/// Runs every system over the same ordered cases and calculates paired, reproducible results.
/// It refuses studies without B0, preventing the deterministic baseline from quietly disappearing
/// as more sophisticated agentic or learned configurations are added.
/// </summary>
public static class ComparativeResearchEvaluator
{
    public static async Task<ComparativeResearchReport> RunAsync(
        ResearchProtocol protocol,
        IReadOnlyList<ResearchCase> cases,
        IReadOnlyList<IResearchIntelligenceSystem> systems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(systems);
        protocol.Validate();
        if (cases.Count == 0) throw new ArgumentException("At least one research case is required.", nameof(cases));
        if (systems.Count < 2) throw new ArgumentException("A comparative study requires at least two systems.", nameof(systems));
        var baselineCount = systems.Count(s => s.Configuration == ResearchConfiguration.B0DeterministicTrust);
        if (baselineCount != 1)
            throw new ArgumentException("Exactly one B0 deterministic trust baseline is mandatory.", nameof(systems));
        if (cases.Select(c => c.CaseId).Distinct(StringComparer.Ordinal).Count() != cases.Count)
            throw new ArgumentException("Research case identifiers must be unique.", nameof(cases));
        if (systems.Select(s => s.SystemId).Distinct(StringComparer.Ordinal).Count() != systems.Count)
            throw new ArgumentException("Research system identifiers must be unique.", nameof(systems));
        foreach (var researchCase in cases) researchCase.Validate();

        var trials = new List<ResearchTrialResult>(cases.Count * systems.Count);
        // Sequential by design: it avoids resource contention changing latency comparisons and
        // keeps invocation ordering stable for reproducibility.
        foreach (var system in systems)
        foreach (var researchCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            var observation = await system.EvaluateAsync(researchCase, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            observation.Validate();
            trials.Add(new ResearchTrialResult(
                researchCase.CaseId, system.SystemId, system.Version, system.Configuration,
                researchCase.ExpectedDecision, observation.Recommendation, observation.UnsafeProbability,
                researchCase.ReferenceEvidenceIds, observation.EvidenceIds, observation.ToolsUsed,
                observation.HypothesesFormed, observation.HypothesesWithCounterEvidence,
                observation.StopCriterionSatisfied, observation.PaymentExecuted, stopwatch.Elapsed.TotalMilliseconds));
        }

        var metrics = systems.Select(s => CalculateSystemMetrics(
            s, trials.Where(t => t.SystemId == s.SystemId).ToList())).ToList();
        var baseline = systems.Single(s => s.Configuration == ResearchConfiguration.B0DeterministicTrust);
        var comparisons = systems.Where(s => s.SystemId != baseline.SystemId)
            .Select(s => Compare(baseline.SystemId, s.SystemId, trials)).ToList();
        return new ComparativeResearchReport(protocol, trials, metrics, comparisons);
    }

    private static SystemResearchMetrics CalculateSystemMetrics(
        IResearchIntelligenceSystem system, IReadOnlyList<ResearchTrialResult> trials)
    {
        var n = trials.Count;
        var correct = trials.Count(t => t.Correct);
        var tp = trials.Count(t => t.ExpectedUnsafe && t.PredictedUnsafe);
        var fp = trials.Count(t => !t.ExpectedUnsafe && t.PredictedUnsafe);
        var fn = trials.Count(t => t.ExpectedUnsafe && !t.PredictedUnsafe);
        var precision = Divide(tp, tp + fp);
        var recall = Divide(tp, tp + fn);
        var evidenceTp = trials.Sum(t => t.EvidenceIds.Intersect(t.ReferenceEvidenceIds).Count());
        var evidencePredicted = trials.Sum(t => t.EvidenceIds.Count);
        var evidenceExpected = trials.Sum(t => t.ReferenceEvidenceIds.Count);
        var evidencePrecision = Divide(evidenceTp, evidencePredicted);
        var evidenceRecall = Divide(evidenceTp, evidenceExpected);
        var latencies = trials.Select(t => t.WallLatencyMs).OrderBy(x => x).ToList();

        return new SystemResearchMetrics(
            system.SystemId, system.Version, system.Configuration, n, Divide(correct, n),
            Wilson(correct, n), precision, recall, F1(precision, recall),
            trials.Average(t => Math.Pow(t.UnsafeProbability - (t.ExpectedUnsafe ? 1 : 0), 2)),
            ExpectedCalibrationError(trials, 10), evidencePrecision, evidenceRecall,
            F1(evidencePrecision, evidenceRecall),
            Divide(trials.Count(t => t.HypothesesWithCounterEvidence > 0), n),
            Divide(trials.Count(t => t.StopCriterionSatisfied), n),
            trials.Average(t => t.ToolsUsed.Count), Percentile(latencies, .5), Percentile(latencies, .95),
            trials.Count(t => t.ExpectedUnsafe && t.PaymentExecuted));
    }

    private static PairedComparison Compare(string baselineId, string comparatorId, IReadOnlyList<ResearchTrialResult> trials)
    {
        var baseline = trials.Where(t => t.SystemId == baselineId).ToDictionary(t => t.CaseId);
        var comparator = trials.Where(t => t.SystemId == comparatorId).ToDictionary(t => t.CaseId);
        if (!baseline.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(comparator.Keys))
            throw new InvalidOperationException("Paired systems did not evaluate identical case sets.");
        var baselineOnly = baseline.Keys.Count(id => baseline[id].Correct && !comparator[id].Correct);
        var comparatorOnly = baseline.Keys.Count(id => !baseline[id].Correct && comparator[id].Correct);
        return new PairedComparison(baselineId, comparatorId, baselineOnly, comparatorOnly,
            comparator.Values.Count(t => t.Correct) / (double)comparator.Count - baseline.Values.Count(t => t.Correct) / (double)baseline.Count,
            ExactTwoSidedBinomialP(baselineOnly, comparatorOnly));
    }

    private static double ExpectedCalibrationError(IReadOnlyList<ResearchTrialResult> trials, int bins)
    {
        var total = trials.Count;
        var ece = 0d;
        for (var bin = 0; bin < bins; bin++)
        {
            var lower = bin / (double)bins;
            var upper = (bin + 1) / (double)bins;
            var bucket = trials.Where(t => t.UnsafeProbability >= lower && (bin == bins - 1 ? t.UnsafeProbability <= upper : t.UnsafeProbability < upper)).ToList();
            if (bucket.Count == 0) continue;
            var confidence = bucket.Average(t => t.UnsafeProbability);
            var frequency = bucket.Average(t => t.ExpectedUnsafe ? 1d : 0d);
            ece += bucket.Count / (double)total * Math.Abs(confidence - frequency);
        }
        return ece;
    }

    private static ConfidenceInterval Wilson(int successes, int total)
    {
        if (total == 0) return new ConfidenceInterval(0, 0, .95);
        const double z = 1.959963984540054;
        var p = successes / (double)total;
        var denominator = 1 + z * z / total;
        var centre = (p + z * z / (2 * total)) / denominator;
        var margin = z * Math.Sqrt(p * (1 - p) / total + z * z / (4d * total * total)) / denominator;
        return new ConfidenceInterval(Math.Max(0, centre - margin), Math.Min(1, centre + margin), .95);
    }

    private static double ExactTwoSidedBinomialP(int a, int b)
    {
        var n = a + b;
        if (n == 0) return 1;
        var k = Math.Min(a, b);
        var cumulative = 0d;
        for (var i = 0; i <= k; i++) cumulative += BinomialCoefficient(n, i) * Math.Pow(.5, n);
        return Math.Min(1, 2 * cumulative);
    }

    private static double BinomialCoefficient(int n, int k)
    {
        var result = 1d;
        for (var i = 1; i <= k; i++) result *= (n - (k - i)) / (double)i;
        return result;
    }

    private static double Divide(int numerator, int denominator) => denominator == 0 ? 0 : numerator / (double)denominator;
    private static double F1(double precision, double recall) => precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0) return 0;
        var rank = fraction * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }
}
