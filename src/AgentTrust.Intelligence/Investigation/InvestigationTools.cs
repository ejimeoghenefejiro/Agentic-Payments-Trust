using System.ComponentModel;
using System.Text.Json;
using AgentTrust.Intelligence.Anomaly;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Risk;
using Microsoft.SemanticKernel;

namespace AgentTrust.Intelligence.Investigation;

/// <summary>
/// Exposes the investigation building blocks as Semantic Kernel functions — "give the agent
/// financial investigation tools" from the doc, rather than expecting an LLM to compute risk
/// itself. A real LLM-driven investigation agent can be built later by handing a Kernel loaded
/// with this plugin to a chat loop (the same pattern AgentTrust.Agents.SemanticKernelPaymentAgent
/// already uses for payment intents); InvestigationAgent.cs is the deterministic, no-LLM-cost
/// equivalent used by default and by every test in this repo.
/// </summary>
public sealed class InvestigationTools
{
    private readonly ITransactionEventStore _eventStore;
    private readonly TransactionRiskEngine _riskEngine;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public InvestigationTools(ITransactionEventStore eventStore, TransactionRiskEngine riskEngine)
    {
        _eventStore = eventStore;
        _riskEngine = riskEngine;
    }

    [KernelFunction("get_customer_history")]
    [Description("Returns the customer's prior transaction history as JSON.")]
    public string GetCustomerHistory(string customerId) =>
        JsonSerializer.Serialize(_eventStore.GetCustomerHistory(customerId), JsonOptions);

    [KernelFunction("get_merchant_history")]
    [Description("Returns the merchant's prior transaction history as JSON.")]
    public string GetMerchantHistory(string merchantId) =>
        JsonSerializer.Serialize(_eventStore.GetMerchantHistory(merchantId), JsonOptions);

    [KernelFunction("calculate_behaviour_profile")]
    [Description("Builds a customer behaviour profile (typical amount range, devices, locations, merchants, beneficiaries, time window) from history.")]
    public string CalculateBehaviourProfile(string customerId)
    {
        var history = _eventStore.GetCustomerHistory(customerId);
        var profile = BehaviourProfileBuilder.BuildCustomerProfile(customerId, history);
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    [KernelFunction("calculate_risk")]
    [Description("Runs the full investigation pipeline for a candidate transaction (JSON-encoded TransactionEvent) and returns a structured, evidence-backed RiskAssessment as JSON. This is a recommendation only — it never authorises a payment.")]
    public string CalculateRisk(string candidateTransactionJson)
    {
        var candidate = JsonSerializer.Deserialize<TransactionEvent>(candidateTransactionJson, JsonOptions)
            ?? throw new ArgumentException("Could not parse candidate transaction JSON.", nameof(candidateTransactionJson));

        var agent = new InvestigationAgent(_eventStore, _riskEngine);
        var assessment = agent.Investigate(candidate);
        return JsonSerializer.Serialize(assessment, JsonOptions);
    }
}
