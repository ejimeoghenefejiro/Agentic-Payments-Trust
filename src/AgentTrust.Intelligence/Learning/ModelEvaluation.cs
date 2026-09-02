using AgentTrust.Intelligence.Risk;

namespace AgentTrust.Intelligence.Learning;

/// <summary>Precision/recall/F1 of the AI's ESCALATE recommendation treated as the positive
/// class, against real-world/human-determined outcomes — "over time, the platform can develop a
/// valuable proprietary financial-intelligence dataset," and this is how you'd know whether the
/// AI is actually getting better or worse at flagging genuinely suspicious activity.</summary>
public sealed record ModelEvaluationResult(
    int TotalCases,
    int TruePositives,
    int FalsePositives,
    int TrueNegatives,
    int FalseNegatives,
    double Precision,
    double Recall,
    double F1,
    double Accuracy);

public static class ModelEvaluation
{
    public static ModelEvaluationResult EvaluateCurated(IOutcomeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Evaluate(store.GetCurated());
    }

    public static ModelEvaluationResult Evaluate(IReadOnlyList<DecisionFeedback> feedback)
    {
        if (feedback.Count == 0)
        {
            return new ModelEvaluationResult(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var tp = feedback.Count(f => f.AiRecommendation == IntelligenceRecommendation.Escalate && f.ActualOutcome == ActualOutcome.Suspicious);
        var fp = feedback.Count(f => f.AiRecommendation == IntelligenceRecommendation.Escalate && f.ActualOutcome == ActualOutcome.Legitimate);
        var tn = feedback.Count(f => f.AiRecommendation == IntelligenceRecommendation.Approve && f.ActualOutcome == ActualOutcome.Legitimate);
        var fn = feedback.Count(f => f.AiRecommendation == IntelligenceRecommendation.Approve && f.ActualOutcome == ActualOutcome.Suspicious);

        var precision = (tp + fp) == 0 ? 0.0 : (double)tp / (tp + fp);
        var recall = (tp + fn) == 0 ? 0.0 : (double)tp / (tp + fn);
        var f1 = (precision + recall) == 0 ? 0.0 : 2 * precision * recall / (precision + recall);
        var accuracy = (double)(tp + tn) / feedback.Count;

        return new ModelEvaluationResult(feedback.Count, tp, fp, tn, fn, precision, recall, f1, accuracy);
    }
}
