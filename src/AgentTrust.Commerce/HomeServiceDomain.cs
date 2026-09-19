namespace AgentTrust.Commerce;

public enum ServicePricingType { Fixed, Hourly, QuoteBased }
public enum HomeServiceBookingStatus { Pending, Confirmed, ProviderAssigned, EnRoute, InProgress, Completed, Cancelled, Rejected, Failed, Unknown }

public sealed record HomeServiceProvider(string ProviderId, string BusinessName, IReadOnlyList<string> ServiceCategories,
    decimal? Rating, string ServiceArea, bool Active, IReadOnlyList<string> Tags);
public sealed record HomeServiceOffering(string ServiceId, string ProviderId, string Name, string Description,
    ServicePricingType PricingType, decimal BasePrice, string Currency, TimeSpan? MinimumDuration,
    TimeSpan? MaximumDuration, TimeSpan? DurationIncrement, bool Available, IReadOnlyList<string> Tags);
public sealed record ServiceAvailabilityRequest(string ServiceId, DateOnly RequestedDate, string TimeWindow,
    TimeSpan Duration, string LocationReference);
public sealed record ServiceAvailabilitySlot(string SlotId, string ProviderId, string ServiceId,
    DateTimeOffset Start, DateTimeOffset End, bool Available);
public sealed record HomeServiceQuoteRequest(string ProviderId, string ServiceId, string SlotId,
    TimeSpan Duration, string LocationReference, IReadOnlyList<string> SelectedOptions, string? Notes = null);
public sealed record HomeServiceQuote(string QuoteId, string ProviderId, string ServiceId, string Currency,
    decimal LabourAmount, decimal CalloutFee, decimal? MaterialsEstimate, decimal ServiceFee,
    decimal Tax, decimal Discount, decimal Total, DateTimeOffset ExpiresAt, string ProviderReference);
public sealed record HomeServiceBookingIntent(string IntentId, string PrincipalId, string AgentId,
    string ProviderId, string ServiceId, string SlotId, string QuoteId, string QuoteHash,
    string LocationReference, DateTimeOffset ScheduledStart, DateTimeOffset ScheduledEnd,
    string Currency, decimal TotalAmount, DateTimeOffset CreatedAt, DateTimeOffset QuoteExpiresAt);
public sealed record HomeServiceBookingResult(string ProviderBookingId, HomeServiceBookingStatus Status,
    string ProviderReference, string? ConfirmationCode, DateTimeOffset ScheduledStart,
    DateTimeOffset ScheduledEnd, string? FailureReason = null);
public sealed record HomeServiceCancellationResult(string ProviderBookingId, bool Cancelled,
    decimal CancellationFee, decimal? RefundAmount, string Currency, string ProviderReference,
    string? FailureReason = null);

public sealed record HomeServiceSearchRequest(string Query, string ServiceArea);
public interface IHomeServiceDiscoveryCapability
{
    Task<IReadOnlyList<HomeServiceOffering>> SearchServicesAsync(HomeServiceSearchRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<HomeServiceProvider>> SearchProvidersAsync(string serviceId, CancellationToken ct = default);
    Task<HomeServiceOffering?> GetServiceAsync(string serviceId, CancellationToken ct = default);
}
public interface IHomeServiceAvailabilityCapability
{
    Task<IReadOnlyList<ServiceAvailabilitySlot>> GetAvailabilityAsync(ServiceAvailabilityRequest request, CancellationToken ct = default);
}
public interface IHomeServiceQuoteCapability
{
    Task<HomeServiceQuote> GetHomeServiceQuoteAsync(HomeServiceQuoteRequest request, CancellationToken ct = default);
}
public interface IHomeServiceBookingCapability
{
    Task<HomeServiceBookingResult> BookServiceAsync(HomeServiceBookingIntent intent, ServiceActionAuthorisation authorisation, CancellationToken ct = default);
    Task<HomeServiceBookingResult> RescheduleServiceAsync(string providerBookingId, string slotId, ServiceActionAuthorisation authorisation, CancellationToken ct = default);
    Task<HomeServiceCancellationResult> CancelServiceAsync(string providerBookingId, ServiceActionAuthorisation authorisation, CancellationToken ct = default);
    Task<HomeServiceBookingResult?> GetBookingStatusAsync(string providerBookingId, CancellationToken ct = default);
}

public sealed record ManagedProviderProfile(string ProviderId, string BusinessName, string ServiceArea,
    IReadOnlyList<string> CapabilityNames, string PaymentAccountReference);
public interface IManagedProviderCatalog
{
    Task RegisterAsync(ManagedProviderProfile profile, CancellationToken ct = default);
}
