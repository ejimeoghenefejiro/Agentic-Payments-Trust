namespace AgentTrust.Data;

// Durable persistence shapes for the Consumer Pilot. Sensitive provider credentials, PAN, CVV,
// and PaymentIntent client secrets must never be stored in these records.
public sealed class ConsumerProfileEntity
{
    public string PrincipalId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Timezone { get; set; } = "UTC";
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ConnectedServiceEntity
{
    public string Id { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ExternalAccountReference { get; set; } = "";
    public string ConnectionType { get; set; } = "";
    public string? CredentialReference { get; set; }
    public string Status { get; set; } = "";
    public string CapabilitiesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ConsumerPurchaseTaskEntity
{
    public string TaskId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string MerchantScopeJson { get; set; } = "[]";
    public string Schedule { get; set; } = "";
    public string Timezone { get; set; } = "UTC";
    public decimal MaximumAmount { get; set; }
    public string Currency { get; set; } = "";
    public string ShoppingListJson { get; set; } = "[]";
    public string PreferencesJson { get; set; } = "{}";
    public string MandateId { get; set; } = "";
    public int MandateVersion { get; set; }
    public string PaymentMethodId { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset NextExecutionAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class PurchaseExecutionEntity
{
    public string ExecutionId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public DateTimeOffset ScheduledFor { get; set; }
    public string PrincipalId { get; set; } = "";
    public string PurchaseIntentId { get; set; } = "";
    public string State { get; set; } = "";
    public string? TransactionId { get; set; }
    public string? ProviderPaymentId { get; set; }
    public string? RequiredAction { get; set; }
    public string ReasonsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class PurchaseIntentEntity
{
    public string PurchaseIntentId { get; set; } = "";
    public string ExecutionId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string MandateId { get; set; } = "";
    public int MandateVersion { get; set; }
    public string TaskId { get; set; } = "";
    public string MerchantId { get; set; } = "";
    public string MerchantName { get; set; } = "";
    public string Currency { get; set; } = "";
    public string BasketJson { get; set; } = "[]";
    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal TotalAmount { get; set; }
    public string DeliveryAddressReference { get; set; } = "";
    public string? RequestedDeliveryWindow { get; set; }
    public string PaymentMethodReference { get; set; } = "";
    public string IntentHash { get; set; } = "";
    public string PaymentIdempotencyKey { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset QuoteExpiresAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class PurchaseAuthorisationEntity
{
    public string AuthorisationId { get; set; } = "";
    public string PurchaseIntentId { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string MandateId { get; set; } = "";
    public int MandateVersion { get; set; }
    public string MerchantId { get; set; } = "";
    public decimal AuthorisedAmount { get; set; }
    public string Currency { get; set; } = "";
    public string IntentHash { get; set; } = "";
    public string PolicyVersion { get; set; } = "";
    public string SigningKeyId { get; set; } = "";
    public string Algorithm { get; set; } = "HMAC-SHA256";
    public string Signature { get; set; } = "";
    public string Status { get; set; } = "Active";
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class PurchaseLifecycleEventEntity
{
    public long SequenceNumber { get; set; }
    public string EventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PurchaseIntentId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string? TransactionId { get; set; }
    public string IntentHash { get; set; } = "";
    public string PreviousHash { get; set; } = "";
    public string CurrentHash { get; set; } = "";
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset Timestamp { get; set; }
}

public sealed class PendingConsumerApprovalEntity
{
    public string ApprovalId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string PurchaseIntentId { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string MandateId { get; set; } = "";
    public int MandateVersion { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string MerchantId { get; set; } = "";
    public string IntentHash { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? ApproverSubject { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class CheckoutExecutionEntity
{
    public string CheckoutExecutionId { get; set; } = "";
    public string PurchaseIntentId { get; set; } = "";
    public string PaymentIdempotencyKey { get; set; } = "";
    public string Status { get; set; } = "Created";
    public int SubmissionCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class ConsumerPaymentAttemptEntity
{
    public string PaymentAttemptId { get; set; } = "";
    public string CheckoutExecutionId { get; set; } = "";
    public string PurchaseIntentId { get; set; } = "";
    public string PaymentIdempotencyKey { get; set; } = "";
    public string Provider { get; set; } = "Stripe";
    public string? ProviderPaymentId { get; set; }
    public string? ProviderCustomerId { get; set; }
    public string ProviderPaymentMethodId { get; set; } = "";
    public string LatestStatus { get; set; } = "Created";
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class TaskOccurrenceEntity
{
    public string OccurrenceId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public DateTimeOffset ScheduledFor { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ClaimedBy { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class SpendReservationEntity
{
    public string ReservationId { get; set; } = "";
    public string MandateId { get; set; } = "";
    public int MandateVersion { get; set; }
    public string ExecutionId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "Reserved";
    public DateTimeOffset ReservedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? FinalisedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class OneOffAuthorisationEntity
{
    public string AuthorisationId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string PurchaseIntentId { get; set; } = "";
    public string MandateId { get; set; } = "";
    public int MandateVersion { get; set; }
    public string TransactionFingerprint { get; set; } = "";
    public decimal MaximumAmount { get; set; }
    public string Currency { get; set; } = "";
    public string MerchantId { get; set; } = "";
    public string PaymentMethodReference { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public string Status { get; set; } = "Active";
    public DateTimeOffset ApprovedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class StripeWebhookEventEntity
{
    public string ProviderEventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string? ProviderPaymentId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string Status { get; set; } = "Received";
    public DateTimeOffset ProviderCreatedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? FailureReason { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class PurchaseReceiptEntity
{
    public string ReceiptId { get; set; } = "";
    public string PurchaseIntentId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string MerchantId { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "";
    public string ProviderPaymentId { get; set; } = "";
    public DateTimeOffset PurchasedAt { get; set; }
}

public sealed class FinancialMandateEntity
{
    public string MandateId { get; set; } = "";
    public int Version { get; set; }
    public string PrincipalId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string Merchant { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string PaymentMethodId { get; set; } = "";
    public decimal PerTransactionLimit { get; set; }
    public decimal? DailyLimit { get; set; }
    public decimal? WeeklyLimit { get; set; }
    public decimal? MonthlyLimit { get; set; }
    public string Currency { get; set; } = "";
    public string TaskParametersJson { get; set; } = "{}";
    public string AboveLimit { get; set; } = "Block";
    public string Status { get; set; } = "Active";
    public string? SupersedesMandateId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long ConcurrencyVersion { get; set; } = 1;
}

public sealed class ConsumerPaymentMethodEntity
{
    public string PaymentMethodId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProviderToken { get; set; } = "";
    public string CardBrand { get; set; } = "";
    public string Last4 { get; set; } = "";
    public int ExpiryMonth { get; set; }
    public int ExpiryYear { get; set; }
    public string Status { get; set; } = "Active";
    public long Version { get; set; } = 1;
}

public sealed class ConsumerPlanningConversationEntity
{
    public string ConversationId{get;set;}="";public string PrincipalId{get;set;}="";public string Objective{get;set;}="";public string Status{get;set;}="INVESTIGATING";public string StateJson{get;set;}="{}";public DateTimeOffset CreatedAt{get;set;}public DateTimeOffset UpdatedAt{get;set;}public long Version{get;set;}=1;
}
public sealed class ConsumerPlanningTurnEntity
{
    public string TurnId{get;set;}="";public string ConversationId{get;set;}="";public int Sequence{get;set;}public string Role{get;set;}="";public string Kind{get;set;}="";public string Content{get;set;}="";public string? ToolName{get;set;}public string? ToolInputJson{get;set;}public string? ToolOutputJson{get;set;}public DateTimeOffset CreatedAt{get;set;}
}
public sealed class ConsumerProductReservationEntity
{
    public string ReservationId{get;set;}="";public string ConversationId{get;set;}="";public string ProductId{get;set;}="";public int Quantity{get;set;}public decimal UnitPrice{get;set;}public string Currency{get;set;}="";public string Status{get;set;}="Reserved";public DateTimeOffset ReservedAt{get;set;}public DateTimeOffset ExpiresAt{get;set;}public long Version{get;set;}=1;
}
public sealed class ConsumerPreferenceMemoryEntity
{
    public string MemoryId{get;set;}="";public string PrincipalId{get;set;}="";public string Key{get;set;}="";public string Value{get;set;}="";public string SourceConversationId{get;set;}="";public DateTimeOffset CreatedAt{get;set;}public DateTimeOffset UpdatedAt{get;set;}public long Version{get;set;}=1;
}
public sealed class ConsumerConversationPolicyEntity
{
    public string PrincipalId{get;set;}="";public string InteractionMode{get;set;}="AUTO_WHEN_SAFE";public bool AskBeforeSubstitutions{get;set;}public bool ShowBasketBeforePayment{get;set;}public DateTimeOffset UpdatedAt{get;set;}public long Version{get;set;}=1;
}
