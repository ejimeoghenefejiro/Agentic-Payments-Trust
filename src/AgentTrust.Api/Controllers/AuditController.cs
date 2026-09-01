using AgentTrust.Evidence;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/audit")]
public sealed class AuditController : ControllerBase
{
    private readonly IAuditRecordStore _auditStore;
    public AuditController(IAuditRecordStore auditStore) => _auditStore = auditStore;

    [HttpGet("{transactionId}")]
    public IActionResult Get(string transactionId)
    {
        var entry = _auditStore.LoadAll().LastOrDefault(e => e.Record.TransactionId == transactionId);
        return entry is null ? NotFound() : Ok(entry.Record);
    }

    /// <summary>Rehydrates the full chain from the persistent store (not the per-request
    /// in-memory ledger) and verifies it, so this reflects everything ever recorded, not just
    /// what happened during this HTTP request.</summary>
    [HttpGet("verify")]
    public IActionResult Verify()
    {
        var entries = _auditStore.LoadAll();
        var ledger = AuditLedger.LoadExisting(entries);
        var result = ledger.Verify();
        return Ok(new { isValid = result.IsValid, breaks = result.Breaks, entryCount = entries.Count });
    }
}
