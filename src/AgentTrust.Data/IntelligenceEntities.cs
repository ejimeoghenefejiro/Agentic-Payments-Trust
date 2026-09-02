namespace AgentTrust.Data;

// Persistence for AgentTrust.Intelligence: raw transaction events (the source data both
// FinancialGraph and CustomerBehaviourProfile are built from on demand — the graph itself is
// never stored, only rebuilt from these rows) and periodic profile snapshots (long-term memory
// for behavioural-change detection).

public sealed class TransactionEventEntity
{
    public string TransactionId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string MerchantId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; }
    public string DeviceId { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Location { get; set; } = "";
    public string? BeneficiaryId { get; set; }
    public DateTimeOffset? BeneficiaryCreatedAt { get; set; }
    public bool WasRefunded { get; set; }
    public int PriorFailedAttempts { get; set; }
}

public sealed class ProfileSnapshotEntity
{
    public int Id { get; set; }
    public string EntityId { get; set; } = "";
    public DateTimeOffset TakenAt { get; set; }
    public string ProfileJson { get; set; } = "";
}
