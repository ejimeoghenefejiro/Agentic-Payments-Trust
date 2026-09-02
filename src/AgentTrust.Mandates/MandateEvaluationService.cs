namespace AgentTrust.Mandates;

public enum MandateCheckDecision
{
    Approve,
    Escalate,
    Block
}

public sealed record MandateCheckResult(
    MandateCheckDecision Decision,
    IReadOnlyList<string> Reasons,
    bool ContextMatches,
    bool WithinPerTransactionLimit,
    bool WithinWeeklyLimit,
    bool WithinDailyLimit = true,
    bool WithinMonthlyLimit = true);

/// <summary>
/// Checks what the frozen trust layer's DelegatedAuthority structurally cannot: does this
/// specific task's context (route, recipient, whatever TaskParameters the mandate carries)
/// match what was actually authorised, and is cumulative spend within the mandate's weekly/
/// monthly cap. Implements the doc's "Context Can Override Apparent Normality" scenario: an
/// in-limit amount with mismatched context still escalates, because a changed pickup/destination/
/// recipient is invisible to an amount-based policy engine.
/// </summary>
public sealed class MandateEvaluationService
{
    private readonly IMandateUsageTracker _usageTracker;

    public MandateEvaluationService(IMandateUsageTracker usageTracker) => _usageTracker = usageTracker;

    public MandateCheckResult Evaluate(
        FinancialMandate mandate,
        decimal proposedAmount,
        IReadOnlyDictionary<string, string> proposedContext,
        DateTimeOffset now)
    {
        var reasons = new List<string>();

        if (proposedAmount <= 0)
        {
            reasons.Add("AMOUNT_MUST_BE_POSITIVE");
            return new MandateCheckResult(MandateCheckDecision.Block, reasons, false, false, false, false, false);
        }

        if (!mandate.IsActive(now))
        {
            reasons.Add("MANDATE_INACTIVE");
            return new MandateCheckResult(MandateCheckDecision.Block, reasons, false, false, false);
        }

        var mismatchedKeys = mandate.TaskParameters
            .Where(kv => !proposedContext.TryGetValue(kv.Key, out var actual) || !string.Equals(actual, kv.Value, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
        var contextMatches = mismatchedKeys.Count == 0;
        if (!contextMatches)
        {
            reasons.Add($"CONTEXT_MISMATCH:{string.Join(",", mismatchedKeys)}");
        }

        var withinPerTransactionLimit = proposedAmount <= mandate.PerTransactionLimit;
        if (!withinPerTransactionLimit)
        {
            reasons.Add("ABOVE_PER_TRANSACTION_LIMIT");
        }

        var withinWeeklyLimit = true;
        if (mandate.WeeklyLimit is decimal weeklyLimit)
        {
            var spentThisWeek = _usageTracker.AmountSpentSince(mandate.MandateId, now.AddDays(-7));
            withinWeeklyLimit = spentThisWeek + proposedAmount <= weeklyLimit;
            if (!withinWeeklyLimit)
            {
                reasons.Add("ABOVE_WEEKLY_LIMIT");
            }
        }

        var withinDailyLimit = true;
        if (mandate.DailyLimit is decimal dailyLimit)
        {
            var start = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            withinDailyLimit = _usageTracker.AmountSpentSince(mandate.MandateId, start) + proposedAmount <= dailyLimit;
            if (!withinDailyLimit) reasons.Add("ABOVE_DAILY_LIMIT");
        }

        var withinMonthlyLimit = true;
        if (mandate.MonthlyLimit is decimal monthlyLimit)
        {
            withinMonthlyLimit = _usageTracker.AmountSpentSince(mandate.MandateId, now.AddMonths(-1)) + proposedAmount <= monthlyLimit;
            if (!withinMonthlyLimit) reasons.Add("ABOVE_MONTHLY_LIMIT");
        }

        var decision = DetermineDecision(mandate, contextMatches, withinPerTransactionLimit, withinWeeklyLimit, withinDailyLimit, withinMonthlyLimit);
        return new MandateCheckResult(decision, reasons, contextMatches, withinPerTransactionLimit, withinWeeklyLimit, withinDailyLimit, withinMonthlyLimit);
    }

    private static MandateCheckDecision DetermineDecision(FinancialMandate mandate, bool contextMatches, bool withinPerTransactionLimit, bool withinWeeklyLimit, bool withinDailyLimit, bool withinMonthlyLimit)
    {
        // A context mismatch always escalates regardless of amount — a fixed spending limit is
        // not the same claim as "this specific recipient/route/task is the one that was approved."
        if (!contextMatches)
        {
            return MandateCheckDecision.Escalate;
        }

        if (!withinWeeklyLimit || !withinDailyLimit || !withinMonthlyLimit || !withinPerTransactionLimit)
        {
            return mandate.AboveLimit == AboveLimitAction.Block ? MandateCheckDecision.Block : MandateCheckDecision.Escalate;
        }

        return MandateCheckDecision.Approve;
    }
}
