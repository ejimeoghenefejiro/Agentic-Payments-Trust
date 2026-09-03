using AgentTrust.Evidence;
using AgentTrust.Core;
using AgentTrust.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/audit")]
[Authorize(Policy = "Consumer")]
public sealed class AuditController : ControllerBase
{
    private readonly IAuditRecordStore _auditStore;
    private readonly ITransactionIntentStore _intents;
    public AuditController(IAuditRecordStore auditStore, ITransactionIntentStore intents) { _auditStore = auditStore; _intents = intents; }

    [HttpGet("{transactionId}")]
    public IActionResult Get(string transactionId)
    {
        var principalId = User.FindFirst(AgentTrustClaimTypes.PrincipalId)?.Value;
        var intent = _intents.Find(transactionId);
        if (intent is null) return NotFound();
        if (principalId is null || !string.Equals(intent.PrincipalId, principalId, StringComparison.Ordinal)) return Forbid();
        var entry = _auditStore.LoadAll().LastOrDefault(e => e.Record.TransactionId == transactionId);
        return entry is null ? NotFound() : Ok(entry.Record);
    }

    /// <summary>Rehydrates the full chain from the persistent store (not the per-request
    /// in-memory ledger) and verifies it, so this reflects everything ever recorded, not just
    /// what happened during this HTTP request.</summary>
    [HttpGet("verify"), Authorize(Policy = "AuditAdmin")]
    public IActionResult Verify()
    {
        var entries = _auditStore.LoadAll();
        var ledger = AuditLedger.LoadExisting(entries);
        var result = ledger.Verify();
        return Ok(new { isValid = result.IsValid, breaks = result.Breaks, entryCount = entries.Count });
    }
}
