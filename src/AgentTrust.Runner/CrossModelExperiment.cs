using System.Text.Json;
using AgentTrust.Agents;

namespace AgentTrust.Runner;

/// <summary>
/// Runs the same scenario suite under different agent "model" configurations through the same
/// IPaymentAgent abstraction (AgentFactory), and reports LLM-dependent metrics (intent-generation
/// correctness, evidence quality, agent latency) separately per model, alongside a single
/// policy-engine metrics block computed once from the direct-injection scenarios — those never
/// touch the agent, so they are identical for every model and are not repeated per model.
/// </summary>
public sealed record ModelProfile(string Name, Func<ScenarioDefinition, string?>? ResponseOverride, bool UseLive);

public sealed class PolicyEngineMetrics
{
    public double UnauthorisedTransactionPreventionRate { get; set; }
    public double EscalationAccuracy { get; set; }
    public double DuplicatePreventionRate { get; set; }
    public double AverageLatencyMs { get; set; }
}

public sealed class ModelMetrics
{
    public string Model { get; set; } = "";
    public double CorrectIntentGenerationRate { get; set; }
    public double EndToEndPolicyAccuracy { get; set; }
    public double EvidencePrecision { get; set; }
    public double EvidenceRecall { get; set; }
    public double EvidenceF1 { get; set; }
    public double AverageAgentLatencyMs { get; set; }
    public double AveragePolicyLatencyMs { get; set; }
    public double AverageTotalLatencyMs { get; set; }
}

public static class CrossModelExperiment
{
    public static List<ModelProfile> DefaultProfiles()
    {
        var profiles = new List<ModelProfile>
        {
            new("scripted-baseline", ResponseOverride: null, UseLive: false),
            new("scripted-degraded", ResponseOverride: DegradedResponse, UseLive: false)
        };

        if (AgentFactory.IsLiveModeConfigured)
        {
            var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
            profiles.Add(new ModelProfile($"live:{model}", ResponseOverride: null, UseLive: true));
        }

        return profiles;
    }

    /// <summary>Simulates a lower-quality model: for the one scenario expecting a clean
    /// well-formed proposal (S16), this profile drops the amount field, producing invalid
    /// output where the baseline model succeeds — illustrating how CorrectIntentGenerationRate
    /// diverges between models even though the policy engine and scenarios are unchanged.</summary>
    private static string? DegradedResponse(ScenarioDefinition scenario) =>
        scenario.ScenarioId == "S16"
            ? "{\"action\":\"purchase\",\"category\":\"fuel\",\"merchant\":\"ABC Energy\",\"currency\":\"NGN\",\"reason\":\"low fuel\",\"evidenceIds\":[\"sensor_883\"]}"
            : null;

    public static PolicyEngineMetrics ComputePolicyEngineMetrics(IReadOnlyList<ScenarioResult> directResults, IReadOnlyList<ScenarioDefinition> directScenarios)
    {
        var illegitimate = directScenarios.Where(s => s.ExpectedDecision is "Deny" or "Escalate").Select(s => s.ScenarioId).ToHashSet();
        var illegitimateResults = directResults.Where(r => illegitimate.Contains(r.ScenarioId)).ToList();

        var escalateScenarios = directScenarios.Where(s => s.ExpectedDecision == "Escalate").Select(s => s.ScenarioId).ToHashSet();
        var escalateResults = directResults.Where(r => escalateScenarios.Contains(r.ScenarioId)).ToList();

        var duplicateResults = directResults.Where(r => r.ScenarioId == "S09").ToList();

        return new PolicyEngineMetrics
        {
            UnauthorisedTransactionPreventionRate = Rate(illegitimateResults),
            EscalationAccuracy = Rate(escalateResults),
            DuplicatePreventionRate = Rate(duplicateResults),
            AverageLatencyMs = directResults.Count == 0 ? 0 : directResults.Average(r => r.PolicyLatencyMs)
        };
    }

    public static async Task<ModelMetrics> RunModelAsync(ModelProfile profile, IReadOnlyList<ScenarioDefinition> agentScenarios)
    {
        var results = new List<(ScenarioResult Result, bool ExpectedValid)>();

        foreach (var scenario in agentScenarios)
        {
            var effective = CloneWithOverride(scenario, profile);
            var result = await ScenarioRunner.RunAsync(effective);
            results.Add((result, scenario.ExpectedAgentOutputValid ?? true));
        }

        var correctIntentGeneration = results.Count(r =>
            string.Equals(r.Result.AgentOutputStatus, r.ExpectedValid ? "Valid" : "Invalid", StringComparison.OrdinalIgnoreCase));

        return new ModelMetrics
        {
            Model = profile.Name,
            CorrectIntentGenerationRate = results.Count == 0 ? 0 : (double)correctIntentGeneration / results.Count,
            EndToEndPolicyAccuracy = results.Count == 0 ? 0 : (double)results.Count(r => r.Result.Correct) / results.Count,
            EvidencePrecision = results.Count == 0 ? 0 : results.Average(r => r.Result.EvidencePrecision),
            EvidenceRecall = results.Count == 0 ? 0 : results.Average(r => r.Result.EvidenceRecall),
            EvidenceF1 = results.Count == 0 ? 0 : results.Average(r => r.Result.EvidenceF1),
            AverageAgentLatencyMs = results.Count == 0 ? 0 : results.Average(r => r.Result.AgentLatencyMs),
            AveragePolicyLatencyMs = results.Count == 0 ? 0 : results.Average(r => r.Result.PolicyLatencyMs),
            AverageTotalLatencyMs = results.Count == 0 ? 0 : results.Average(r => r.Result.AgentLatencyMs + r.Result.PolicyLatencyMs)
        };
    }

    private static ScenarioDefinition CloneWithOverride(ScenarioDefinition scenario, ModelProfile profile)
    {
        var json = JsonSerializer.Serialize(scenario);
        var clone = JsonSerializer.Deserialize<ScenarioDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        if (profile.UseLive)
        {
            clone.ScriptedAgentResponse = null; // forces AgentFactory.CreateLive in ScenarioRunner
        }
        else
        {
            var overrideResponse = profile.ResponseOverride?.Invoke(scenario);
            if (overrideResponse is not null) clone.ScriptedAgentResponse = overrideResponse;
        }

        return clone;
    }

    private static double Rate(IReadOnlyList<ScenarioResult> results) =>
        results.Count == 0 ? 0 : (double)results.Count(r => r.Correct) / results.Count;
}
