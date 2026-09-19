using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentTrust.Commerce;

public enum CapabilityFamily { Discovery, Configuration, Quote, Reservation, Execution, Lifecycle, Evidence }
public sealed record CapabilityDescriptor(string Name, CapabilityFamily Family, string InputSchema, string OutputSchema);
public sealed record CapabilityInvocationResult(bool Success, JsonElement Value, string? Error = null);

/// <summary>Provider-neutral contract for products, reservations, journeys, bookings, and other services.</summary>
public interface IServiceConnector
{
    string ProviderId { get; }
    string ProviderName { get; }
    IReadOnlyCollection<CapabilityDescriptor> Capabilities { get; }
    Task<CapabilityInvocationResult> InvokeAsync(string capability, JsonElement input, CancellationToken cancellationToken = default);
}

public sealed class ServiceConnectorRegistry
{
    private readonly IReadOnlyDictionary<string, IServiceConnector> _providers;
    public ServiceConnectorRegistry(IEnumerable<IServiceConnector> providers) => _providers = providers.ToDictionary(x => x.ProviderId, StringComparer.OrdinalIgnoreCase);
    public bool TryGet(string id, out IServiceConnector provider) => _providers.TryGetValue(id, out provider!);
    public IServiceConnector GetRequired(string id) => _providers.TryGetValue(id, out var provider) ? provider : throw new KeyNotFoundException($"Service provider '{id}' is not registered.");
}

public sealed record ServiceActionProposal(string ProposalId, string PrincipalId, string AgentId, string ProviderId,
    string Action, string Objective, JsonElement Configuration, decimal MaximumAmount, string Currency, string IdempotencyKey);
public sealed record ServiceQuote(string QuoteId, string ProviderId, string Action, decimal Total, string Currency,
    DateTimeOffset ExpiresAt, JsonElement Details);
public sealed record ServiceTrustDecision(bool Approved, IReadOnlyList<string> Reasons, string PolicyVersion);
public sealed record ServiceActionAuthorisation(string AuthorisationId, string ProposalId, string QuoteId, string PrincipalId,
    string AgentId, string ProviderId, string Action, decimal Amount, string Currency, string PolicyVersion, DateTimeOffset ExpiresAt, string Signature);
public sealed record ServiceExecutionResult(bool Succeeded, string Status, string? ProviderReference, JsonElement Evidence, string? Error = null);
public sealed record ServicePlanningContext(string PrincipalId, string AgentId, string Instruction, string ProviderId);
public sealed record ServicePlan(ServiceActionProposal Proposal, ServiceQuote Quote, IReadOnlyList<string> CapabilitiesUsed);
public interface IServiceDomainCapability
{
    string DomainId { get; }
    bool CanHandle(IServiceConnector provider, ServicePlanningContext context);
    Task<ServicePlan> PlanAsync(IServiceConnector provider, ServicePlanningContext context, CancellationToken cancellationToken = default);
}

public sealed class ServicePlanningRouter
{
    private readonly ServiceConnectorRegistry _providers; private readonly IReadOnlyList<IServiceDomainCapability> _domains;
    public ServicePlanningRouter(ServiceConnectorRegistry providers, IEnumerable<IServiceDomainCapability> domains) { _providers = providers; _domains = domains.ToArray(); }
    public Task<ServicePlan> PlanAsync(ServicePlanningContext context, CancellationToken token = default)
    { var provider = _providers.GetRequired(context.ProviderId); var domain = _domains.FirstOrDefault(x => x.CanHandle(provider, context)) ?? throw new InvalidOperationException("No domain capability supports this provider objective."); return domain.PlanAsync(provider, context, token); }
}

public interface IServiceActionTrustPolicy { ServiceTrustDecision Evaluate(ServiceActionProposal proposal, ServiceQuote quote, DateTimeOffset now); }
public sealed class BoundedServiceActionTrustPolicy : IServiceActionTrustPolicy
{
    private readonly IReadOnlySet<string> _providers; private readonly IReadOnlySet<string> _actions;
    public BoundedServiceActionTrustPolicy(IReadOnlySet<string> providers, IReadOnlySet<string> actions) { _providers = providers; _actions = actions; }
    public ServiceTrustDecision Evaluate(ServiceActionProposal proposal, ServiceQuote quote, DateTimeOffset now)
    {
        var reasons = new List<string>();
        if (!_providers.Contains(proposal.ProviderId)) reasons.Add("PROVIDER_OUT_OF_SCOPE");
        if (!_actions.Contains(proposal.Action)) reasons.Add("ACTION_OUT_OF_SCOPE");
        if (quote.ProviderId != proposal.ProviderId || quote.Action != proposal.Action) reasons.Add("QUOTE_SCOPE_MISMATCH");
        if (!string.Equals(quote.Currency, proposal.Currency, StringComparison.OrdinalIgnoreCase)) reasons.Add("CURRENCY_MISMATCH");
        if (quote.Total > proposal.MaximumAmount) reasons.Add("AMOUNT_EXCEEDS_LIMIT");
        if (quote.ExpiresAt <= now) reasons.Add("QUOTE_EXPIRED");
        return new(reasons.Count == 0, reasons, "service-policy-v1");
    }
}

public interface IServiceActionAuthorisationService
{
    ServiceActionAuthorisation Issue(ServiceActionProposal proposal, ServiceQuote quote, string policyVersion, DateTimeOffset now);
    bool Verify(ServiceActionAuthorisation authorisation, DateTimeOffset now);
}
public sealed class HmacServiceActionAuthorisationService : IServiceActionAuthorisationService
{
    private readonly byte[] _key; public HmacServiceActionAuthorisationService(byte[] key) => _key = key.Length >= 32 ? key : throw new ArgumentException("At least 256 bits are required.");
    public ServiceActionAuthorisation Issue(ServiceActionProposal p, ServiceQuote q, string policyVersion, DateTimeOffset now)
    { var expires = now.AddMinutes(5); var unsigned = new ServiceActionAuthorisation($"saa_{Guid.NewGuid():N}", p.ProposalId, q.QuoteId, p.PrincipalId, p.AgentId, p.ProviderId, p.Action, q.Total, q.Currency, policyVersion, expires, ""); return unsigned with { Signature = Sign(unsigned) }; }
    public bool Verify(ServiceActionAuthorisation a, DateTimeOffset now)
    {
        if (a.ExpiresAt <= now) return false;

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(a.Signature),
                Convert.FromHexString(Sign(a)));
        }
        catch (FormatException)
        {
            return false;
        }
    }
    private string Sign(ServiceActionAuthorisation a) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(string.Join('|', a.AuthorisationId, a.ProposalId, a.QuoteId, a.PrincipalId, a.AgentId, a.ProviderId, a.Action, a.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), a.Currency, a.PolicyVersion, a.ExpiresAt.ToUnixTimeSeconds()))));
}

public sealed record TrustedServiceActionResult(ServiceTrustDecision TrustDecision, ServiceActionAuthorisation? Authorisation, ServiceExecutionResult? Execution);
public sealed class TrustedServiceActionOrchestrator
{
    private readonly IServiceActionTrustPolicy _policy; private readonly IServiceActionAuthorisationService _authorisations;
    public TrustedServiceActionOrchestrator(IServiceActionTrustPolicy policy, IServiceActionAuthorisationService authorisations) { _policy = policy; _authorisations = authorisations; }
    public async Task<TrustedServiceActionResult> ExecuteAsync(ServiceActionProposal proposal, ServiceQuote quote, IServiceConnector provider, DateTimeOffset now, CancellationToken token = default,
        Func<ServiceActionProposal, ServiceQuote, ServiceActionAuthorisation, string, CancellationToken, Task<ServiceExecutionResult?>>? beforeExecution = null)
    {
        if (provider.ProviderId != proposal.ProviderId) throw new InvalidOperationException("Provider does not match proposal.");
        var decision = _policy.Evaluate(proposal, quote, now); if (!decision.Approved) return new(decision, null, null);
        var authorisation = _authorisations.Issue(proposal, quote, decision.PolicyVersion, now);
        var reservation = await provider.InvokeAsync("reserve", JsonSerializer.SerializeToElement(new { quoteId = quote.QuoteId }), token);
        if (!reservation.Success) return new(decision, authorisation, new(false, "Failed", null, reservation.Value, reservation.Error));
        var reservationId = reservation.Value.GetProperty("reservationId").GetString()
            ?? throw new InvalidOperationException("Provider returned an invalid reservation.");
        if (beforeExecution is not null
            && await beforeExecution(proposal, quote, authorisation, reservationId, token) is { } blocked)
        {
            if (!string.Equals(blocked.Status, "PaymentProcessing", StringComparison.OrdinalIgnoreCase))
            {
                await provider.InvokeAsync(
                    "release",
                    JsonSerializer.SerializeToElement(new { reservationId }),
                    token);
            }

            return new(decision, authorisation, blocked);
        }
        var request = JsonSerializer.SerializeToElement(new { proposal, quote, authorisation, reservationId });
        var response = await provider.InvokeAsync(proposal.Action, request, token);
        var execution = response.Success ? new ServiceExecutionResult(true, "Confirmed", response.Value.TryGetProperty("providerReference", out var reference) ? reference.GetString() : null, response.Value) : new ServiceExecutionResult(false, "Failed", null, response.Value, response.Error);
        return new(decision, authorisation, execution);
    }
}
