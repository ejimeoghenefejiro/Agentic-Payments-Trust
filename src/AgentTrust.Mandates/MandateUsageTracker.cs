namespace AgentTrust.Mandates;

/// <summary>
/// Tracks cumulative spend per mandate for the weekly/monthly caps the doc's example uses
/// ("weeklyLimit": 25) — a concept the frozen trust layer's DelegatedAuthority doesn't have
/// (only a daily limit), so it's tracked here rather than by extending the frozen core.
/// </summary>
public interface IMandateUsageTracker
{
    void RecordSpend(string mandateId, decimal amount, DateTimeOffset when);
    decimal AmountSpentSince(string mandateId, DateTimeOffset since);
}

public sealed class InMemoryMandateUsageTracker : IMandateUsageTracker
{
    private readonly List<(string MandateId, decimal Amount, DateTimeOffset When)> _records = new();

    public void RecordSpend(string mandateId, decimal amount, DateTimeOffset when) =>
        _records.Add((mandateId, amount, when));

    public decimal AmountSpentSince(string mandateId, DateTimeOffset since) =>
        _records.Where(r => r.MandateId == mandateId && r.When >= since).Sum(r => r.Amount);
}
