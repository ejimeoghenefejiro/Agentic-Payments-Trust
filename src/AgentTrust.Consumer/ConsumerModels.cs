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
public sealed record ConsumerRecipient(string RecipientId, string PrincipalId, string Name, string? Relationship,
    string DeliveryAddress, string? Email, string? Phone, bool DeliveryConsent,
    IReadOnlyList<string> AllowedCategories, IReadOnlyList<string> ExcludedCategories,
    IReadOnlyList<string> PreferredMerchants, IReadOnlyList<string> ExcludedMerchants,
    bool AllowSubstitutions, bool NotifyRecipient, string PayerNotification,
    bool Active, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version = 1);
public sealed record ConnectedService(string Id, string PrincipalId, string Provider,
    string ExternalAccountReference, string ConnectionType, string? CredentialReference,
    ConnectedServiceStatus Status, IReadOnlySet<string> Capabilities, DateTimeOffset CreatedAt,
    DateTimeOffset? LastVerifiedAt);
public sealed record PurchasePreference(string DeliveryAddressReference, string? RequestedDeliveryWindow,
    SubstitutionPolicy Substitutions, IReadOnlyDictionary<string, string> DeliveryPreferences);
public sealed record ShoppingListItem(string SearchTerm, int Quantity, string? PreferredProductId = null,
    decimal? MaximumUnitPrice = null, bool RequiredForOutcome = true);
public sealed record ConsumerPurchaseTask(string TaskId, string PrincipalId, string AgentId,
    IReadOnlySet<string> MerchantScope, string Schedule, string Timezone, decimal MaximumAmount,
    string Currency, IReadOnlyList<ShoppingListItem> ShoppingList, PurchasePreference Preferences,
    string MandateId, string PaymentMethodId, ConsumerTaskStatus Status, DateTimeOffset NextExecutionAt,
    DateTimeOffset CreatedAt, string? RecipientId = null);

public static class ConsumerTaskSchedule
{
    public static bool HasEnded(ConsumerPurchaseTask task, DateTimeOffset asOf)
    {
        if (!task.Preferences.DeliveryPreferences.TryGetValue("endDate", out var value)
            || !DateTimeOffset.TryParse(value, out var endDate)) return false;
        return endDate.Date < asOf.Date;
    }
}
public sealed record PurchaseExecution(string ExecutionId, string TaskId, string PrincipalId,
    string PurchaseIntentId, PurchaseExecutionState State, string? TransactionId,
    string? ProviderReference, string? RequiredAction, IReadOnlyList<string> Reasons,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ConsumerPlanningConversation(string ConversationId,string PrincipalId,string Objective,string Status,
    string StateJson,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,long Version=1);
public sealed record ConsumerPlanningTurn(string TurnId,string ConversationId,int Sequence,string Role,string Kind,
    string Content,string? ToolName,string? ToolInputJson,string? ToolOutputJson,DateTimeOffset CreatedAt);
public sealed record ConsumerProductReservation(string ReservationId,string ConversationId,string ProductId,int Quantity,
    decimal UnitPrice,string Currency,string Status,DateTimeOffset ReservedAt,DateTimeOffset ExpiresAt,long Version=1);
public sealed record ConversationPolicy(string PrincipalId,string InteractionMode,bool AskBeforeSubstitutions,
    bool ShowBasketBeforePayment,DateTimeOffset UpdatedAt,long Version=1);
public sealed record ShoppingDeliveryPolicy(string PrincipalId,string? DeliveryAddress,string? Postcode,
    IReadOnlyList<string> PreferredMerchants,IReadOnlyList<string> ExcludedMerchants,decimal MaximumDistanceMiles,
    bool AllowAlternativeMerchants,decimal MaximumAdditionalDeliveryCost,bool AllowSplitOrders,
    bool AllowCrossBrandSubstitutions,bool AskBeforeNonPreferredMerchant,DateTimeOffset UpdatedAt,long Version=1);
public interface IConsumerPlanningStore
{
    ConsumerPlanningConversation Create(string principalId,string objective,string stateJson,DateTimeOffset now);
    ConsumerPlanningConversation? FindOwned(string conversationId,string principalId);
    ConsumerPlanningConversation? FindLatestOpen(string principalId,DateTimeOffset notBefore);
    void Save(ConsumerPlanningConversation conversation);
    void Append(ConsumerPlanningTurn turn);
    IReadOnlyList<ConsumerPlanningTurn> Turns(string conversationId);
    void ReplaceReservations(string conversationId,IReadOnlyList<ConsumerProductReservation> reservations);
    IReadOnlyList<ConsumerProductReservation> Reservations(string conversationId);
    IReadOnlyDictionary<string,string> Preferences(string principalId);
    void Remember(string principalId,string key,string value,string sourceConversationId,DateTimeOffset now);
    ConversationPolicy GetPolicy(string principalId);
    void SavePolicy(ConversationPolicy policy);
    ShoppingDeliveryPolicy GetShoppingDeliveryPolicy(string principalId);
    void SaveShoppingDeliveryPolicy(ShoppingDeliveryPolicy policy);
}

public interface IConsumerTaskStore
{
    void Save(ConsumerPurchaseTask task);
    ConsumerPurchaseTask? FindOwned(string taskId, string principalId);
    IReadOnlyList<ConsumerPurchaseTask> FindByPrincipal(string principalId);
    IReadOnlyList<ConsumerPurchaseTask> FindDue(DateTimeOffset asOf, int maximum = 50);
}
public interface IConsumerRecipientStore
{
    void Save(ConsumerRecipient recipient);
    ConsumerRecipient? FindOwned(string recipientId, string principalId);
    IReadOnlyList<ConsumerRecipient> FindByPrincipal(string principalId);
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
    public IReadOnlyList<ConsumerPurchaseTask> FindDue(DateTimeOffset asOf, int maximum = 50) { lock (_gate) return _items.Values.Where(t => t.Status == ConsumerTaskStatus.Active && t.NextExecutionAt <= asOf && !ConsumerTaskSchedule.HasEnded(t, asOf)).OrderBy(t => t.NextExecutionAt).Take(maximum).ToList(); }
}
public sealed class InMemoryConsumerRecipientStore : IConsumerRecipientStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ConsumerRecipient> _items = new();
    public void Save(ConsumerRecipient recipient) { lock (_gate) _items[recipient.RecipientId] = recipient; }
    public ConsumerRecipient? FindOwned(string id, string principal) { lock (_gate) return _items.GetValueOrDefault(id) is { } x && x.PrincipalId == principal ? x : null; }
    public IReadOnlyList<ConsumerRecipient> FindByPrincipal(string principal) { lock (_gate) return _items.Values.Where(x => x.PrincipalId == principal).OrderBy(x => x.Name).ToList(); }
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
public sealed class InMemoryConsumerPlanningStore:IConsumerPlanningStore
{
    private readonly object _gate=new();private readonly Dictionary<string,ConsumerPlanningConversation> _conversations=new();private readonly List<ConsumerPlanningTurn> _turns=[];private readonly List<ConsumerProductReservation> _reservations=[];
    public ConsumerPlanningConversation Create(string principal,string objective,string state,DateTimeOffset now){lock(_gate){var item=new ConsumerPlanningConversation($"conversation_{Guid.NewGuid():N}",principal,objective,"INVESTIGATING",state,now,now);_conversations[item.ConversationId]=item;return item;}}
    public ConsumerPlanningConversation? FindOwned(string id,string principal){lock(_gate)return _conversations.GetValueOrDefault(id)is{} x&&x.PrincipalId==principal?x:null;}
    public ConsumerPlanningConversation? FindLatestOpen(string principal,DateTimeOffset notBefore){lock(_gate)return _conversations.Values.Where(x=>x.PrincipalId==principal&&x.UpdatedAt>=notBefore&&x.Status is "INVESTIGATING" or "NEEDS_INPUT" or "PROPOSE").OrderByDescending(x=>x.UpdatedAt).FirstOrDefault();}
    public void Save(ConsumerPlanningConversation item){lock(_gate)_conversations[item.ConversationId]=item;}
    public void Append(ConsumerPlanningTurn turn){lock(_gate){if(_turns.All(x=>x.TurnId!=turn.TurnId))_turns.Add(turn);}}
    public IReadOnlyList<ConsumerPlanningTurn> Turns(string id){lock(_gate)return _turns.Where(x=>x.ConversationId==id).OrderBy(x=>x.Sequence).ToList();}
    public void ReplaceReservations(string id,IReadOnlyList<ConsumerProductReservation> rows){lock(_gate){_reservations.RemoveAll(x=>x.ConversationId==id);_reservations.AddRange(rows);}}
    public IReadOnlyList<ConsumerProductReservation> Reservations(string id){lock(_gate)return _reservations.Where(x=>x.ConversationId==id).ToList();}
    private readonly Dictionary<(string Principal,string Key),string> _preferences=new();private readonly Dictionary<string,ConversationPolicy> _policies=new();private readonly Dictionary<string,ShoppingDeliveryPolicy> _shoppingPolicies=new();
    public IReadOnlyDictionary<string,string> Preferences(string principal){lock(_gate)return _preferences.Where(x=>x.Key.Principal==principal).ToDictionary(x=>x.Key.Key,x=>x.Value);}
    public void Remember(string principal,string key,string value,string source,DateTimeOffset now){lock(_gate)_preferences[(principal,key)]=value;}
    public ConversationPolicy GetPolicy(string principal){lock(_gate)return _policies.GetValueOrDefault(principal)??new(principal,"AUTO_WHEN_SAFE",false,false,DateTimeOffset.UtcNow);}
    public void SavePolicy(ConversationPolicy policy){lock(_gate)_policies[policy.PrincipalId]=policy;}
    public ShoppingDeliveryPolicy GetShoppingDeliveryPolicy(string principal){lock(_gate)return _shoppingPolicies.GetValueOrDefault(principal)??new(principal,null,null,[],[],5,true,0,false,true,true,DateTimeOffset.UtcNow);}
    public void SaveShoppingDeliveryPolicy(ShoppingDeliveryPolicy policy){lock(_gate)_shoppingPolicies[policy.PrincipalId]=policy;}
}
