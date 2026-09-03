using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentTrust.Consumer;

public enum ConnectedServiceStatus { Active, Suspended, Revoked }
public enum ConsumerTaskStatus { Active, Paused, Cancelled }
public enum SubstitutionPolicy { Never, SameOrLowerPrice, AllowConfigured }
public enum PurchaseExecutionState
{
    Created, BasketBuilding, Quoted, AwaitingTrustDecision, Denied, AwaitingHumanApproval,
    Authorised, CheckoutSubmitted, RequiresAction, Processing, Purchased, Failed, Cancelled, Unknown
}

public sealed record ConsumerProfile(string PrincipalId, string DisplayName, string Timezone, DateTimeOffset CreatedAt);
public sealed record ConnectedService(string Id, string PrincipalId, string Provider,
    string ExternalAccountReference, string ConnectionType, string? CredentialReference,
    ConnectedServiceStatus Status, IReadOnlySet<string> Capabilities, DateTimeOffset CreatedAt,
    DateTimeOffset? LastVerifiedAt);
public sealed record PurchasePreference(string DeliveryAddressReference, string? RequestedDeliveryWindow,
    SubstitutionPolicy Substitutions, IReadOnlyDictionary<string, string> DeliveryPreferences);
public sealed record ShoppingListItem(string SearchTerm, int Quantity, string? PreferredProductId = null,
    decimal? MaximumUnitPrice = null);
public sealed record ConsumerPurchaseTask(string TaskId, string PrincipalId, string AgentId,
    IReadOnlySet<string> MerchantScope, string Schedule, string Timezone, decimal MaximumAmount,
    string Currency, IReadOnlyList<ShoppingListItem> ShoppingList, PurchasePreference Preferences,
    string MandateId, string PaymentMethodId, ConsumerTaskStatus Status, DateTimeOffset NextExecutionAt,
    DateTimeOffset CreatedAt);
public sealed record PurchaseExecution(string ExecutionId, string TaskId, string PrincipalId,
    string PurchaseIntentId, PurchaseExecutionState State, string? TransactionId,
    string? ProviderReference, string? RequiredAction, IReadOnlyList<string> Reasons,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public interface IConsumerTaskStore
{
    void Save(ConsumerPurchaseTask task);
    ConsumerPurchaseTask? FindOwned(string taskId, string principalId);
    IReadOnlyList<ConsumerPurchaseTask> FindByPrincipal(string principalId);
    IReadOnlyList<ConsumerPurchaseTask> FindDue(DateTimeOffset asOf, int maximum = 50);
}
public interface IConnectedServiceStore
{
    void Save(ConnectedService service);
    IReadOnlyList<ConnectedService> FindByPrincipal(string principalId);
}
public interface IPurchaseExecutionStore
{
    void Save(PurchaseExecution execution);
    PurchaseExecution? FindOwned(string executionId, string principalId);
    PurchaseExecution? FindByIntent(string purchaseIntentId);
    IReadOnlyList<PurchaseExecution> FindByPrincipal(string principalId);
}

public sealed class InMemoryConsumerTaskStore : IConsumerTaskStore
{
    private readonly object _gate = new(); private readonly Dictionary<string, ConsumerPurchaseTask> _items = new();
    public void Save(ConsumerPurchaseTask task) { lock (_gate) _items[task.TaskId] = task; }
    public ConsumerPurchaseTask? FindOwned(string id, string principal) { lock (_gate) return _items.GetValueOrDefault(id) is { } t && t.PrincipalId == principal ? t : null; }
    public IReadOnlyList<ConsumerPurchaseTask> FindByPrincipal(string principal) { lock (_gate) return _items.Values.Where(t => t.PrincipalId == principal).ToList(); }
    public IReadOnlyList<ConsumerPurchaseTask> FindDue(DateTimeOffset asOf, int maximum = 50) { lock (_gate) return _items.Values.Where(t => t.Status == ConsumerTaskStatus.Active && t.NextExecutionAt <= asOf).OrderBy(t => t.NextExecutionAt).Take(maximum).ToList(); }
}
public sealed class InMemoryConnectedServiceStore : IConnectedServiceStore
{
    private readonly object _gate = new(); private readonly Dictionary<string, ConnectedService> _items = new();
    public void Save(ConnectedService item) { lock (_gate) _items[item.Id] = item; }
    public IReadOnlyList<ConnectedService> FindByPrincipal(string principal) { lock (_gate) return _items.Values.Where(x => x.PrincipalId == principal).ToList(); }
}
public sealed class InMemoryPurchaseExecutionStore : IPurchaseExecutionStore
{
    private readonly object _gate = new(); private readonly Dictionary<string, PurchaseExecution> _items = new();
    public void Save(PurchaseExecution item) { lock (_gate) _items[item.ExecutionId] = item; }
    public PurchaseExecution? FindOwned(string id, string principal) { lock (_gate) return _items.GetValueOrDefault(id) is { } x && x.PrincipalId == principal ? x : null; }
    public PurchaseExecution? FindByIntent(string intent) { lock (_gate) return _items.Values.FirstOrDefault(x => x.PurchaseIntentId == intent); }
    public IReadOnlyList<PurchaseExecution> FindByPrincipal(string principal) { lock (_gate) return _items.Values.Where(x => x.PrincipalId == principal).ToList(); }
}
