using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentTrust.Runner.Experiments;

public static class ExperimentReportWriter
{
    public static void Write(string outputDir, int seed, IReadOnlyList<ExperimentResult> results, AggregateMetrics metrics)
    {
        Directory.CreateDirectory(outputDir);

        WriteResultsCsv(Path.Combine(outputDir, "results.csv"), results);
        WriteCategorySummaryCsv(Path.Combine(outputDir, "per_category.csv"), metrics.PerCategory);
        WriteConfusionMatrixCsv(Path.Combine(outputDir, "confusion_matrix.csv"), metrics.ConfusionMatrix);

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var summary = new
        {
            seed,
            metrics.TotalScenarios,
            metrics.AuditChainValid,
            metrics.AuditChainBreaks,
            metrics.PolicyEnforcementAccuracy,
            metrics.UnauthorizedTransactionPreventionRate,
            metrics.AuthorizedTransactionAcceptanceRate,
            metrics.RevocationEnforcementRate,
            metrics.HumanEscalationAccuracy,
            metrics.ReasonCodeAccuracy,
            metrics.EvidencePrecision,
            metrics.EvidenceRecall,
            metrics.EvidenceF1,
            metrics.AuditReconstructionRate,
            metrics.MedianPolicyLatencyMs,
            metrics.P95PolicyLatencyMs,
            metrics.P99PolicyLatencyMs,
            metrics.MedianWallLatencyMs,
            metrics.P95WallLatencyMs,
            metrics.ConfusionMatrix,
            PerCategory = metrics.PerCategory,
            metrics.Adversarial,
            generated_at = DateTimeOffset.UtcNow
        };
        File.WriteAllText(Path.Combine(outputDir, "summary.json"), JsonSerializer.Serialize(summary, jsonOptions));
    }

    private static void WriteResultsCsv(string path, IReadOnlyList<ExperimentResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("scenario_id,category,expected_decision,actual_decision,decision_correct,expected_reason_code,actual_reason_codes,reason_code_correct,expected_payment_status,actual_payment_status,payment_status_correct,evidence_precision,evidence_recall,evidence_f1,policy_latency_ms,wall_latency_ms,audit_reconstructable");
        foreach (var r in results)
        {
            sb.AppendLine(string.Join(",",
                Csv(r.ScenarioId),
                Csv(r.Category.ToString()),
                Csv(r.ExpectedDecision.ToString()),
                Csv(r.ActualDecision.ToString()),
                Csv(r.DecisionCorrect),
                Csv(r.ExpectedReasonCode ?? ""),
                Csv(string.Join(";", r.ActualReasonCodes)),
                Csv(r.ReasonCodeCorrect),
                Csv(r.ExpectedPaymentStatus.ToString()),
                Csv(r.ActualPaymentStatus.ToString()),
                Csv(r.PaymentStatusCorrect),
                Csv(r.EvidencePrecision),
                Csv(r.EvidenceRecall),
                Csv(r.EvidenceF1),
                Csv(r.PolicyLatencyMs),
                Csv(r.WallLatencyMs),
                Csv(r.AuditReconstructable)));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteCategorySummaryCsv(string path, IReadOnlyList<CategoryResult> categories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("category,count,decision_accuracy,reason_code_accuracy,average_evidence_f1,median_policy_latency_ms");
        foreach (var c in categories)
        {
            sb.AppendLine(string.Join(",",
                Csv(c.Category), Csv(c.Count), Csv(c.DecisionAccuracy), Csv(c.ReasonCodeAccuracy),
                Csv(c.AverageEvidenceF1), Csv(c.MedianPolicyLatencyMs)));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteConfusionMatrixCsv(string path, Dictionary<string, Dictionary<string, int>> matrix)
    {
        var actualLabels = matrix.Values.SelectMany(v => v.Keys).Distinct().OrderBy(k => k).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("expected_decision," + string.Join(",", actualLabels));
        foreach (var (expected, row) in matrix.OrderBy(kv => kv.Key))
        {
            sb.AppendLine(Csv(expected) + "," + string.Join(",", actualLabels.Select(l => row.GetValueOrDefault(l, 0).ToString(CultureInfo.InvariantCulture))));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static string Csv(string value) => value.Contains(',') || value.Contains('"')
        ? "\"" + value.Replace("\"", "\"\"") + "\""
        : value;

    private static string Csv(bool value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Csv(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Csv(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Csv(double value) => value.ToString("F4", CultureInfo.InvariantCulture);
}
