using AgentTrust.Orchestration;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/approvals")]
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
            var outcome = _framework.ResolveApproval(transactionId, request.Approve, request.Approver, request.Reason);
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
