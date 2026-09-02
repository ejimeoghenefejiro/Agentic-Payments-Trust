using AgentTrust.Agents;
using AgentTrust.Core.Models;
using AgentTrust.Intelligence.Graph;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Risk;
using AgentTrust.Orchestration;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/transactions")]
public sealed class TransactionsController : ControllerBase
{
    private readonly TrustFramework _framework;
    private readonly ITransactionEventStore _eventStore;
    private readonly InvestigationPlanner _investigationPlanner;

    public TransactionsController(TrustFramework framework, ITransactionEventStore eventStore, InvestigationPlanner investigationPlanner)
    {
        _framework = framework;
        _eventStore = eventStore;
        _investigationPlanner = investigationPlanner;
    }

    /// <summary>
    /// Supports natural-language agent-driven execution: set UserInstruction and the request
    /// is routed through a SemanticKernelPaymentAgent first (agent proposes, never decides).
    /// Omit UserInstruction to submit a TransactionIntent directly (direct-injection mode).
    /// </summary>
    [HttpPost("request")]
    public async Task<IActionResult> RequestTransaction([FromBody] TransactionRequest request)
    {
        var evidence = request.Evidence.Select(e => new EvidenceItem(e.EvidenceId, e.Type, e.Description, e.Exists)).ToList();

        TransactionIntent intent;
        AgentProposalResult? agentProposal = null;

        if (!string.IsNullOrWhiteSpace(request.UserInstruction))
        {
            IPaymentAgent agent = request.ScriptedAgentResponse is not null
                ? AgentFactory.CreateScripted(request.AgentId, request.ScriptedAgentResponse)
                : AgentFactory.IsLiveModeConfigured
                    ? AgentFactory.CreateLive(request.AgentId)
                    : throw new InvalidOperationException("Set OPENAI_API_KEY, or supply ScriptedAgentResponse, to run agent-driven requests.");

            var context = new AgentProposalContext(
                request.TransactionId, request.AgentId, request.PrincipalId, request.UserInstruction,
                evidence, request.Context ?? new(), request.ExpectedCurrency, DateTimeOffset.UtcNow);

            agentProposal = await agent.ProposeTransactionAsync(context);

            if (agentProposal.Status == AgentOutputStatus.Invalid)
            {
                return UnprocessableEntity(new
                {
                    transactionId = request.TransactionId,
                    decision = "Rejected",
                    reasonCodes = agentProposal.ValidationReasonCodes,
                    rawAgentOutput = agentProposal.RawOutput
                });
            }

            intent = agentProposal.Intent!;
        }
        else
        {
            intent = new TransactionIntent(
                request.TransactionId, request.AgentId, request.PrincipalId,
                request.Action ?? "", request.Merchant ?? "", request.Category ?? "",
                request.Amount ?? 0, request.Reason ?? "", evidence, DateTimeOffset.UtcNow, request.IdempotencyKey);
        }

        MultiStepInvestigationResult? investigation = null;
        if (request.CandidateEvent is not null)
        {
            var candidate = request.CandidateEvent.ToDomain();
            var merchantHistory = _eventStore.GetMerchantHistory(candidate.MerchantId);
            var graph = RelationshipAnalyzer.BuildGraph(merchantHistory.Append(candidate));
            investigation = _investigationPlanner.Investigate(candidate, graph);

            // Intelligence output remains advisory and is returned separately. It is deliberately
            // not promoted into the authoritative EvidenceManifest: model/analytics-produced
            // references must never satisfy deterministic policy evidence requirements merely
            // because the intelligence layer emitted them. InvestigationAgent records the event.
        }

        var manifest = new EvidenceManifest(intent.TransactionId, intent.Evidence.ToList(), Array.Empty<string>());
        var outcome = _framework.ProcessTransaction(intent, manifest);

        return Ok(new
        {
            transactionId = intent.TransactionId,
            decision = outcome.PolicyDecision.Decision.ToString(),
            reasonCodes = outcome.PolicyDecision.ReasonCodes,
            paymentStatus = outcome.PaymentResult.Status.ToString(),
            agentLatencyMs = agentProposal?.AgentLatencyMs,
            policyLatencyMs = outcome.LatencyMs,
            auditRecordHash = outcome.Audit.EvidenceHash,
            intelligence = investigation is null ? null : new
            {
                recommendation = investigation.FinalAssessment.Recommendation.ToString(),
                riskScore = investigation.FinalAssessment.RiskScore,
                riskFactors = investigation.FinalAssessment.RiskFactors,
                steps = investigation.Steps
            }
        });
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var intent = _framework.FindIntent(id);
        if (intent is null) return NotFound();
        var decision = _framework.FindPolicyDecision(id);
        var payment = _framework.FindPaymentResult(id);
        var approval = _framework.FindApproval(id);

        return Ok(new { intent, decision, payment, approval });
    }
}
