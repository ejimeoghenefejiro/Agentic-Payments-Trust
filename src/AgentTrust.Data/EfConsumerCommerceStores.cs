using System.Security.Cryptography;
using System.Text;
using System.Data;
using System.Text.Json;
using AgentTrust.Commerce;
using AgentTrust.Consumer;
using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Payments;
using AgentTrust.PaymentMethods;
using AgentTrust.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Data;

internal static class ConsumerStoreJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    internal static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)
        ?? throw new InvalidOperationException($"Persisted {typeof(T).Name} JSON is invalid.");
}

public sealed class EfConsumerTaskStore : IConsumerTaskStore
{
    private readonly AgentTrustDbContext _db;
    public EfConsumerTaskStore(AgentTrustDbContext db) => _db = db;
    public void Save(ConsumerPurchaseTask task)
    {
        var row = _db.ConsumerPurchaseTasks.SingleOrDefault(x => x.TaskId == task.TaskId);
        if (row is null) { row = new ConsumerPurchaseTaskEntity { TaskId = task.TaskId, Version = 1 }; _db.Add(row); }
        else row.Version++;
        row.PrincipalId = task.PrincipalId; row.AgentId = task.AgentId;
        row.MerchantScopeJson = ConsumerStoreJson.Write(task.MerchantScope); row.Schedule = task.Schedule;
        row.Timezone = task.Timezone; row.MaximumAmount = task.MaximumAmount; row.Currency = task.Currency;
        row.ShoppingListJson = ConsumerStoreJson.Write(task.ShoppingList); row.PreferencesJson = ConsumerStoreJson.Write(task.Preferences);
        row.MandateId = task.MandateId; row.PaymentMethodId = task.PaymentMethodId; row.Status = task.Status.ToString();
        row.NextExecutionAt = task.NextExecutionAt; row.CreatedAt = task.CreatedAt; row.UpdatedAt = DateTimeOffset.UtcNow;
        _db.SaveChanges();
    }
    public ConsumerPurchaseTask? FindOwned(string id, string principal) => Map(_db.ConsumerPurchaseTasks.AsNoTracking().SingleOrDefault(x => x.TaskId == id && x.PrincipalId == principal));
    public IReadOnlyList<ConsumerPurchaseTask> FindByPrincipal(string principal) => _db.ConsumerPurchaseTasks.AsNoTracking().Where(x => x.PrincipalId == principal).Select(x => x).AsEnumerable().Select(Map).OfType<ConsumerPurchaseTask>().ToList();
    public IReadOnlyList<ConsumerPurchaseTask> FindDue(DateTimeOffset asOf, int maximum = 50) => _db.ConsumerPurchaseTasks.AsNoTracking()
        .Where(x => x.Status == "Active" && x.NextExecutionAt <= asOf).OrderBy(x => x.NextExecutionAt).Take(maximum)
        .AsEnumerable().Select(Map).OfType<ConsumerPurchaseTask>().ToList();
    private static ConsumerPurchaseTask? Map(ConsumerPurchaseTaskEntity? x) => x is null ? null : new(x.TaskId, x.PrincipalId, x.AgentId,
        ConsumerStoreJson.Read<HashSet<string>>(x.MerchantScopeJson), x.Schedule, x.Timezone, x.MaximumAmount, x.Currency,
        ConsumerStoreJson.Read<List<ShoppingListItem>>(x.ShoppingListJson), ConsumerStoreJson.Read<PurchasePreference>(x.PreferencesJson),
        x.MandateId, x.PaymentMethodId, Enum.Parse<ConsumerTaskStatus>(x.Status), x.NextExecutionAt, x.CreatedAt);
}

public sealed class EfConnectedServiceStore : IConnectedServiceStore
{
    private readonly AgentTrustDbContext _db; public EfConnectedServiceStore(AgentTrustDbContext db) => _db = db;
    public void Save(ConnectedService item)
    {
        var x = _db.ConnectedServices.SingleOrDefault(v => v.Id == item.Id);
        if (x is null) { x = new ConnectedServiceEntity { Id = item.Id, Version = 1 }; _db.Add(x); } else x.Version++;
        x.PrincipalId=item.PrincipalId; x.Provider=item.Provider; x.ExternalAccountReference=item.ExternalAccountReference;
        x.ConnectionType=item.ConnectionType; x.CredentialReference=item.CredentialReference; x.Status=item.Status.ToString();
        x.CapabilitiesJson=ConsumerStoreJson.Write(item.Capabilities); x.CreatedAt=item.CreatedAt; x.LastVerifiedAt=item.LastVerifiedAt; _db.SaveChanges();
    }
    public IReadOnlyList<ConnectedService> FindByPrincipal(string principal) => _db.ConnectedServices.AsNoTracking().Where(x => x.PrincipalId == principal).AsEnumerable().Select(x =>
        new ConnectedService(x.Id,x.PrincipalId,x.Provider,x.ExternalAccountReference,x.ConnectionType,x.CredentialReference,
            Enum.Parse<ConnectedServiceStatus>(x.Status),ConsumerStoreJson.Read<HashSet<string>>(x.CapabilitiesJson),x.CreatedAt,x.LastVerifiedAt)).ToList();
}

public sealed class EfPurchaseExecutionStore : IPurchaseExecutionStore
{
    private readonly AgentTrustDbContext _db; public EfPurchaseExecutionStore(AgentTrustDbContext db) => _db = db;
    public void Save(PurchaseExecution item)
    {
        var x = _db.PurchaseExecutions.SingleOrDefault(v => v.ExecutionId == item.ExecutionId);
        if (x is null) { x = new PurchaseExecutionEntity { ExecutionId=item.ExecutionId, ScheduledFor=item.CreatedAt, Version=1 }; _db.Add(x); } else x.Version++;
        x.TaskId=item.TaskId; x.PrincipalId=item.PrincipalId; x.PurchaseIntentId=item.PurchaseIntentId; x.State=item.State.ToString();
        x.TransactionId=item.TransactionId; x.ProviderPaymentId=item.ProviderReference; x.RequiredAction=item.RequiredAction;
        x.ReasonsJson=ConsumerStoreJson.Write(item.Reasons); x.CreatedAt=item.CreatedAt; x.UpdatedAt=item.UpdatedAt; _db.SaveChanges();
    }
    public PurchaseExecution? FindOwned(string id,string principal)=>Map(_db.PurchaseExecutions.AsNoTracking().SingleOrDefault(x=>x.ExecutionId==id&&x.PrincipalId==principal));
    public PurchaseExecution? FindByIntent(string id)=>Map(_db.PurchaseExecutions.AsNoTracking().SingleOrDefault(x=>x.PurchaseIntentId==id));
    public IReadOnlyList<PurchaseExecution> FindByPrincipal(string principal)=>_db.PurchaseExecutions.AsNoTracking().Where(x=>x.PrincipalId==principal).AsEnumerable().Select(Map).OfType<PurchaseExecution>().ToList();
    private static PurchaseExecution? Map(PurchaseExecutionEntity? x)=>x is null?null:new(x.ExecutionId,x.TaskId,x.PrincipalId,x.PurchaseIntentId,
        Enum.Parse<PurchaseExecutionState>(x.State),x.TransactionId,x.ProviderPaymentId,x.RequiredAction,ConsumerStoreJson.Read<List<string>>(x.ReasonsJson),x.CreatedAt,x.UpdatedAt);
}

public sealed class EfPurchaseAuditSink : IPurchaseAuditSink
{
    private readonly AgentTrustDbContext _db; public EfPurchaseAuditSink(AgentTrustDbContext db)=>_db=db;
    public void Append(PurchaseAuditEvent item)
    {
        if (_db.PurchaseLifecycleEvents.Any(x=>x.EventId==item.EventId)) return;
        var previous=_db.PurchaseLifecycleEvents.OrderByDescending(x=>x.SequenceNumber).Select(x=>x.CurrentHash).FirstOrDefault()??"GENESIS";
        var metadata=ConsumerStoreJson.Write(item.Metadata);
        var current=PurchaseAuditHash.Compute(item,previous);
        _db.PurchaseLifecycleEvents.Add(new PurchaseLifecycleEventEntity{EventId=item.EventId,EventType=item.EventType,PurchaseIntentId=item.PurchaseIntentId,
            PrincipalId=item.PrincipalId,TransactionId=item.TransactionId,IntentHash=item.IntentHash,PreviousHash=previous,CurrentHash=current,MetadataJson=metadata,Timestamp=item.Timestamp});
        _db.SaveChanges();
    }
    public IReadOnlyList<PurchaseAuditEvent> Find(string id)=>_db.PurchaseLifecycleEvents.AsNoTracking().Where(x=>x.PurchaseIntentId==id).OrderBy(x=>x.SequenceNumber).AsEnumerable()
        .Select(x=>new PurchaseAuditEvent(x.EventId,x.EventType,x.PurchaseIntentId,x.PrincipalId,x.TransactionId,x.IntentHash,x.Timestamp,ConsumerStoreJson.Read<Dictionary<string,string>>(x.MetadataJson),x.PreviousHash,x.CurrentHash)).ToList();
}

public sealed class EfScheduledOccurrenceStore : IScheduledOccurrenceStore
{
    private readonly AgentTrustDbContext _db; public EfScheduledOccurrenceStore(AgentTrustDbContext db)=>_db=db;
    public bool TryClaim(string taskId,DateTimeOffset scheduledFor,DateTimeOffset claimedAt,out ScheduledOccurrence? occurrence)
    {
        var row=new TaskOccurrenceEntity{OccurrenceId=$"occ_{Guid.NewGuid():N}",TaskId=taskId,ScheduledFor=scheduledFor,Status="Claimed",ClaimedAt=claimedAt,
            LeaseExpiresAt=claimedAt.AddMinutes(5),CreatedAt=claimedAt,Version=1}; _db.TaskOccurrences.Add(row);
        try { _db.SaveChanges(); occurrence=new(row.OccurrenceId,taskId,scheduledFor,ScheduledOccurrenceStatus.Claimed,claimedAt); return true; }
        catch(DbUpdateException){_db.Entry(row).State=EntityState.Detached;occurrence=null;return false;}
    }
    public void Complete(string id,bool success){var x=_db.TaskOccurrences.SingleOrDefault(v=>v.OccurrenceId==id);if(x is null)return;x.Status=success?"Completed":"Failed";x.LeaseExpiresAt=null;x.Version++;_db.SaveChanges();}
}

public sealed class EfOneOffAuthorisationStore : IOneOffAuthorisationStore
{
    private readonly AgentTrustDbContext _db; public EfOneOffAuthorisationStore(AgentTrustDbContext db)=>_db=db;
    public void Save(OneOffAuthorisation item){var x=_db.OneOffAuthorisations.SingleOrDefault(v=>v.AuthorisationId==item.AuthorisationId);if(x is null){x=new(){AuthorisationId=item.AuthorisationId,Version=1};_db.Add(x);}else x.Version++;
        x.PurchaseIntentId=item.ExecutionId;x.MandateId=item.MandateId;x.MandateVersion=item.MandateVersion;x.TransactionFingerprint=item.TransactionFingerprint;x.MaximumAmount=item.Amount;x.Currency=item.Currency;x.MerchantId=item.Merchant;
        x.PaymentMethodReference=item.PaymentMethodId;x.ApprovedBy=item.Approver;x.ApprovedAt=item.CreatedAt;x.ExpiresAt=item.ExpiresAt;x.Status=item.Status.ToString();x.ConsumedAt=item.ConsumedAt;_db.SaveChanges();}
    public OneOffAuthorisation? Find(string id)=>Map(_db.OneOffAuthorisations.AsNoTracking().SingleOrDefault(x=>x.AuthorisationId==id));
    public bool TryConsume(string id,string fingerprint,DateTimeOffset now,out OneOffAuthorisation? consumed)
    {var x=_db.OneOffAuthorisations.SingleOrDefault(v=>v.AuthorisationId==id&&v.Status=="Active"&&v.ExpiresAt>=now&&v.TransactionFingerprint==fingerprint);if(x is null){consumed=null;return false;}x.Status="Consumed";x.ConsumedAt=now;x.Version++;try{_db.SaveChanges();consumed=Map(x);return true;}catch(DbUpdateConcurrencyException){consumed=null;return false;}}
    private static OneOffAuthorisation? Map(OneOffAuthorisationEntity? x)=>x is null?null:new(x.AuthorisationId,x.PurchaseIntentId,x.MandateId,x.MandateVersion,x.TransactionFingerprint,x.MaximumAmount,x.Currency,x.MerchantId,x.PaymentMethodReference,x.ApprovedBy,x.ApprovedAt,x.ExpiresAt,Enum.Parse<OneOffAuthorisationStatus>(x.Status),x.ConsumedAt);
}

public sealed class EfPaymentAttemptStore : IPaymentAttemptStore
{
    private readonly AgentTrustDbContext _db; public EfPaymentAttemptStore(AgentTrustDbContext db)=>_db=db;
    public PaymentAttempt? FindByIdempotencyKey(string key)=>Map(_db.ConsumerPaymentAttempts.AsNoTracking().SingleOrDefault(x=>x.PaymentIdempotencyKey==key));
    public void Save(PaymentAttempt item){var x=_db.ConsumerPaymentAttempts.SingleOrDefault(v=>v.PaymentIdempotencyKey==item.IdempotencyKey);if(x is null){x=new(){PaymentAttemptId=item.AttemptId,PaymentIdempotencyKey=item.IdempotencyKey,CheckoutExecutionId=item.TransactionId,PurchaseIntentId=item.TransactionId,ProviderPaymentMethodId="trust-framework",CreatedAt=item.CreatedAt,Version=1};_db.Add(x);}else x.Version++;
        x.LatestStatus=item.Status.ToString();x.ProviderPaymentId=item.Result?.ProviderReference;x.FailureCode=item.Result?.FailureReason;x.UpdatedAt=item.UpdatedAt;_db.SaveChanges();}
    private static PaymentAttempt? Map(ConsumerPaymentAttemptEntity? x)=>x is null?null:new(x.PaymentAttemptId,x.PurchaseIntentId,x.PaymentIdempotencyKey,Enum.Parse<PaymentAttemptStatus>(x.LatestStatus),
        x.ProviderPaymentId is null&&x.FailureCode is null?null:new PaymentResult(x.PurchaseIntentId,x.LatestStatus=="Captured"?PaymentStatus.Success:PaymentStatus.Failure,x.ProviderPaymentId??"",x.FailureCode),x.CreatedAt,x.UpdatedAt);
}

public sealed class EfMandateStore : IMandateStore
{
    private readonly AgentTrustDbContext _db; public EfMandateStore(AgentTrustDbContext db)=>_db=db;
    public void Save(FinancialMandate item)
    {
        using var tx=_db.Database.BeginTransaction();
        var active=_db.FinancialMandates.Where(x=>x.MandateId==item.MandateId&&x.Status=="Active"&&x.Version<item.Version).ToList();
        foreach(var old in active){old.Status="Superseded";old.ConcurrencyVersion++;}
        var x=_db.FinancialMandates.SingleOrDefault(v=>v.MandateId==item.MandateId&&v.Version==item.Version);
        if(x is null){x=new(){MandateId=item.MandateId,Version=item.Version,ConcurrencyVersion=1};_db.Add(x);}else x.ConcurrencyVersion++;
        x.PrincipalId=item.PrincipalId;x.AgentId=item.AgentId;x.Merchant=item.Merchant;x.Purpose=item.Purpose;x.PaymentMethodId=item.PaymentMethodId;
        x.PerTransactionLimit=item.PerTransactionLimit;x.DailyLimit=item.DailyLimit;x.WeeklyLimit=item.WeeklyLimit;x.MonthlyLimit=item.MonthlyLimit;x.Currency=item.Currency;
        x.TaskParametersJson=ConsumerStoreJson.Write(item.TaskParameters);x.AboveLimit=item.AboveLimit.ToString();x.Status=item.Status.ToString();x.SupersedesMandateId=item.SupersedesMandateId;
        x.CreatedAt=item.CreatedAt;x.EffectiveFrom=item.EffectiveFrom;x.ExpiresAt=item.ExpiresAt;_db.SaveChanges();tx.Commit();
    }
    public FinancialMandate? Find(string id)=>Map(_db.FinancialMandates.AsNoTracking().Where(x=>x.MandateId==id).OrderByDescending(x=>x.Version).FirstOrDefault());
    public FinancialMandate? FindVersion(string id,int version)=>Map(_db.FinancialMandates.AsNoTracking().SingleOrDefault(x=>x.MandateId==id&&x.Version==version));
    public IReadOnlyList<FinancialMandate> GetHistory(string id)=>_db.FinancialMandates.AsNoTracking().Where(x=>x.MandateId==id).OrderBy(x=>x.Version).AsEnumerable().Select(Map).OfType<FinancialMandate>().ToList();
    public IReadOnlyList<FinancialMandate> FindByAgent(string id)=>_db.FinancialMandates.AsNoTracking().Where(x=>x.AgentId==id).AsEnumerable().Select(Map).OfType<FinancialMandate>().ToList();
    public IReadOnlyList<FinancialMandate> FindByPrincipal(string id)=>_db.FinancialMandates.AsNoTracking().Where(x=>x.PrincipalId==id)
        .AsEnumerable().GroupBy(x=>x.MandateId).Select(g=>g.OrderByDescending(x=>x.Version).First()).Select(Map).OfType<FinancialMandate>().ToList();
    private static FinancialMandate? Map(FinancialMandateEntity? x)=>x is null?null:new(x.MandateId,x.PrincipalId,x.AgentId,x.Merchant,x.Purpose,x.PaymentMethodId,x.PerTransactionLimit,x.WeeklyLimit,x.MonthlyLimit,x.Currency,
        ConsumerStoreJson.Read<Dictionary<string,string>>(x.TaskParametersJson),Enum.Parse<AboveLimitAction>(x.AboveLimit),Enum.Parse<MandateStatus>(x.Status),x.CreatedAt,x.ExpiresAt)
        {Version=x.Version,DailyLimit=x.DailyLimit,EffectiveFrom=x.EffectiveFrom,SupersedesMandateId=x.SupersedesMandateId};
}

public sealed class EfPaymentMethodStore : IPaymentMethodStore
{
    private readonly AgentTrustDbContext _db; public EfPaymentMethodStore(AgentTrustDbContext db)=>_db=db;
    public void Save(PaymentMethod item){var x=_db.ConsumerPaymentMethods.SingleOrDefault(v=>v.PaymentMethodId==item.PaymentMethodId);if(x is null){x=new(){PaymentMethodId=item.PaymentMethodId,Version=1};_db.Add(x);}else x.Version++;
        x.PrincipalId=item.PrincipalId;x.Provider=item.Provider;x.ProviderToken=item.Token;x.CardBrand=item.CardBrand;x.Last4=item.Last4;x.ExpiryMonth=item.ExpiryMonth;x.ExpiryYear=item.ExpiryYear;x.Status=item.Status.ToString();_db.SaveChanges();}
    public PaymentMethod? Find(string id)=>Map(_db.ConsumerPaymentMethods.AsNoTracking().SingleOrDefault(x=>x.PaymentMethodId==id));
    public PaymentMethod? FindByProviderToken(string provider,string token)=>Map(_db.ConsumerPaymentMethods.AsNoTracking().SingleOrDefault(x=>x.Provider==provider&&x.ProviderToken==token));
    public IReadOnlyList<PaymentMethod> FindByPrincipal(string id)=>_db.ConsumerPaymentMethods.AsNoTracking().Where(x=>x.PrincipalId==id).AsEnumerable().Select(Map).OfType<PaymentMethod>().ToList();
    private static PaymentMethod? Map(ConsumerPaymentMethodEntity? x)=>x is null?null:new(x.PaymentMethodId,x.PrincipalId,x.Provider,x.ProviderToken,x.CardBrand,x.Last4,x.ExpiryMonth,x.ExpiryYear,Enum.Parse<PaymentMethodStatus>(x.Status));
}

public sealed class EfConsumerPlanningStore:IConsumerPlanningStore
{
    private readonly AgentTrustDbContext _db;public EfConsumerPlanningStore(AgentTrustDbContext db)=>_db=db;
    public ConsumerPlanningConversation Create(string principal,string objective,string state,DateTimeOffset now){var x=new ConsumerPlanningConversationEntity{ConversationId=$"conversation_{Guid.NewGuid():N}",PrincipalId=principal,Objective=objective,Status="INVESTIGATING",StateJson=state,CreatedAt=now,UpdatedAt=now,Version=1};_db.Add(x);_db.SaveChanges();return Map(x);}
    public ConsumerPlanningConversation? FindOwned(string id,string principal)=>_db.ConsumerPlanningConversations.AsNoTracking().SingleOrDefault(x=>x.ConversationId==id&&x.PrincipalId==principal)is{} x?Map(x):null;
    public void Save(ConsumerPlanningConversation item){var x=_db.ConsumerPlanningConversations.Single(v=>v.ConversationId==item.ConversationId);x.Status=item.Status;x.StateJson=item.StateJson;x.UpdatedAt=item.UpdatedAt;x.Version++;_db.SaveChanges();}
    public void Append(ConsumerPlanningTurn item){if(_db.ConsumerPlanningTurns.Any(x=>x.TurnId==item.TurnId))return;_db.Add(new ConsumerPlanningTurnEntity{TurnId=item.TurnId,ConversationId=item.ConversationId,Sequence=item.Sequence,Role=item.Role,Kind=item.Kind,Content=item.Content,ToolName=item.ToolName,ToolInputJson=item.ToolInputJson,ToolOutputJson=item.ToolOutputJson,CreatedAt=item.CreatedAt});_db.SaveChanges();}
    public IReadOnlyList<ConsumerPlanningTurn> Turns(string id)=>_db.ConsumerPlanningTurns.AsNoTracking().Where(x=>x.ConversationId==id).OrderBy(x=>x.Sequence).Select(x=>new ConsumerPlanningTurn(x.TurnId,x.ConversationId,x.Sequence,x.Role,x.Kind,x.Content,x.ToolName,x.ToolInputJson,x.ToolOutputJson,x.CreatedAt)).ToList();
    public void ReplaceReservations(string id,IReadOnlyList<ConsumerProductReservation> rows){var old=_db.ConsumerProductReservations.Where(x=>x.ConversationId==id);_db.RemoveRange(old);_db.AddRange(rows.Select(x=>new ConsumerProductReservationEntity{ReservationId=x.ReservationId,ConversationId=x.ConversationId,ProductId=x.ProductId,Quantity=x.Quantity,UnitPrice=x.UnitPrice,Currency=x.Currency,Status=x.Status,ReservedAt=x.ReservedAt,ExpiresAt=x.ExpiresAt,Version=x.Version}));_db.SaveChanges();}
    public IReadOnlyList<ConsumerProductReservation> Reservations(string id)=>_db.ConsumerProductReservations.AsNoTracking().Where(x=>x.ConversationId==id).Select(x=>new ConsumerProductReservation(x.ReservationId,x.ConversationId,x.ProductId,x.Quantity,x.UnitPrice,x.Currency,x.Status,x.ReservedAt,x.ExpiresAt,x.Version)).ToList();
    public IReadOnlyDictionary<string,string> Preferences(string principal)=>_db.ConsumerPreferenceMemories.AsNoTracking().Where(x=>x.PrincipalId==principal).ToDictionary(x=>x.Key,x=>x.Value);
    public void Remember(string principal,string key,string value,string source,DateTimeOffset now){var x=_db.ConsumerPreferenceMemories.SingleOrDefault(v=>v.PrincipalId==principal&&v.Key==key);if(x is null){x=new(){MemoryId=$"preference_{Guid.NewGuid():N}",PrincipalId=principal,Key=key,CreatedAt=now,Version=1};_db.Add(x);}else x.Version++;x.Value=value;x.SourceConversationId=source;x.UpdatedAt=now;_db.SaveChanges();}
    public ConversationPolicy GetPolicy(string principal)=>_db.ConsumerConversationPolicies.AsNoTracking().SingleOrDefault(x=>x.PrincipalId==principal)is{} x
        ?new(x.PrincipalId,x.InteractionMode,x.AskBeforeSubstitutions,x.ShowBasketBeforePayment,x.UpdatedAt,x.Version)
        :new(principal,"AUTO_WHEN_SAFE",false,false,DateTimeOffset.UtcNow);
    public void SavePolicy(ConversationPolicy policy){var x=_db.ConsumerConversationPolicies.SingleOrDefault(v=>v.PrincipalId==policy.PrincipalId);if(x is null){x=new(){PrincipalId=policy.PrincipalId};_db.Add(x);}x.InteractionMode=policy.InteractionMode;x.AskBeforeSubstitutions=policy.AskBeforeSubstitutions;x.ShowBasketBeforePayment=policy.ShowBasketBeforePayment;x.UpdatedAt=policy.UpdatedAt;x.Version=policy.Version;_db.SaveChanges();}
    private static ConsumerPlanningConversation Map(ConsumerPlanningConversationEntity x)=>new(x.ConversationId,x.PrincipalId,x.Objective,x.Status,x.StateJson,x.CreatedAt,x.UpdatedAt,x.Version);
}

public sealed class EfMandateUsageTracker : IMandateUsageTracker
{
    private readonly AgentTrustDbContext _db; public EfMandateUsageTracker(AgentTrustDbContext db)=>_db=db;
    public void RecordSpend(string mandateId,decimal amount,DateTimeOffset when){if(amount<=0)throw new ArgumentOutOfRangeException(nameof(amount));_db.SpendReservations.Add(new(){ReservationId=$"legacy_{Guid.NewGuid():N}",MandateId=mandateId,ExecutionId=$"legacy_{Guid.NewGuid():N}",Amount=amount,Currency="",Status="Committed",ReservedAt=when,FinalisedAt=when,Version=1});_db.SaveChanges();}
    public decimal AmountSpentSince(string id,DateTimeOffset since)=>_db.SpendReservations.Where(x=>x.MandateId==id&&x.Status=="Committed"&&x.ReservedAt>=since).Sum(x=>(decimal?)x.Amount)??0;
    public bool TryReserve(FinancialMandate mandate,string executionId,decimal amount,DateTimeOffset now,out MandateSpendReservation? reservation,out IReadOnlyList<string> reasons,bool oneOffLimitOverride=false)
    {
        using var tx=_db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable);var failures=new List<string>();
        if(amount<=0)failures.Add("AMOUNT_MUST_BE_POSITIVE");if(_db.SpendReservations.Any(x=>x.ExecutionId==executionId&&x.Status!="Released"))failures.Add("EXECUTION_ALREADY_RESERVED");
        if(!oneOffLimitOverride){Check(mandate.DailyLimit,new DateTimeOffset(now.Year,now.Month,now.Day,0,0,0,now.Offset),"DAILY_LIMIT_EXCEEDED");Check(mandate.WeeklyLimit,now.AddDays(-7),"WEEKLY_LIMIT_EXCEEDED");Check(mandate.MonthlyLimit,now.AddMonths(-1),"MONTHLY_LIMIT_EXCEEDED");}
        if(failures.Count>0){tx.Rollback();reservation=null;reasons=failures;return false;}
        var row=new SpendReservationEntity{ReservationId=$"res_{Guid.NewGuid():N}",MandateId=mandate.MandateId,MandateVersion=mandate.Version,ExecutionId=executionId,Amount=amount,Currency=mandate.Currency,Status="Reserved",ReservedAt=now,ExpiresAt=now.AddMinutes(30),Version=1};_db.Add(row);
        try{_db.SaveChanges();tx.Commit();reservation=new(row.ReservationId,row.MandateId,row.ExecutionId,row.Amount,row.ReservedAt,SpendReservationStatus.Reserved);reasons=[];return true;}catch(DbUpdateException){tx.Rollback();reservation=null;reasons=["RESERVATION_CONFLICT"];return false;}
        void Check(decimal? limit,DateTimeOffset since,string code){if(limit is null)return;var used=_db.SpendReservations.Where(x=>x.MandateId==mandate.MandateId&&x.ReservedAt>=since&&(x.Status=="Committed"||x.Status=="Reserved")).Sum(x=>(decimal?)x.Amount)??0;if(used+amount>limit)failures.Add(code);}
    }
    public bool Commit(string id)=>Finalise(id,"Committed"); public bool Release(string id)=>Finalise(id,"Released");
    private bool Finalise(string id,string status){var x=_db.SpendReservations.SingleOrDefault(v=>v.ReservationId==id&&v.Status=="Reserved");if(x is null)return false;x.Status=status;x.FinalisedAt=DateTimeOffset.UtcNow;x.Version++;try{_db.SaveChanges();return true;}catch(DbUpdateConcurrencyException){return false;}}
}

public sealed class EfCommerceDurability : ICommerceDurability
{
    private readonly AgentTrustDbContext _db; public EfCommerceDurability(AgentTrustDbContext db)=>_db=db;
    public void SaveIntent(PurchaseIntent item,string executionId,int mandateVersion)
    {
        if(_db.PurchaseIntents.Any(x=>x.PurchaseIntentId==item.PurchaseIntentId))return;
        _db.PurchaseIntents.Add(new(){PurchaseIntentId=item.PurchaseIntentId,ExecutionId=executionId,PrincipalId=item.PrincipalId,AgentId=item.AgentId,MandateId=item.MandateId,MandateVersion=mandateVersion,
            TaskId=item.TaskId,MerchantId=item.MerchantId,MerchantName=item.MerchantName,Currency=item.Currency,BasketJson=ConsumerStoreJson.Write(item.BasketItems),Subtotal=item.Subtotal,DeliveryFee=item.DeliveryFee,
            TotalAmount=item.TotalAmount,DeliveryAddressReference=item.DeliveryAddressReference,RequestedDeliveryWindow=item.RequestedDeliveryWindow,PaymentMethodReference=item.PaymentMethodReference,
            IntentHash=PurchaseIntentCanonicalizer.Hash(item),PaymentIdempotencyKey=item.IdempotencyKey,CreatedAt=item.CreatedAt,QuoteExpiresAt=item.QuoteExpiresAt,Version=1});_db.SaveChanges();
    }
    public void SaveAuthorisation(PurchaseAuthorisation a)
    {
        if(_db.PurchaseAuthorisations.Any(x=>x.AuthorisationId==a.AuthorisationId))return;
        _db.PurchaseAuthorisations.Add(new(){AuthorisationId=a.AuthorisationId,PurchaseIntentId=a.PurchaseIntentId,TransactionId=a.TransactionId,PrincipalId=a.PrincipalId,AgentId=a.AgentId,MandateId=a.MandateId,
            MandateVersion=a.MandateVersion,MerchantId=a.MerchantId,AuthorisedAmount=a.AuthorisedAmount,Currency=a.Currency,IntentHash=a.IntentHash,PolicyVersion=a.PolicyVersion,
            SigningKeyId="legacy-current",Algorithm="HMAC-SHA256",Signature=a.Signature,Status="Active",IssuedAt=a.AuthorisedAt,ExpiresAt=a.ExpiresAt,Version=1});_db.SaveChanges();
    }
    public void SaveCheckout(PurchaseIntent item,string status)
    {
        var x=_db.CheckoutExecutions.SingleOrDefault(v=>v.PurchaseIntentId==item.PurchaseIntentId);
        if(x is null){x=new(){CheckoutExecutionId=$"checkout_{item.PurchaseIntentId}",PurchaseIntentId=item.PurchaseIntentId,PaymentIdempotencyKey=item.IdempotencyKey,CreatedAt=DateTimeOffset.UtcNow,Version=1};_db.Add(x);}
        else x.Version++;x.Status=status;x.SubmissionCount++;x.UpdatedAt=DateTimeOffset.UtcNow;_db.SaveChanges();
    }
    public void BeginPaymentSubmission(PurchaseIntent item,string provider)
    {
        using var transaction=_db.Database.CurrentTransaction is null?_db.Database.BeginTransaction(IsolationLevel.Serializable):null;
        var now=DateTimeOffset.UtcNow;
        var checkout=_db.CheckoutExecutions.SingleOrDefault(x=>x.PaymentIdempotencyKey==item.IdempotencyKey);
        if(checkout is null){checkout=new(){CheckoutExecutionId=$"checkout_{Guid.NewGuid():N}",PurchaseIntentId=item.PurchaseIntentId,PaymentIdempotencyKey=item.IdempotencyKey,Status="Submitted",SubmissionCount=1,CreatedAt=now,UpdatedAt=now,Version=1};_db.CheckoutExecutions.Add(checkout);}
        else{checkout.SubmissionCount++;if(checkout.Status is not("Succeeded" or "Failed"))checkout.Status="Submitted";checkout.UpdatedAt=now;checkout.Version++;}
        var attempt=_db.ConsumerPaymentAttempts.SingleOrDefault(x=>x.PaymentIdempotencyKey==item.IdempotencyKey);
        if(attempt is null)_db.ConsumerPaymentAttempts.Add(new(){PaymentAttemptId=$"payattempt_{Guid.NewGuid():N}",CheckoutExecutionId=checkout.CheckoutExecutionId,PurchaseIntentId=item.PurchaseIntentId,PaymentIdempotencyKey=item.IdempotencyKey,Provider=provider,ProviderPaymentMethodId=item.PaymentMethodReference,LatestStatus="Submitted",CreatedAt=now,UpdatedAt=now,Version=1});
        else{attempt.CheckoutExecutionId=checkout.CheckoutExecutionId;attempt.Provider=provider;attempt.ProviderPaymentMethodId=item.PaymentMethodReference;if(attempt.LatestStatus is not("Captured" or "Declined"))attempt.LatestStatus="Submitted";attempt.UpdatedAt=now;attempt.Version++;}
        _db.SaveChanges();transaction?.Commit();
    }
    public void RecordPaymentResult(PurchaseIntent item,PlatformPaymentResult result)
    {
        var now=DateTimeOffset.UtcNow;var attempt=_db.ConsumerPaymentAttempts.Single(x=>x.PaymentIdempotencyKey==item.IdempotencyKey);
        var checkout=_db.CheckoutExecutions.Single(x=>x.PaymentIdempotencyKey==item.IdempotencyKey);
        attempt.ProviderPaymentId=result.ProviderReference??attempt.ProviderPaymentId;attempt.LatestStatus=result.Status switch{PlatformPaymentStatus.Succeeded=>"Captured",PlatformPaymentStatus.RequiresAction=>"RequiresAction",PlatformPaymentStatus.Processing=>"Processing",PlatformPaymentStatus.Failed=>"Declined",_=>"Unknown"};attempt.FailureCode=result.FailureReason;attempt.UpdatedAt=now;attempt.Version++;
        checkout.Status=result.Status switch{PlatformPaymentStatus.Succeeded=>"Succeeded",PlatformPaymentStatus.Failed=>"Failed",PlatformPaymentStatus.RequiresAction=>"RequiresAction",PlatformPaymentStatus.Processing=>"Processing",_=>"Unknown"};checkout.UpdatedAt=now;checkout.Version++;
        _db.SaveChanges();
    }
    public void RecordPaymentUnknown(PurchaseIntent item,string? failureCode)
    {
        var now=DateTimeOffset.UtcNow;var attempt=_db.ConsumerPaymentAttempts.Single(x=>x.PaymentIdempotencyKey==item.IdempotencyKey);var checkout=_db.CheckoutExecutions.Single(x=>x.PaymentIdempotencyKey==item.IdempotencyKey);
        if(attempt.LatestStatus!="Captured"){attempt.LatestStatus="Unknown";attempt.FailureCode=failureCode;attempt.UpdatedAt=now;attempt.Version++;}
        if(checkout.Status!="Succeeded"){checkout.Status="Unknown";checkout.UpdatedAt=now;checkout.Version++;}_db.SaveChanges();
    }
    public void SaveReceipt(PurchaseReceipt r,string principal)
    {if(_db.PurchaseReceipts.Any(x=>x.ReceiptId==r.ReceiptId||x.PurchaseIntentId==r.PurchaseIntentId))return;_db.PurchaseReceipts.Add(new(){ReceiptId=r.ReceiptId,PurchaseIntentId=r.PurchaseIntentId,PrincipalId=principal,MerchantId=r.MerchantId,TotalAmount=r.TotalAmount,Currency=r.Currency,ProviderPaymentId=r.ProviderReference,PurchasedAt=r.PurchasedAt});_db.SaveChanges();}
    public PurchaseReceipt? FindReceiptOwned(string id,string principal)=>_db.PurchaseReceipts.AsNoTracking().SingleOrDefault(x=>x.ReceiptId==id&&x.PrincipalId==principal) is { } x
        ? new(x.ReceiptId,x.PurchaseIntentId,x.MerchantId,x.TotalAmount,x.Currency,x.ProviderPaymentId,x.PurchasedAt) : null;
    public PurchaseReceipt? FindReceiptByPurchaseOwned(string id,string principal)=>_db.PurchaseReceipts.AsNoTracking().SingleOrDefault(x=>x.PurchaseIntentId==id&&x.PrincipalId==principal) is { } x
        ? new(x.ReceiptId,x.PurchaseIntentId,x.MerchantId,x.TotalAmount,x.Currency,x.ProviderPaymentId,x.PurchasedAt) : null;
    public void SavePending(PurchaseIntent i,string fingerprint,string reservationId,int mandateVersion)
    {if(_db.PendingConsumerApprovals.Any(x=>x.PurchaseIntentId==i.PurchaseIntentId))return;_db.PendingConsumerApprovals.Add(new(){ApprovalId=$"approval_{Guid.NewGuid():N}",PrincipalId=i.PrincipalId,PurchaseIntentId=i.PurchaseIntentId,TransactionId=i.PurchaseIntentId,MandateId=i.MandateId,MandateVersion=mandateVersion,Amount=i.TotalAmount,Currency=i.Currency,MerchantId=i.MerchantId,IntentHash=fingerprint,Status="Pending",CreatedAt=DateTimeOffset.UtcNow,ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),Version=1});_db.SaveChanges();}
    public DurablePendingPurchase? FindPendingOwned(string id,string principal)
    {var a=_db.PendingConsumerApprovals.AsNoTracking().SingleOrDefault(x=>x.PurchaseIntentId==id&&x.PrincipalId==principal&&x.Status=="Pending"&&x.ExpiresAt>DateTimeOffset.UtcNow);if(a is null)return null;var x=_db.PurchaseIntents.AsNoTracking().Single(x=>x.PurchaseIntentId==id);var reservation=_db.SpendReservations.AsNoTracking().Single(r=>r.ExecutionId==id&&r.Status=="Reserved");var intent=new PurchaseIntent(x.PurchaseIntentId,x.PrincipalId,x.AgentId,x.MandateId,x.TaskId,x.MerchantId,x.MerchantName,x.Currency,ConsumerStoreJson.Read<List<BasketItem>>(x.BasketJson),x.Subtotal,x.DeliveryFee,x.TotalAmount,x.DeliveryAddressReference,x.RequestedDeliveryWindow,x.PaymentMethodReference,x.CreatedAt,x.QuoteExpiresAt,x.PaymentIdempotencyKey);return new(intent,a.IntentHash,reservation.ReservationId,a.MandateVersion);}
    public void CompletePending(string id,bool approved,string approver){var x=_db.PendingConsumerApprovals.SingleOrDefault(v=>v.PurchaseIntentId==id&&v.Status=="Pending");if(x is null)return;x.Status=approved?"Approved":"Rejected";x.ApproverSubject=approver;x.DecidedAt=DateTimeOffset.UtcNow;x.ConsumedAt=approved?DateTimeOffset.UtcNow:null;x.Version++;_db.SaveChanges();}
    public PurchaseIntent? FindIntentOwned(string id,string principal)=>_db.PurchaseIntents.AsNoTracking().SingleOrDefault(x=>x.PurchaseIntentId==id&&x.PrincipalId==principal)is{} x
        ?new(x.PurchaseIntentId,x.PrincipalId,x.AgentId,x.MandateId,x.TaskId,x.MerchantId,x.MerchantName,x.Currency,ConsumerStoreJson.Read<List<BasketItem>>(x.BasketJson),x.Subtotal,x.DeliveryFee,x.TotalAmount,x.DeliveryAddressReference,x.RequestedDeliveryWindow,x.PaymentMethodReference,x.CreatedAt,x.QuoteExpiresAt,x.PaymentIdempotencyKey):null;
}
