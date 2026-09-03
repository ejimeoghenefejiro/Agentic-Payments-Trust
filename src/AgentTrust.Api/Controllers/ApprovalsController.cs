using AgentTrust.Orchestration;
using AgentTrust.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/approvals")]
[Authorize(Policy = "StepUp")]
public sealed class ApprovalsController : ControllerBase
{
    private readonly TrustFramework _framework;
    public ApprovalsController(TrustFramework framework) => _framework = framework;

    /// <summary>Resolves a pending ESCALATE. The payment adapter only executes here, on
    /// Approve — never before a human has resolved the escalation.</summary>
    [HttpPost("{transactionId}")]
    public IActionResult Decide(string transactionId, [FromBody] ApprovalDecisionRequest request)
    {
        try
        {
            var principalId = User.FindFirst(AgentTrustClaimTypes.PrincipalId)?.Value;
            var intent = _framework.FindIntent(transactionId);
            if (intent is null) return NotFound();
            if (principalId is null || !string.Equals(intent.PrincipalId, principalId, StringComparison.Ordinal)) return Forbid();
            var outcome = _framework.ResolveApproval(transactionId, request.Approve, principalId, request.Reason);
            return Ok(new
            {
                transactionId,
                finalDecision = outcome.PolicyDecision.Decision.ToString(),
                paymentStatus = outcome.PaymentResult.Status.ToString(),
                approval = _framework.FindApproval(transactionId)
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
