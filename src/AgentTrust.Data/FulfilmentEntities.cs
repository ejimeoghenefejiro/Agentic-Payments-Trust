namespace AgentTrust.Data;

public sealed class FulfilmentQuoteEntity
{
    public string QuoteId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal Total { get; set; }
    public string QuoteHash { get; set; } = "";
    public string ProviderReference { get; set; } = "";
    public string QuoteJson { get; set; } = "{}";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class FulfilmentIntentEntity
{
    public string FulfilmentIntentId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string MerchantOrderId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string QuoteId { get; set; } = "";
    public string QuoteHash { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public string? DestinationReference { get; set; }
    public string? PickupLocationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class FulfilmentExecutionEntity
{
    public string FulfilmentIntentId { get; set; } = "";
    public string? FulfilmentId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? ProviderReference { get; set; }
    public string? CourierReference { get; set; }
    public string? FailureReason { get; set; }
    public int ReconciliationAttempts { get; set; }
    public DateTimeOffset? NextReconciliationAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class FulfilmentStatusHistoryEntity
{
    public long SequenceNumber { get; set; }
    public string ProviderEventId { get; set; } = "";
    public string FulfilmentId { get; set; } = "";
    public string PreviousStatus { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset ProviderTimestamp { get; set; }
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class FulfilmentWebhookEventEntity
{
    public string ProviderEventId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string Status { get; set; } = "Received";
    public string? FailureReason { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class FulfilmentCancellationEntity
{
    public string CancellationId { get; set; } = "";
    public string FulfilmentId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public decimal CancellationFee { get; set; }
    public decimal? RefundAmount { get; set; }
    public string Currency { get; set; } = "";
    public string? ProviderReference { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; } = 1;
}
