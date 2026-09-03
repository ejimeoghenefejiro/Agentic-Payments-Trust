using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentTrust.Commerce;

public sealed record PurchaseAuditEvent(string EventId, string EventType, string PurchaseIntentId,
    string PrincipalId, string? TransactionId, string IntentHash, DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string> Metadata);
public interface IPurchaseAuditSink { void Append(PurchaseAuditEvent auditEvent); IReadOnlyList<PurchaseAuditEvent> Find(string purchaseIntentId); }
public sealed class InMemoryPurchaseAuditSink : IPurchaseAuditSink
{
    private readonly object _gate = new(); private readonly List<PurchaseAuditEvent> _events = new();
    public void Append(PurchaseAuditEvent item) { lock (_gate) _events.Add(item); }
    public IReadOnlyList<PurchaseAuditEvent> Find(string id) { lock (_gate) return _events.Where(x => x.PurchaseIntentId == id).ToList(); }
}
