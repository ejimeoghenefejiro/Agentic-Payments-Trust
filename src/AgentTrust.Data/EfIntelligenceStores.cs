using System.Text.Json;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Investigation;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Data;

/// <summary>
/// EF-Core-backed persistence for AgentTrust.Intelligence's source data: raw transaction events
/// (FinancialGraph and CustomerBehaviourProfile are both rebuilt on demand from these rows —
/// neither the graph nor a profile is itself a stored shape) and periodic profile snapshots
/// (long-term memory for behavioural-change detection). Ordering/date-range work happens after
/// materialising rows into memory, not inside the LINQ query — see EfTransactionLedger for why:
/// SQL Server and SQLite each reject a different DateTimeOffset expression shape in-query.
/// </summary>
public sealed class EfTransactionEventStore : ITransactionEventStore
{
    private readonly AgentTrustDbContext _db;
    public EfTransactionEventStore(AgentTrustDbContext db) => _db = db;

    public void Record(TransactionEvent transactionEvent)
    {
        var existing = _db.TransactionEvents.Find(transactionEvent.TransactionId);
        var entity = ToEntity(transactionEvent);
        if (existing is null) _db.TransactionEvents.Add(entity);
        else _db.Entry(existing).CurrentValues.SetValues(entity);
        _db.SaveChanges();
    }

    public IReadOnlyList<TransactionEvent> GetCustomerHistory(string customerId) =>
        _db.TransactionEvents.AsNoTracking().Where(e => e.CustomerId == customerId).ToList()
            .OrderBy(e => e.Timestamp).Select(ToDomain).ToList();

    public IReadOnlyList<TransactionEvent> GetMerchantHistory(string merchantId) =>
        _db.TransactionEvents.AsNoTracking().Where(e => e.MerchantId == merchantId).ToList()
            .OrderBy(e => e.Timestamp).Select(ToDomain).ToList();

    private static TransactionEventEntity ToEntity(TransactionEvent e) => new()
    {
        TransactionId = e.TransactionId,
        CustomerId = e.CustomerId,
        MerchantId = e.MerchantId,
        Amount = e.Amount,
        Currency = e.Currency,
        Timestamp = e.Timestamp,
        DeviceId = e.DeviceId,
        IpAddress = e.IpAddress,
        Location = e.Location,
        BeneficiaryId = e.BeneficiaryId,
        BeneficiaryCreatedAt = e.BeneficiaryCreatedAt,
        WasRefunded = e.WasRefunded,
        PriorFailedAttempts = e.PriorFailedAttempts
    };

    private static TransactionEvent ToDomain(TransactionEventEntity e) => new(
        e.TransactionId, e.CustomerId, e.MerchantId, e.Amount, e.Currency, e.Timestamp,
        e.DeviceId, e.IpAddress, e.Location, e.BeneficiaryId, e.BeneficiaryCreatedAt,
        e.WasRefunded, e.PriorFailedAttempts);
}

public sealed class EfProfileHistoryStore : IProfileHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly AgentTrustDbContext _db;
    public EfProfileHistoryStore(AgentTrustDbContext db) => _db = db;

    public void RecordSnapshot(string entityId, CustomerBehaviourProfile profile, DateTimeOffset takenAt)
    {
        _db.ProfileSnapshots.Add(new ProfileSnapshotEntity
        {
            EntityId = entityId,
            TakenAt = takenAt,
            ProfileJson = JsonSerializer.Serialize(profile, JsonOptions)
        });
        _db.SaveChanges();
    }

    public IReadOnlyList<ProfileSnapshot> GetHistory(string entityId) =>
        _db.ProfileSnapshots.AsNoTracking().Where(s => s.EntityId == entityId).ToList()
            .OrderBy(s => s.TakenAt)
            .Select(s => new ProfileSnapshot(entityId, JsonSerializer.Deserialize<CustomerBehaviourProfile>(s.ProfileJson, JsonOptions)!, s.TakenAt))
            .ToList();

    public CustomerBehaviourProfile? GetSnapshotClosestTo(string entityId, DateTimeOffset asOf) =>
        GetHistory(entityId)
            .OrderBy(s => Math.Abs((s.TakenAt - asOf).Ticks))
            .FirstOrDefault()?.Profile;
}
