using System.Text.Json;
using AgentTrust.Evidence;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Data;

public sealed class EfAuditRecordStore : IAuditRecordStore
{
    private readonly AgentTrustDbContext _db;
    public EfAuditRecordStore(AgentTrustDbContext db) => _db = db;

    public void Append(ChainedAuditRecord entry)
    {
        _db.AuditRecords.Add(new AuditRecordEntity
        {
            SequenceNumber = entry.SequenceNumber,
            TransactionId = entry.Record.TransactionId,
            AgentId = entry.Record.AgentId,
            PrincipalId = entry.Record.PrincipalId,
            AuthorityId = entry.Record.AuthorityId,
            PolicyVersion = entry.Record.PolicyVersion,
            RecordJson = JsonSerializer.Serialize(entry.Record),
            PreviousHash = entry.PreviousHash,
            CurrentHash = entry.CurrentHash,
            Timestamp = entry.Record.Timestamp
        });
        _db.SaveChanges();
    }

    public IReadOnlyList<ChainedAuditRecord> LoadAll()
    {
        return _db.AuditRecords.AsNoTracking()
            .OrderBy(e => e.SequenceNumber)
            .ToList()
            .Select(e => new ChainedAuditRecord(
                JsonSerializer.Deserialize<AgentTrust.Core.Models.AuditRecord>(e.RecordJson)!,
                e.PreviousHash,
                e.CurrentHash,
                e.SequenceNumber))
            .ToList();
    }
}
