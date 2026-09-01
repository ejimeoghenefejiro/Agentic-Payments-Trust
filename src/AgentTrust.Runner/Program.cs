using System.Text.Json;
using AgentTrust.Runner;

var baseDir = AppContext.BaseDirectory;
var repoRoot = FindRepoRoot(baseDir);
var scenariosDir = Path.Combine(repoRoot, "scenarios");
var resultsDir = Path.Combine(repoRoot, "results");
Directory.CreateDirectory(resultsDir);

if (args.Contains("--demo"))
{
    await EndToEndDemo.RunAsync();
    return;
}

if (args.Contains("--cross-model"))
{
    await RunCrossModelExperiment(scenariosDir, resultsDir);
    return;
}

await RunScenarioSuite(scenariosDir, resultsDir);

static async Task RunScenarioSuite(string scenariosDir, string resultsDir)
{
    var scenarios = ScenarioRunner.LoadAll(scenariosDir);
    var results = new List<ScenarioResult>();
    foreach (var scenario in scenarios)
    {
        results.Add(await ScenarioRunner.RunAsync(scenario));
    }

    foreach (var r in results)
    {
        var mark = r.Correct ? "PASS" : "FAIL";
        var mode = r.AgentMode ? $"agent({r.AgentOutputStatus})" : "direct";
        Console.WriteLine($"[{mark}] {r.ScenarioId,-10} expected={r.ExpectedDecision,-9} actual={r.ActualDecision,-9} mode={mode,-16} {r.Description}");
    }

    var accuracy = results.Count == 0 ? 0 : (double)results.Count(r => r.Correct) / results.Count;
    var avgF1 = results.Count == 0 ? 0 : results.Average(r => r.EvidenceF1);
    var avgPolicyLatency = results.Count == 0 ? 0 : results.Average(r => r.PolicyLatencyMs);
    var avgAgentLatency = results.Where(r => r.AgentMode).Select(r => (double?)r.AgentLatencyMs).DefaultIfEmpty(0).Average() ?? 0;

    var directResults = results.Where(r => !r.AgentMode).ToList();
    var agentResults = results.Where(r => r.AgentMode).ToList();

    var summary = new
    {
        total_scenarios = results.Count,
        overall_accuracy = accuracy,
        direct_injection_scenarios = directResults.Count,
        direct_injection_policy_accuracy = directResults.Count == 0 ? 0 : (double)directResults.Count(r => r.Correct) / directResults.Count,
        agent_mode_scenarios = agentResults.Count,
        agent_mode_end_to_end_accuracy = agentResults.Count == 0 ? 0 : (double)agentResults.Count(r => r.Correct) / agentResults.Count,
        agent_valid_output_rate = agentResults.Count == 0 ? 0 : (double)agentResults.Count(r => r.AgentOutputStatus == "Valid") / agentResults.Count,
        average_evidence_f1 = avgF1,
        average_policy_latency_ms = avgPolicyLatency,
        average_agent_latency_ms = avgAgentLatency,
        generated_at = DateTimeOffset.UtcNow
    };

    Console.WriteLine();
    Console.WriteLine($"Overall accuracy:      {accuracy:P1}  ({results.Count(r => r.Correct)}/{results.Count})");
    Console.WriteLine($"Direct-injection:      {summary.direct_injection_policy_accuracy:P1}  ({directResults.Count} scenarios — isolates policy-engine correctness)");
    Console.WriteLine($"Agent mode:            {summary.agent_mode_end_to_end_accuracy:P1}  ({agentResults.Count} scenarios — isolates agent-intent-generation + policy correctness)");
    Console.WriteLine($"Avg evidence F1:       {avgF1:F2}");
    Console.WriteLine($"Avg policy latency:    {avgPolicyLatency:F2} ms");
    Console.WriteLine($"Avg agent latency:     {avgAgentLatency:F2} ms");

    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(Path.Combine(resultsDir, "experiment_summary.json"), JsonSerializer.Serialize(summary, jsonOptions));
    File.WriteAllText(Path.Combine(resultsDir, "scenario_results.json"), JsonSerializer.Serialize(results, jsonOptions));

    Console.WriteLine();
    Console.WriteLine($"Results written to {resultsDir}");
}

static async Task RunCrossModelExperiment(string scenariosDir, string resultsDir)
{
    var allScenarios = ScenarioRunner.LoadAll(scenariosDir);
    var directScenarios = allScenarios.Where(s => string.IsNullOrWhiteSpace(s.UserInstruction)).ToList();
    var agentScenarios = allScenarios.Where(s => !string.IsNullOrWhiteSpace(s.UserInstruction)).ToList();

    var directResults = new List<ScenarioResult>();
    foreach (var s in directScenarios) directResults.Add(await ScenarioRunner.RunAsync(s));
    var policyMetrics = CrossModelExperiment.ComputePolicyEngineMetrics(directResults, directScenarios);

    Console.WriteLine("=== Policy-engine metrics (shared across all models — 15 direct-injection scenarios) ===");
    Console.WriteLine($"Unauthorised-transaction prevention rate: {policyMetrics.UnauthorisedTransactionPreventionRate:P1}");
    Console.WriteLine($"Escalation accuracy:                      {policyMetrics.EscalationAccuracy:P1}");
    Console.WriteLine($"Duplicate prevention rate:                {policyMetrics.DuplicatePreventionRate:P1}");
    Console.WriteLine($"Average policy latency:                   {policyMetrics.AverageLatencyMs:F2} ms");
    Console.WriteLine();

    var profiles = CrossModelExperiment.DefaultProfiles();
    var modelResults = new List<ModelMetrics>();
    foreach (var profile in profiles)
    {
        Console.WriteLine($"=== Model: {profile.Name} ({agentScenarios.Count} agent-mode scenarios) ===");
        var metrics = await CrossModelExperiment.RunModelAsync(profile, agentScenarios);
        modelResults.Add(metrics);
        Console.WriteLine($"Correct intent-generation rate: {metrics.CorrectIntentGenerationRate:P1}");
        Console.WriteLine($"End-to-end policy accuracy:     {metrics.EndToEndPolicyAccuracy:P1}");
        Console.WriteLine($"Evidence precision/recall/F1:   {metrics.EvidencePrecision:P0} / {metrics.EvidenceRecall:P0} / {metrics.EvidenceF1:P0}");
        Console.WriteLine($"Avg agent / policy / total lat: {metrics.AverageAgentLatencyMs:F1} / {metrics.AveragePolicyLatencyMs:F1} / {metrics.AverageTotalLatencyMs:F1} ms");
        Console.WriteLine();
    }

    var output = new { policy_engine = policyMetrics, per_model = modelResults, generated_at = DateTimeOffset.UtcNow };
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(Path.Combine(resultsDir, "cross_model_results.json"), JsonSerializer.Serialize(output, jsonOptions));
    Console.WriteLine($"Results written to {Path.Combine(resultsDir, "cross_model_results.json")}");
}

static string FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null && !dir.GetFiles("*.sln").Any())
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? startDir;
}
