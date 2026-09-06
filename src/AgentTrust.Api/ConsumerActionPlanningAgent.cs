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

public interface IProviderPlanningCapability
{
    string ProviderId { get; }
    IReadOnlySet<string> Capabilities { get; }
    bool CanHandle(ConsumerActionPlanningContext context);
    Task<ConsumerPurchasePlan> PlanAsync(ConsumerActionPlanningContext context, CancellationToken cancellationToken);
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
    private readonly IReadOnlyList<IProviderPlanningCapability> _capabilities;

    public ConsumerPurchaseRequestAgent(IEnumerable<IProviderPlanningCapability> capabilities) =>
        _capabilities = capabilities.ToArray();

    public Task<ConsumerPurchasePlan> PlanAsync(ConsumerActionPlanningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Instruction))
            throw new ArgumentException("A consumer instruction is required.", nameof(context));

        var capability = _capabilities.FirstOrDefault(candidate => candidate.CanHandle(context));
        if (capability is null)
            throw new InvalidOperationException($"Provider '{context.ProviderId}' has no registered planning capability for this objective.");

        return capability.PlanAsync(context, cancellationToken);
    }
}

/// <summary>Grocery-specific adapter. Catalogue access and meal semantics stay behind this capability.</summary>
public sealed class GroceryProviderPlanningCapability : IProviderPlanningCapability
{
    private static readonly IReadOnlySet<string> RequiredCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "search_products", "get_quote"
    };

    private readonly MerchantConnectorRegistry _connectors;
    private readonly GroceryConsumerPurchasePlanner _planner;

    public GroceryProviderPlanningCapability(MerchantConnectorRegistry connectors, GroceryConsumerPurchasePlanner planner)
    {
        _connectors = connectors;
        _planner = planner;
    }

    public string ProviderId => "grocery";
    public IReadOnlySet<string> Capabilities => RequiredCapabilities;

    public bool CanHandle(ConsumerActionPlanningContext context) =>
        _connectors.TryGet(context.ProviderId, out _) &&
        RequiredCapabilities.All(required => context.AvailableCapabilities.Contains(required));

    public async Task<ConsumerPurchasePlan> PlanAsync(ConsumerActionPlanningContext context, CancellationToken cancellationToken)
    {
        var connector = _connectors.GetRequired(context.ProviderId);
        var catalogue = await connector.SearchProductsAsync(string.Empty, cancellationToken);
        return await _planner.PlanAsync(context.PrincipalId, context.ConversationId, context.Instruction, catalogue, cancellationToken);
    }
}
