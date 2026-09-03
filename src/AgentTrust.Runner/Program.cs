using System.Text.Json;
using AgentTrust.Orchestration;
using AgentTrust.Payments;
using AgentTrust.Runner;
using AgentTrust.Runner.Experiments;
using Microsoft.EntityFrameworkCore;

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

if (args.Contains("--intelligence-demo"))
{
    IntelligenceDemo.Run();
    return;
}

if (args.Contains("--mandate-demo"))
{
    MandateDemo.Run();
    return;
}

if (args.Contains("--intelligence-phase3-demo"))
{
    IntelligencePhase3Demo.Run();
    return;
}

if (args.Contains("--cross-model"))
{
    await RunCrossModelExperiment(scenariosDir, resultsDir);
    return;
}

if (args.Contains("--live-b2-b3"))
{
    await RunLiveB2B3(args, repoRoot, resultsDir);
    return;
}

if (args.Contains("--consumer-purchase-demo"))
{
    await ConsumerPurchaseDemo.RunAsync(repoRoot);
    return;
}

if (args.Contains("--research-eval"))
{
    RunResearchEvaluation(args, resultsDir);
    return;
}

await RunScenarioSuite(scenariosDir, resultsDir);

static async Task RunLiveB2B3(string[] args, string repoRoot, string resultsDir)
{
    var developmentSettings = Path.Combine(repoRoot, "src", "AgentTrust.Api", "appsettings.Development.json");
    string? configuredKey = null;
    string? configuredModel = null;
    if (File.Exists(developmentSettings))
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(developmentSettings));
        if (document.RootElement.TryGetProperty("OpenAI", out var openAi))
        {
            if (openAi.TryGetProperty("ApiKey", out var key)) configuredKey = key.GetString();
            if (openAi.TryGetProperty("Model", out var model)) configuredModel = model.GetString();
        }
    }
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? configuredKey;
    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("Set OPENAI_API_KEY or the ignored development setting before running the live experiment.");
    var chatModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? configuredModel ?? "gpt-4o-mini";
    var embeddingModel = Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_MODEL") ?? "text-embedding-3-small";
    var embeddingDimensions = GetIntArg(args, "--embedding-dimensions", 1536);
    var repetitions = GetIntArg(args, "--repetitions", 5);
    var requestDelayMs = GetIntArg(args, "--request-delay-ms", 4500);
    if (requestDelayMs is < 0 or > 60_000)
        throw new ArgumentOutOfRangeException(nameof(requestDelayMs), "--request-delay-ms must be between 0 and 60000.");

    Console.WriteLine($"Running live paired B2/B3 experiment: model={chatModel}, embedding={embeddingModel}, repetitions={repetitions}, requestDelay={requestDelayMs}ms...");
    ComparativeResearchReport report;
    try
    {
        report = await LiveB2B3Experiment.RunAsync(repoRoot, apiKey, chatModel, embeddingModel,
            embeddingDimensions, repetitions, requestDelayMs);
    }
    catch (HttpRequestException error)
    {
        Console.Error.WriteLine($"Live B2/B3 experiment could not reach the configured provider: {error.Message}");
        Environment.ExitCode = 2;
        return;
    }
    var outputDir = Path.Combine(resultsDir, "experiments", report.Protocol.StudyId);
    ExperimentReportWriter.WriteComparative(outputDir, report);
    foreach (var metrics in report.Systems)
    {
        var recommendationAccuracy = metrics.DecisionAccuracy is null ? "N/A" : metrics.DecisionAccuracy.Value.ToString("P1");
        Console.WriteLine($"{metrics.SystemId}: recommendationAccuracy={recommendationAccuracy}, trustAccuracy={metrics.TrustDecisionAccuracy:P1}, evidenceF1={metrics.EvidenceF1:F3}, retrievalF1={F1(metrics.SemanticRetrievalPrecision, metrics.SemanticRetrievalRecall):F3}, MRR={metrics.MeanReciprocalRank:F3}, tools={metrics.MeanToolCalls:F1}, p95={metrics.P95LatencyMs:F0}ms, payments={metrics.PaymentExecutions}, unauthorized={metrics.UnauthorizedExecutions}, bypass={metrics.PolicyBypassSuccesses}/{metrics.PolicyBypassAttempts}");
    }
    Console.WriteLine("Trust-boundary diagnostics (B0):");
    Console.WriteLine($"{"Case",-24} {"Expected",-10} {"Actual",-10} {"Payment",-14} Match");
    foreach (var trial in report.Trials.Where(trial => trial.Configuration == ResearchConfiguration.B0DeterministicTrust))
        Console.WriteLine($"{trial.CaseId,-24} {trial.ExpectedTrustDecision,-10} {trial.TrustDecision,-10} {trial.PaymentStatus,-14} {trial.ExpectedTrustDecision == trial.TrustDecision}");
    Console.WriteLine($"Results written to {outputDir}");
}

static double F1(double precision, double recall) => precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

static void RunResearchEvaluation(string[] args, string resultsDir)
{
    var seed = GetIntArg(args, "--seed", 42);
    var count = GetIntArg(args, "--count", 1000);
    var sqlServerConnection = Environment.GetEnvironmentVariable("SQLSERVER_CONNECTION");
    var useSqlServer = args.Contains("--sql-server") || !string.IsNullOrWhiteSpace(sqlServerConnection);

    IReadOnlyList<ExperimentResult> results;
    AgentTrust.Evidence.AuditChainVerificationResult chainVerification;

    if (useSqlServer)
    {
        if (string.IsNullOrWhiteSpace(sqlServerConnection))
        {
            Console.WriteLine("--sql-server was passed but SQLSERVER_CONNECTION is not set. Aborting.");
            return;
        }

        Console.WriteLine($"Research Evaluation Phase 1: generating {count} scenarios (seed={seed}) against SQL Server...");
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AgentTrust.Data.AgentTrustDbContext>()
            .UseSqlServer(sqlServerConnection, x => x.MigrationsAssembly("AgentTrust.Data.Migrations.SqlServer"))
            .Options;
        using var db = new AgentTrust.Data.AgentTrustDbContext(options);
        // A reproducible experiment must not depend on residual data from a previous run against
        // this database (e.g. re-running the same seed produces the same transaction ids, which
        // collided with leftover rows and broke a unique constraint before this was added) — so
        // the experiments database is reset on every --sql-server run. This only ever targets
        // whatever database SQLSERVER_CONNECTION points at for this command; it does not touch
        // any other database on the server. Migrate() (not EnsureCreated()) so the experiment
        // database's schema is produced the same way the real API's is — from the same
        // migrations, not a separate reflection-based snapshot that could silently drift from them.
        db.Database.EnsureDeleted();
        db.Database.Migrate();

        var agents = new AgentTrust.Data.EfAgentRegistry(db);
        var bindings = new AgentTrust.Data.EfPrincipalBindingStore(db);
        var authorities = new AgentTrust.Data.EfDelegatedAuthorityStore(db);
        var ledger = new AgentTrust.Data.EfTransactionLedger(db);
        var paymentAdapter = new MockPaymentAdapter();
        var intentStore = new AgentTrust.Data.EfTransactionIntentStore(db);
        var evidenceStore = new AgentTrust.Data.EfEvidenceManifestStore(db);
        var policyStore = new AgentTrust.Data.EfPolicyDecisionStore(db);
        var paymentStore = new AgentTrust.Data.EfPaymentOutcomeStore(db);
        var approvalStore = new AgentTrust.Data.EfApprovalStore(db);
        var auditStore = new AgentTrust.Data.EfAuditRecordStore(db);
        var framework = new TrustFramework(agents, bindings, authorities, ledger, paymentAdapter,
            intentStore, evidenceStore, policyStore, paymentStore, approvalStore, auditStore);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        (results, chainVerification) = ExperimentRunner.Run(seed, count, agents, bindings, authorities, paymentAdapter, framework);
        stopwatch.Stop();
        Console.WriteLine($"SQL Server run completed in {stopwatch.Elapsed.TotalSeconds:F1}s ({count} scenarios, each writing agent/binding/authority/intent/evidence/policy/payment/audit rows).");
    }
    else
    {
        Console.WriteLine($"Research Evaluation Phase 1: generating {count} scenarios (seed={seed}) in-memory...");
        (results, chainVerification) = ExperimentRunner.Run(seed, count);
    }

    var metrics = MetricsCalculator.Compute(results, chainVerification);

    var backendSuffix = useSqlServer ? "_sqlserver" : "_inmemory";
    var outputDir = Path.Combine(resultsDir, "experiments", $"run_seed{seed}_n{count}{backendSuffix}");
    ExperimentReportWriter.Write(outputDir, seed, results, metrics);

    Console.WriteLine();
    Console.WriteLine($"N = {metrics.TotalScenarios} transaction scenarios");
    Console.WriteLine();
    Console.WriteLine($"Policy Enforcement Accuracy:        {metrics.PolicyEnforcementAccuracy:P1}");
    Console.WriteLine($"Unauthorized Prevention Rate:       {metrics.UnauthorizedTransactionPreventionRate:P1}");
    Console.WriteLine($"Authorized Acceptance Rate:         {metrics.AuthorizedTransactionAcceptanceRate:P1}");
    Console.WriteLine($"Revocation Enforcement Rate:        {metrics.RevocationEnforcementRate:P1}");
    Console.WriteLine($"Human Escalation Accuracy:          {metrics.HumanEscalationAccuracy:P1}");
    Console.WriteLine($"Reason-Code Accuracy:               {metrics.ReasonCodeAccuracy:P1}");
    Console.WriteLine();
    Console.WriteLine($"Evidence Precision:                 {metrics.EvidencePrecision:P1}");
    Console.WriteLine($"Evidence Recall:                    {metrics.EvidenceRecall:P1}");
    Console.WriteLine($"Evidence F1:                        {metrics.EvidenceF1:P1}");
    Console.WriteLine();
    Console.WriteLine($"Audit Reconstruction Rate:          {metrics.AuditReconstructionRate:P1}");
    Console.WriteLine($"Audit Chain Valid:                  {metrics.AuditChainValid}");
    Console.WriteLine();
    Console.WriteLine($"Median policy latency:               {metrics.MedianPolicyLatencyMs:F3} ms");
    Console.WriteLine($"P95 policy latency:                  {metrics.P95PolicyLatencyMs:F3} ms");
    Console.WriteLine($"P99 policy latency:                  {metrics.P99PolicyLatencyMs:F3} ms");
    Console.WriteLine($"Median wall latency (incl. overhead): {metrics.MedianWallLatencyMs:F3} ms");
    Console.WriteLine($"P95 wall latency (incl. overhead):    {metrics.P95WallLatencyMs:F3} ms");
    Console.WriteLine();
    Console.WriteLine("=== Adversarial subset (prompt injection, duplicate payment, credential attack, authority-scope violation) ===");
    Console.WriteLine($"Attack scenarios:        {metrics.Adversarial.AttackScenarios}");
    Console.WriteLine($"Attack Success Rate:     {metrics.Adversarial.AttackSuccessRate:P1}  (lower is better)");
    Console.WriteLine($"Attack Prevention Rate:  {metrics.Adversarial.AttackPreventionRate:P1}");
    Console.WriteLine($"False Positive Rate:     {metrics.Adversarial.FalsePositiveRate:P1}  (legitimate transactions incorrectly blocked)");
    Console.WriteLine($"False Negative Rate:     {metrics.Adversarial.FalseNegativeRate:P1}  (attacks incorrectly approved)");
    Console.WriteLine();
    Console.WriteLine("=== Per-category ===");
    foreach (var c in metrics.PerCategory)
    {
        Console.WriteLine($"  {c.Category,-28} n={c.Count,-6} decision_acc={c.DecisionAccuracy:P1}  reason_acc={c.ReasonCodeAccuracy:P1}  evidence_f1={c.AverageEvidenceF1:F2}  median_latency={c.MedianPolicyLatencyMs:F3}ms");
    }
    Console.WriteLine();
    Console.WriteLine($"Results written to {outputDir}");
    Console.WriteLine($"  results.csv, per_category.csv, confusion_matrix.csv, summary.json");
    Console.WriteLine($"Reproduce with: dotnet run --project src/AgentTrust.Runner -- --research-eval --seed {seed} --count {count}");
}

static int GetIntArg(string[] args, string name, int defaultValue)
{
    var index = Array.IndexOf(args, name);
    if (index < 0 || index + 1 >= args.Length) return defaultValue;
    return int.TryParse(args[index + 1], out var value) ? value : defaultValue;
}

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
