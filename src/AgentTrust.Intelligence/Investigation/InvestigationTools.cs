using System.ComponentModel;
using System.Text.Json;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Graph;
using AgentTrust.Intelligence.Risk;
using Microsoft.SemanticKernel;

namespace AgentTrust.Intelligence.Investigation;

/// <summary>Bounded Level-3 analytical tools. None can authorise or move money.</summary>
public sealed class InvestigationTools
{
    public static readonly IReadOnlySet<string> AllowedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "GetCustomerHistory", "GetMerchantHistory", "GetDeviceHistory", "GetBeneficiaryHistory",
        "CalculateBehaviourProfile", "DetectAnomalies", "AnalyseFinancialGraph", "ComparePeerGroup",
        "GetPreviousHumanReviews", "SearchHistoricalCases", "RetrieveEvidence", "CalculateRiskSignals"
    };
    private static readonly IReadOnlyDictionary<string, string> CanonicalToolNames = BuildCanonicalToolNames();

    private readonly ITransactionEventStore _events;
    private readonly TransactionRiskEngine _riskEngine;
    private readonly IInvestigationMemory _memory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public InvestigationTools(ITransactionEventStore events, TransactionRiskEngine riskEngine, IInvestigationMemory? memory = null)
    {
        _events = events;
        _riskEngine = riskEngine;
        _memory = memory ?? new InMemoryInvestigationMemory();
    }

    public string Execute(string tool, IReadOnlyDictionary<string, string> arguments, TransactionEvent candidate)
    {
        InvestigationSecurityPolicy.ValidateArguments(arguments);
        var canonicalTool = CanonicalizeToolName(tool)
            ?? throw new InvalidOperationException($"Tool '{tool}' is not on the Level-3 investigation allow-list.");
        var raw = canonicalTool switch
        {
            "GetCustomerHistory" => GetCustomerHistory(ScopedArg(arguments, "customerId", candidate.CustomerId)),
            "GetMerchantHistory" => GetMerchantHistory(ScopedArg(arguments, "merchantId", candidate.MerchantId)),
            "GetDeviceHistory" => GetDeviceHistory(ScopedArg(arguments, "deviceId", candidate.DeviceId)),
            "GetBeneficiaryHistory" => GetBeneficiaryHistory(ScopedArg(arguments, "beneficiaryId", candidate.BeneficiaryId ?? "")),
            "CalculateBehaviourProfile" => CalculateBehaviourProfile(ScopedArg(arguments, "customerId", candidate.CustomerId)),
            "DetectAnomalies" => DetectAnomalies(JsonSerializer.Serialize(candidate, JsonOptions)),
            "AnalyseFinancialGraph" => AnalyseFinancialGraph(ScopedArg(arguments, "merchantId", candidate.MerchantId)),
            "ComparePeerGroup" => ComparePeerGroup(ScopedArg(arguments, "merchantId", candidate.MerchantId)),
            "GetPreviousHumanReviews" => GetPreviousHumanReviews(ScopedArg(arguments, "customerId", candidate.CustomerId)),
            "SearchHistoricalCases" => SearchHistoricalCases(Arg(arguments, "query", candidate.MerchantId), candidate.CustomerId),
            "RetrieveEvidence" => RetrieveScopedEvidence(RequiredArg(arguments, "evidenceId"), candidate),
            "CalculateRiskSignals" => CalculateRiskSignals(JsonSerializer.Serialize(candidate, JsonOptions)),
            _ => throw new InvalidOperationException($"Tool '{tool}' is not on the Level-3 investigation allow-list.")
        };
        return Json(new { Classification = "UNTRUSTED_TOOL_OUTPUT", SourceTool = canonicalTool, Payload = JsonDocument.Parse(raw).RootElement.Clone() });
    }

    /// <summary>Maps harmless spelling/casing variants to one audited allow-list identity.</summary>
    public static string? CanonicalizeToolName(string? tool) =>
        !string.IsNullOrWhiteSpace(tool) && CanonicalToolNames.TryGetValue(tool.Trim(), out var canonical)
            ? canonical : null;

    private static IReadOnlyDictionary<string, string> BuildCanonicalToolNames()
    {
        var names = AllowedToolNames.ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);
        names["AnalyzeFinancialGraph"] = "AnalyseFinancialGraph";
        return names;
    }

    [KernelFunction("get_customer_history")]
    [Description("Returns prior transactions for a customer.")]
    public string GetCustomerHistory(string customerId) => Json(_events.GetCustomerHistory(customerId));

    [KernelFunction("get_merchant_history")]
    [Description("Returns prior transactions for a merchant.")]
    public string GetMerchantHistory(string merchantId) => Json(_events.GetMerchantHistory(merchantId));

    [KernelFunction("get_device_history")]
    [Description("Returns transactions previously associated with a device.")]
    public string GetDeviceHistory(string deviceId) => Json(_events.GetDeviceHistory(deviceId));

    [KernelFunction("get_beneficiary_history")]
    [Description("Returns transactions previously associated with a beneficiary.")]
    public string GetBeneficiaryHistory(string beneficiaryId) => Json(_events.GetBeneficiaryHistory(beneficiaryId));

    [KernelFunction("calculate_behaviour_profile")]
    [Description("Calculates the customer's behavioural baseline from structured history.")]
    public string CalculateBehaviourProfile(string customerId) =>
        Json(BehaviourProfileBuilder.BuildCustomerProfile(customerId, _events.GetCustomerHistory(customerId)));

    [KernelFunction("detect_anomalies")]
    [Description("Runs the existing deterministic anomaly detectors and returns their signals.")]
    public string DetectAnomalies(string candidateTransactionJson)
    {
        var candidate = ParseCandidate(candidateTransactionJson);
        return Json(Assess(candidate).RiskFactors);
    }

    [KernelFunction("analyse_financial_graph")]
    [Description("Analyses merchant relationships for shared-device and community patterns.")]
    public string AnalyseFinancialGraph(string merchantId)
    {
        var history = _events.GetMerchantHistory(merchantId);
        var graph = RelationshipAnalyzer.BuildGraph(history);
        return Json(new { Nodes = graph.Nodes.Count, Edges = graph.Edges.Count,
            SharedDevices = RelationshipAnalyzer.FindSharedDevicesForMerchant(graph, merchantId),
            CommunityRisk = CommunityRiskAnalyzer.AnalyzeMerchant(graph, merchantId) });
    }

    [KernelFunction("compare_peer_group")]
    [Description("Compares a merchant with other merchants observed for its customers.")]
    public string ComparePeerGroup(string merchantId)
    {
        var subjectHistory = _events.GetMerchantHistory(merchantId);
        var subject = BehaviourProfileBuilder.BuildMerchantProfile(merchantId, subjectHistory, ObservationDays(subjectHistory));
        var customerIds = subjectHistory.Select(e => e.CustomerId).Distinct();
        var peerIds = customerIds.SelectMany(_events.GetCustomerHistory).Select(e => e.MerchantId)
            .Where(id => !id.Equals(merchantId, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        var peers = peerIds.Select(id => { var h = _events.GetMerchantHistory(id); return BehaviourProfileBuilder.BuildMerchantProfile(id, h, ObservationDays(h)); }).ToList();
        return Json(PeerGroupComparator.CompareMerchantToPeers(subject, peers));
    }

    [KernelFunction("get_previous_human_reviews")]
    [Description("Returns previous analyst decisions and notes for a customer.")]
    public string GetPreviousHumanReviews(string customerId) => Json(_memory.GetPreviousHumanReviews(customerId));

    [KernelFunction("search_historical_cases")]
    [Description("Searches semantic case memory for relevant prior investigations.")]
    public string SearchHistoricalCases(string query) => Json(_memory.SearchHistoricalCases(query));

    private string SearchHistoricalCases(string query, string scopeId) => Json(
        _memory is IScopedInvestigationMemory scoped
            ? scoped.SearchHistoricalCases(query, scopeId)
            : _memory.SearchHistoricalCases(query));

    [KernelFunction("retrieve_evidence")]
    [Description("Retrieves trusted evidence by identifier.")]
    public string RetrieveEvidence(string evidenceId) => Json(_memory.RetrieveEvidence(evidenceId));

    [KernelFunction("calculate_risk_signals")]
    [Description("Runs the existing deterministic risk engine as an advisory signal tool.")]
    public string CalculateRiskSignals(string candidateTransactionJson) => Json(Assess(ParseCandidate(candidateTransactionJson)));

    private RiskAssessment Assess(TransactionEvent candidate)
    {
        var history = _events.GetCustomerHistory(candidate.CustomerId).Where(e => e.TransactionId != candidate.TransactionId).ToList();
        return _riskEngine.Assess(candidate, BehaviourProfileBuilder.BuildCustomerProfile(candidate.CustomerId, history), history);
    }
    private static string Json(object? value) => JsonSerializer.Serialize(value, JsonOptions);
    private static TransactionEvent ParseCandidate(string json) => JsonSerializer.Deserialize<TransactionEvent>(json, JsonOptions)
        ?? throw new ArgumentException("Could not parse candidate transaction JSON.");
    private static string Arg(IReadOnlyDictionary<string, string> args, string name, string fallback) =>
        args.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    private static string ScopedArg(IReadOnlyDictionary<string, string> args, string name, string expected)
    {
        var actual = Arg(args, name, expected);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Tool argument '{name}' is outside the candidate transaction scope.");
        return actual;
    }
    private string RetrieveScopedEvidence(string evidenceId, TransactionEvent candidate)
    {
        var evidence = _memory.RetrieveEvidence(evidenceId);
        if (evidence is null) return Json(null);
        var allowedSubjects = new[] { candidate.TransactionId, candidate.CustomerId, candidate.MerchantId,
            candidate.DeviceId, candidate.BeneficiaryId }.Where(s => s is not null);
        if (!allowedSubjects.Contains(evidence.SubjectId, StringComparer.Ordinal))
            throw new InvalidOperationException("Evidence does not belong to the candidate transaction scope.");
        return Json(evidence);
    }
    private static string RequiredArg(IReadOnlyDictionary<string, string> args, string name) =>
        args.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Tool argument '{name}' is required.");
    private static int ObservationDays(IReadOnlyList<TransactionEvent> history) => history.Count < 2 ? 1
        : Math.Max(1, (int)Math.Ceiling((history.Max(e => e.Timestamp) - history.Min(e => e.Timestamp)).TotalDays));
}
