using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentTrust.Commerce;

public sealed record PurchaseAuditEvent(string EventId, string EventType, string PurchaseIntentId,
    string PrincipalId, string? TransactionId, string IntentHash, DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string> Metadata,string PreviousHash="",string CurrentHash="");
public static class PurchaseAuditHash
{
    public static string Compute(PurchaseAuditEvent item,string previous)
    {var metadata=JsonSerializer.Serialize(item.Metadata);var canonical=$"{item.EventId}|{item.EventType}|{item.PurchaseIntentId}|{item.PrincipalId}|{item.TransactionId}|{item.IntentHash}|{item.Timestamp:O}|{metadata}|{previous}";return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));}
}
public interface IPurchaseAuditSink { void Append(PurchaseAuditEvent auditEvent); IReadOnlyList<PurchaseAuditEvent> Find(string purchaseIntentId); }
public sealed class InMemoryPurchaseAuditSink : IPurchaseAuditSink
{
    private readonly object _gate = new(); private readonly List<PurchaseAuditEvent> _events = new();
    public void Append(PurchaseAuditEvent item) { lock (_gate){var previous=_events.LastOrDefault()?.CurrentHash??"GENESIS";_events.Add(item with{PreviousHash=previous,CurrentHash=PurchaseAuditHash.Compute(item,previous)});} }
    public IReadOnlyList<PurchaseAuditEvent> Find(string id) { lock (_gate) return _events.Where(x => x.PurchaseIntentId == id).ToList(); }
}
