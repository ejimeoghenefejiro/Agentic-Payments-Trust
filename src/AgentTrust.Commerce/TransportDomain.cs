namespace AgentTrust.Commerce;

public enum RideStatus { Requested, DriverAssigned, DriverArriving, DriverWaiting, InProgress, Completed, Cancelled, DriverCancelled, Failed, Unknown }
public sealed record TransportLocation(string LocationId, string DisplayName, double? Latitude = null, double? Longitude = null, string? ProviderPlaceId = null);
public sealed record RideRequest(TransportLocation Pickup, TransportLocation Dropoff, DateTimeOffset RequestedPickupTime,
    int PassengerCount, IReadOnlyList<string> AccessibilityRequirements, int? LuggageCount = null,
    IReadOnlyList<string>? Preferences = null);
public sealed record RideOption(string RideOptionId, string ProviderId, string VehicleType, string DisplayName, int Capacity,
    bool WheelchairAccessible, int EstimatedPickupMinutes, int EstimatedDurationMinutes, string Currency,
    decimal? EstimatedFare = null);
public sealed record RideQuoteRequest(string RideOptionId, TransportLocation Pickup, TransportLocation Dropoff,
    DateTimeOffset PickupTime, int PassengerCount, IReadOnlyList<string> AccessibilityRequirements);
public sealed record RideQuote(string QuoteId, string ProviderId, string RideOptionId, string Currency, decimal BaseFare,
    decimal BookingFee, decimal ServiceFee, decimal? SurgeAmount, decimal Tax, decimal Discount, decimal Total,
    DateTimeOffset EstimatedPickupAt, DateTimeOffset EstimatedArrivalAt, DateTimeOffset ExpiresAt, string ProviderReference);
public sealed record RideBookingIntent(string IntentId, string PrincipalId, string ProviderId, string QuoteId,
    string QuoteHash, string PickupReference, string DropoffReference, DateTimeOffset PickupTime,
    string RideOptionId, string Currency, decimal TotalAmount, DateTimeOffset QuoteExpiresAt);
public sealed record RideBookingResult(string ProviderTripId, RideStatus Status, string ProviderReference,
    string? DriverDisplayName = null, string? VehicleDescription = null, string? Registration = null,
    DateTimeOffset? EstimatedPickupAt = null, string? FailureReason = null);
public sealed record RideCancellationResult(string ProviderTripId, bool Cancelled, decimal CancellationFee,
    string Currency, string ProviderReference, string? FailureReason = null);

public interface IRideSearchCapability
{
    Task<IReadOnlyList<TransportLocation>> ResolveLocationAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<RideOption>> GetRideOptionsAsync(RideRequest request, CancellationToken ct = default);
}
public interface IRideQuoteCapability
{
    Task<RideQuote> GetRideQuoteAsync(RideQuoteRequest request, CancellationToken ct = default);
}
public interface IRideBookingCapability
{
    Task<RideBookingResult> BookRideAsync(RideBookingIntent intent, ServiceActionAuthorisation authorisation, CancellationToken ct = default);
    Task<RideCancellationResult> CancelRideAsync(string providerTripId, ServiceActionAuthorisation authorisation, CancellationToken ct = default);
    Task<RideBookingResult?> GetTripStatusAsync(string providerTripId, CancellationToken ct = default);
}
