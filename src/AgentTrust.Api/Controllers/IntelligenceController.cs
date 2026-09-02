using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Graph;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Learning;
using AgentTrust.Intelligence.Risk;
using AgentTrust.Agents;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

/// <summary>
/// Exposes AgentTrust.Intelligence over HTTP: record raw transaction events, ask for a
/// customer's behaviour profile, run an investigation (single- or multi-step, whichever the
/// planner decides), investigate a merchant, and record/evaluate feedback. Every recommendation
/// here is advisory — nothing in this controller can authorise a payment; that remains
/// exclusively TransactionsController -> TrustFramework's job.
/// </summary>
[ApiController]
[Route("api/intelligence")]
public sealed class IntelligenceController : ControllerBase
{
    private readonly ITransactionEventStore _eventStore;
    private readonly IProfileHistoryStore _profileHistoryStore;
    private readonly InvestigationPlanner _investigationPlanner;
    private readonly MerchantInvestigationAgent _merchantInvestigationAgent;
    private readonly IOutcomeStore _outcomeStore;
    private readonly InvestigationTools _level3Tools;
    private readonly IInvestigationStateStore _investigationStates;

    public IntelligenceController(
        ITransactionEventStore eventStore,
        IProfileHistoryStore profileHistoryStore,
        InvestigationPlanner investigationPlanner,
        MerchantInvestigationAgent merchantInvestigationAgent,
        IOutcomeStore outcomeStore,
        InvestigationTools level3Tools,
        IInvestigationStateStore investigationStates)
    {
        _eventStore = eventStore;
        _profileHistoryStore = profileHistoryStore;
        _investigationPlanner = investigationPlanner;
        _merchantInvestigationAgent = merchantInvestigationAgent;
        _outcomeStore = outcomeStore;
        _level3Tools = level3Tools;
        _investigationStates = investigationStates;
    }

    /// <summary>Runs the genuine Level-3 iterative investigation loop. Its output is advisory;
    /// this endpoint has no dependency on the trust framework or payment adapter.</summary>
    [HttpPost("investigate/level3")]
    public async Task<IActionResult> InvestigateLevel3([FromBody] TransactionEventDto dto, CancellationToken cancellationToken)
    {
        if (!AgentFactory.IsLiveModeConfigured)
            return Problem("Level-3 investigation requires a configured OpenAI model.", statusCode: 503);

        var agent = new FinancialInvestigationAgent(AgentFactory.CreateLiveKernel(), _level3Tools, _investigationStates);
        var result = await agent.InvestigateAsync(dto.ToDomain(), cancellationToken);
        return Ok(result);
    }

    [HttpGet("investigations/{investigationId}")]
    public IActionResult GetInvestigation(string investigationId)
    {
        var state = _investigationStates.Find(investigationId);
        return state is null ? NotFound() : Ok(state);
    }

    /// <summary>Records a raw transaction event into history — the material later investigations
    /// and behaviour profiles are built from. Call this for every real transaction observed,
    /// independently of whether it goes through the trust layer.</summary>
    [HttpPost("events")]
    public IActionResult RecordEvent([FromBody] TransactionEventDto dto)
    {
        var domainEvent = dto.ToDomain();
        _eventStore.Record(domainEvent);
        return CreatedAtAction(nameof(GetCustomerProfile), new { customerId = domainEvent.CustomerId }, domainEvent);
    }

    [HttpGet("customers/{customerId}/profile")]
    public IActionResult GetCustomerProfile(string customerId)
    {
        var history = _eventStore.GetCustomerHistory(customerId);
        if (history.Count == 0)
        {
            return NotFound();
        }
        return Ok(BehaviourProfileBuilder.BuildCustomerProfile(customerId, history));
    }

    /// <summary>
    /// Long-term memory: takes a snapshot of the customer's current behaviour profile (built
    /// live from stored history) and persists it, so a future call can compare "now" against
    /// "then" instead of only ever holding one fixed lifetime baseline. Call this periodically
    /// (e.g. monthly) for customers you want behavioural-change detection on.
    /// </summary>
    [HttpPost("customers/{customerId}/profile/snapshot")]
    public IActionResult SnapshotCustomerProfile(string customerId)
    {
        var history = _eventStore.GetCustomerHistory(customerId);
        if (history.Count == 0)
        {
            return NotFound();
        }
        var profile = BehaviourProfileBuilder.BuildCustomerProfile(customerId, history);
        var takenAt = DateTimeOffset.UtcNow;
        _profileHistoryStore.RecordSnapshot(customerId, profile, takenAt);
        return CreatedAtAction(nameof(GetBehaviouralChange), new { customerId }, new { customerId, takenAt, profile });
    }

    /// <summary>
    /// Behavioural-change detection: compares the customer's current profile (built live from
    /// stored history) against the historical snapshot closest to AsOf — "behaviour should also
    /// change over time rather than assuming a person's historical behaviour remains permanently
    /// fixed." Returns 404 if no snapshot has ever been recorded for this customer
    /// (POST .../profile/snapshot first).
    /// </summary>
    [HttpGet("customers/{customerId}/behavioural-change")]
    public IActionResult GetBehaviouralChange(string customerId, [FromQuery] DateTimeOffset? asOf = null)
    {
        var history = _eventStore.GetCustomerHistory(customerId);
        if (history.Count == 0)
        {
            return NotFound();
        }

        var baseline = _profileHistoryStore.GetSnapshotClosestTo(customerId, asOf ?? DateTimeOffset.UtcNow);
        if (baseline is null)
        {
            return NotFound(new { error = "No profile snapshot recorded yet for this customer. POST .../profile/snapshot first." });
        }

        var current = BehaviourProfileBuilder.BuildCustomerProfile(customerId, history);
        var deviations = BehaviourDeviationService.CompareCustomerProfiles(baseline, current);
        return Ok(new { customerId, baseline, current, deviations });
    }

    /// <summary>
    /// Investigates a candidate transaction: builds the customer's behaviour profile from stored
    /// history, runs anomaly detection, and — only if the initial score is genuinely ambiguous —
    /// digs into the relationship graph built from the merchant's recent history before
    /// finalising a recommendation. Recording the event first (POST /events) is what gives this
    /// endpoint any history to reason over; a customer with no prior events gets a
    /// NO_BEHAVIOUR_HISTORY factor instead of a false "everything is normal."
    /// </summary>
    [HttpPost("investigate")]
    public IActionResult Investigate([FromBody] TransactionEventDto candidateDto)
    {
        var candidate = candidateDto.ToDomain();
        var merchantHistory = _eventStore.GetMerchantHistory(candidate.MerchantId);
        var graph = RelationshipAnalyzer.BuildGraph(merchantHistory.Append(candidate));

        var result = _investigationPlanner.Investigate(candidate, graph);

        return Ok(new
        {
            transactionId = candidate.TransactionId,
            initialAssessment = result.InitialAssessment,
            steps = result.Steps,
            finalAssessment = result.FinalAssessment
        });
    }

    /// <summary>
    /// Investigates a merchant's own history, split at CutoffDate into a baseline window and a
    /// recent window — the doc's "150 tx/day -> suddenly 4,300 tx/day" pattern. Requires events
    /// for that merchant to already be recorded (POST /events) on both sides of the cutoff.
    /// </summary>
    [HttpPost("merchants/{merchantId}/investigate")]
    public IActionResult InvestigateMerchant(string merchantId, [FromBody] MerchantInvestigateRequest request)
    {
        var allHistory = _eventStore.GetMerchantHistory(merchantId);
        var baseline = allHistory.Where(e => e.Timestamp < request.CutoffDate).ToList();
        var recent = allHistory.Where(e => e.Timestamp >= request.CutoffDate).ToList();

        if (baseline.Count == 0 || recent.Count == 0)
        {
            return BadRequest(new { error = "Need recorded events for this merchant on both sides of CutoffDate." });
        }

        var settlementAccounts = request.SettlementAccountId is null
            ? null
            : new Dictionary<string, string> { [merchantId] = request.SettlementAccountId };

        var assessment = _merchantInvestigationAgent.Investigate(
            merchantId, baseline, recent, request.BaselineObservationDays, request.RecentObservationDays, settlementAccounts);

        return Ok(assessment);
    }

    /// <summary>Records what actually happened for a past AI recommendation — the feedback loop
    /// that lets ModelEvaluation measure whether the intelligence layer is getting better or
    /// worse at flagging genuinely suspicious activity.</summary>
    [HttpPost("feedback")]
    public IActionResult RecordFeedback([FromBody] FeedbackRequestDto dto)
    {
        if (!Enum.TryParse<IntelligenceRecommendation>(dto.AiRecommendation, ignoreCase: true, out var recommendation) ||
            !Enum.TryParse<ActualOutcome>(dto.ActualOutcome, ignoreCase: true, out var outcome))
        {
            return BadRequest(new { error = "AiRecommendation must be Approve/Escalate; ActualOutcome must be Legitimate/Suspicious." });
        }

        var feedback = new DecisionFeedback(dto.TransactionId, recommendation, outcome, dto.Notes, DateTimeOffset.UtcNow);
        _outcomeStore.Record(feedback);
        return CreatedAtAction(nameof(GetModelEvaluation), null, feedback);
    }

    [HttpGet("model-evaluation")]
    public IActionResult GetModelEvaluation() => Ok(ModelEvaluation.Evaluate(_outcomeStore.GetAll()));
}
