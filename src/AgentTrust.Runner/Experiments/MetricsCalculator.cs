using AgentTrust.Core.Models;
using AgentTrust.Evidence;

namespace AgentTrust.Runner.Experiments;

public sealed class CategoryResult
{
    public string Category { get; set; } = "";
    public int Count { get; set; }
    public double DecisionAccuracy { get; set; }
    public double ReasonCodeAccuracy { get; set; }
    public double AverageEvidenceF1 { get; set; }
    public double MedianPolicyLatencyMs { get; set; }
}

public sealed class AdversarialMetrics
{
    public int AttackScenarios { get; set; }
    /// <summary>Fraction of attack-category scenarios that were incorrectly Approved.</summary>
    public double AttackSuccessRate { get; set; }
    /// <summary>Fraction of attack-category scenarios correctly Denied or Escalated.</summary>
    public double AttackPreventionRate { get; set; }
    /// <summary>Fraction of legitimate scenarios incorrectly blocked (Denied/Escalated).</summary>
    public double FalsePositiveRate { get; set; }
    /// <summary>Fraction of attack scenarios incorrectly let through (Approved) — identical
    /// population to AttackSuccessRate, reported separately because it answers a different
    /// question: "of things that should have been caught, how many were missed."</summary>
    public double FalseNegativeRate { get; set; }
}

public sealed class AggregateMetrics
{
    public int TotalScenarios { get; set; }
    public bool AuditChainValid { get; set; }
    public IReadOnlyList<string> AuditChainBreaks { get; set; } = Array.Empty<string>();

    public double PolicyEnforcementAccuracy { get; set; }
    public double UnauthorizedTransactionPreventionRate { get; set; }
    public double AuthorizedTransactionAcceptanceRate { get; set; }
    public double RevocationEnforcementRate { get; set; }
    public double HumanEscalationAccuracy { get; set; }
    public double ReasonCodeAccuracy { get; set; }

    public double EvidencePrecision { get; set; }
    public double EvidenceRecall { get; set; }
    public double EvidenceF1 { get; set; }

    public double AuditReconstructionRate { get; set; }

    public double MedianPolicyLatencyMs { get; set; }
    public double P95PolicyLatencyMs { get; set; }
    public double P99PolicyLatencyMs { get; set; }
    public double MedianWallLatencyMs { get; set; }
    public double P95WallLatencyMs { get; set; }

    public Dictionary<string, Dictionary<string, int>> ConfusionMatrix { get; set; } = new();
    public List<CategoryResult> PerCategory { get; set; } = new();
    public AdversarialMetrics Adversarial { get; set; } = new();
}

public static class MetricsCalculator
{
    public static AggregateMetrics Compute(IReadOnlyList<ExperimentResult> results, AuditChainVerificationResult chainVerification)
    {
        var m = new AggregateMetrics
        {
            TotalScenarios = results.Count,
            AuditChainValid = chainVerification.IsValid,
            AuditChainBreaks = chainVerification.Breaks
        };

        if (results.Count == 0) return m;

        m.PolicyEnforcementAccuracy = Rate(results, r => r.DecisionCorrect);
        m.ReasonCodeAccuracy = Rate(results, r => r.ReasonCodeCorrect);
        m.AuditReconstructionRate = Rate(results, r => r.AuditReconstructable);

        var authorizedCategories = ScenarioCategoryExtensions.AuthorizedCategories.ToHashSet();
        var escalationCategories = ScenarioCategoryExtensions.EscalationCategories.ToHashSet();
        var adversarialCategories = ScenarioCategoryExtensions.AdversarialCategories.ToHashSet();

        var unauthorized = results.Where(r => !authorizedCategories.Contains(r.Category)).ToList();
        m.UnauthorizedTransactionPreventionRate = Rate(unauthorized, r => r.ActualDecision != Decision.Approve);

        var authorized = results.Where(r => authorizedCategories.Contains(r.Category)).ToList();
        m.AuthorizedTransactionAcceptanceRate = Rate(authorized, r => r.ActualDecision == Decision.Approve);

        var revocation = results.Where(r => r.Category is ScenarioCategory.RevokedAgent or ScenarioCategory.RevokedAuthority).ToList();
        m.RevocationEnforcementRate = Rate(revocation, r => r.ActualDecision == Decision.Deny);

        var escalation = results.Where(r => escalationCategories.Contains(r.Category)).ToList();
        m.HumanEscalationAccuracy = Rate(escalation, r => r.ActualDecision == Decision.Escalate);

        m.EvidencePrecision = results.Average(r => r.EvidencePrecision);
        m.EvidenceRecall = results.Average(r => r.EvidenceRecall);
        m.EvidenceF1 = results.Average(r => r.EvidenceF1);

        var policyLatencies = results.Select(r => (double)r.PolicyLatencyMs).ToList();
        var wallLatencies = results.Select(r => r.WallLatencyMs).ToList();
        m.MedianPolicyLatencyMs = Percentile(policyLatencies, 50);
        m.P95PolicyLatencyMs = Percentile(policyLatencies, 95);
        m.P99PolicyLatencyMs = Percentile(policyLatencies, 99);
        m.MedianWallLatencyMs = Percentile(wallLatencies, 50);
        m.P95WallLatencyMs = Percentile(wallLatencies, 95);

        // Confusion matrix: expected decision -> actual decision -> count
        foreach (var expected in Enum.GetValues<Decision>())
        {
            var row = new Dictionary<string, int>();
            foreach (var actual in Enum.GetValues<Decision>())
            {
                row[actual.ToString()] = results.Count(r => r.ExpectedDecision == expected && r.ActualDecision == actual);
            }
            m.ConfusionMatrix[expected.ToString()] = row;
        }

        // Per-category breakdown
        foreach (var category in results.Select(r => r.Category).Distinct().OrderBy(c => c.ToString()))
        {
            var subset = results.Where(r => r.Category == category).ToList();
            m.PerCategory.Add(new CategoryResult
            {
                Category = category.ToString(),
                Count = subset.Count,
                DecisionAccuracy = Rate(subset, r => r.DecisionCorrect),
                ReasonCodeAccuracy = Rate(subset, r => r.ReasonCodeCorrect),
                AverageEvidenceF1 = subset.Average(r => r.EvidenceF1),
                MedianPolicyLatencyMs = Percentile(subset.Select(r => (double)r.PolicyLatencyMs).ToList(), 50)
            });
        }

        // Adversarial metrics (Priority 4): derived from the attack-category subset of the same dataset.
        var attacks = results.Where(r => adversarialCategories.Contains(r.Category)).ToList();
        var legitimate = results.Where(r => r.Category == ScenarioCategory.Legitimate).ToList();
        m.Adversarial = new AdversarialMetrics
        {
            AttackScenarios = attacks.Count,
            AttackSuccessRate = Rate(attacks, r => r.ActualDecision == Decision.Approve),
            AttackPreventionRate = Rate(attacks, r => r.ActualDecision != Decision.Approve),
            FalsePositiveRate = Rate(legitimate, r => r.ActualDecision != Decision.Approve),
            FalseNegativeRate = Rate(attacks, r => r.ActualDecision == Decision.Approve)
        };

        return m;
    }

    private static double Rate(IReadOnlyList<ExperimentResult> results, Func<ExperimentResult, bool> predicate) =>
        results.Count == 0 ? 0 : (double)results.Count(predicate) / results.Count;

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = percentile / 100.0 * (sorted.Count - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);
        if (lowerIndex == upperIndex) return sorted[lowerIndex];
        var fraction = rank - lowerIndex;
        return sorted[lowerIndex] + (sorted[upperIndex] - sorted[lowerIndex]) * fraction;
    }
}
