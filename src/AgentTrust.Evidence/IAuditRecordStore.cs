namespace AgentTrust.Evidence;

/// <summary>Persists chained audit entries so a ledger can be rehydrated (via
/// AuditLedger.LoadExisting) and re-verified from storage, e.g. after a process restart or
/// as a tamper check against what a database actually holds.</summary>
public interface IAuditRecordStore
{
    void Append(ChainedAuditRecord entry);
    IReadOnlyList<ChainedAuditRecord> LoadAll();
}

public sealed class InMemoryAuditRecordStore : IAuditRecordStore
{
    private readonly List<ChainedAuditRecord> _entries = new();
    public void Append(ChainedAuditRecord entry) => _entries.Add(entry);
    public IReadOnlyList<ChainedAuditRecord> LoadAll() => _entries.ToList();
}
