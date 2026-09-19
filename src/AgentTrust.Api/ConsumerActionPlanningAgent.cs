using AgentTrust.Commerce;

namespace AgentTrust.Api;

/// <summary>Provider-neutral input to the consumer action planner.</summary>
public sealed record ConsumerActionPlanningContext(
    string PrincipalId,
    string? ConversationId,
    string Instruction,
    string ProviderId,
    string ProviderName,
    IReadOnlySet<string> AvailableCapabilities);

/// <summary>Resolves a provider and its advertised operations. It contains no domain reasoning.</summary>
public interface IProviderPlanningCapability
{
    bool CanHandle(ConsumerActionPlanningContext context);
    ProviderPlanningSession Open(ConsumerActionPlanningContext context);
}

public sealed record ProviderPlanningSession(string ProviderId,string ProviderName,IReadOnlySet<string> Capabilities,object Provider);

/// <summary>Reusable reasoning semantics, selected independently of the provider.</summary>
public interface IDomainPlanningCapability
{
    string DomainId { get; }
    bool CanHandle(ConsumerActionPlanningContext context,ProviderPlanningSession provider);
    Task<ConsumerPurchasePlan> PlanAsync(ConsumerActionPlanningContext context,ProviderPlanningSession provider,CancellationToken cancellationToken);
}

public interface IConsumerPurchaseRequestAgent
{
    Task<ConsumerPurchasePlan> PlanAsync(ConsumerActionPlanningContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Provider-neutral planner entry point. It selects a registered provider/domain capability and
/// never receives products, prices, delivery rules, currency rules, mandates or payment functions.
/// </summary>
public sealed class ConsumerPurchaseRequestAgent : IConsumerPurchaseRequestAgent
{
    private readonly IReadOnlyList<IProviderPlanningCapability> _providers;
    private readonly IReadOnlyList<IDomainPlanningCapability> _domains;

    public ConsumerPurchaseRequestAgent(IEnumerable<IProviderPlanningCapability> providers,IEnumerable<IDomainPlanningCapability> domains)
    { _providers=providers.ToArray();_domains=domains.ToArray(); }

    public Task<ConsumerPurchasePlan> PlanAsync(ConsumerActionPlanningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Instruction))
            throw new ArgumentException("A consumer instruction is required.", nameof(context));

        var resolver=_providers.FirstOrDefault(candidate=>candidate.CanHandle(context))
            ??throw new InvalidOperationException($"Provider '{context.ProviderId}' is not registered.");
        var provider=resolver.Open(context);
        var domain=_domains.FirstOrDefault(candidate=>candidate.CanHandle(context,provider))
            ??throw new InvalidOperationException($"No domain reasoner supports provider '{context.ProviderId}' and its capabilities.");
        return domain.PlanAsync(context,provider,cancellationToken);
    }
}

/// <summary>Generic connector adapter. It routes providers but contains no grocery semantics.</summary>
public sealed class CommerceConnectorPlanningCapability : IProviderPlanningCapability
{
    private readonly MerchantConnectorRegistry _connectors;
    public CommerceConnectorPlanningCapability(MerchantConnectorRegistry connectors)=>_connectors=connectors;
    public bool CanHandle(ConsumerActionPlanningContext context)=>_connectors.TryGet(context.ProviderId,out _);
    public ProviderPlanningSession Open(ConsumerActionPlanningContext context)
    {var connector=_connectors.GetRequired(context.ProviderId);return new(connector.MerchantId,connector.MerchantName,CommerceCapabilityCatalog.Describe(connector),connector);}
}

/// <summary>Shared grocery semantics usable by Tesco, Sainsbury's, or any compatible provider.</summary>
public sealed class GroceryDomainPlanningCapability:IDomainPlanningCapability
{
    private readonly GroceryConsumerPurchasePlanner _planner;
    public GroceryDomainPlanningCapability(GroceryConsumerPurchasePlanner planner)=>_planner=planner;
    public string DomainId=>"grocery";
    public bool CanHandle(ConsumerActionPlanningContext context,ProviderPlanningSession provider)=>
        provider.Provider is IProductSearchCapability&&provider.Capabilities.Contains("search_products")&&provider.Capabilities.Contains("get_quote");
    public async Task<ConsumerPurchasePlan> PlanAsync(ConsumerActionPlanningContext context,ProviderPlanningSession provider,CancellationToken cancellationToken)
    {
        var catalogue=await ((IProductSearchCapability)provider.Provider).SearchProductsAsync(string.Empty,cancellationToken);
        return await _planner.PlanAsync(context.PrincipalId, context.ConversationId, context.Instruction, catalogue, cancellationToken);
    }
}
