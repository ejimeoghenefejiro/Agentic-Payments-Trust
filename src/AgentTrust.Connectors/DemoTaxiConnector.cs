using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentTrust.Commerce;

namespace AgentTrust.Connectors;

public sealed class DemoTaxiConnector : IServiceConnector, IRideSearchCapability, IRideQuoteCapability, IRideBookingCapability
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceActionAuthorisationService _authorisations;
    private readonly ConcurrentDictionary<string, RideQuote> _quotes = [];
    private readonly ConcurrentDictionary<string, RideBookingResult> _tripsByIntent = [];
    private readonly ConcurrentDictionary<string, RideBookingResult> _tripsById = [];
    private readonly ConcurrentDictionary<string, string> _holds = [];
    private static readonly TransportLocation[] Locations =
    [
        new("location-station", "Manchester Piccadilly", 53.4774, -2.2309, "place-station"),
        new("location-airport", "Manchester Airport", 53.365, -2.272, "place-airport"),
        new("location-home", "Saved home", ProviderPlaceId: "saved-home"),
        new("location-work", "Saved work", ProviderPlaceId: "saved-work")
    ];
    private static readonly RideOption[] Options =
    [
        new("economy", "taxi-demo", "economy", "Economy", 4, false, 5, 25, "GBP", 22),
        new("accessible", "taxi-demo", "accessible", "Accessible", 4, true, 9, 28, "GBP", 28),
        new("six-seat", "taxi-demo", "xl", "Six seat", 6, false, 8, 27, "GBP", 31)
    ];

    public DemoTaxiConnector(IServiceActionAuthorisationService authorisations) => _authorisations = authorisations;
    public string ProviderId => "taxi-demo";
    public string ProviderName => "Taxi Demo";
    public int BookRideCount { get; private set; }
    public IReadOnlyCollection<CapabilityDescriptor> Capabilities { get; } =
    [
        new("resolve_location", CapabilityFamily.Discovery, "query", "locations"),
        new("get_ride_options", CapabilityFamily.Discovery, "route,passengers,accessibility", "options"),
        new("get_ride_quote", CapabilityFamily.Quote, "option,route,time", "quote"),
        new("reserve", CapabilityFamily.Reservation, "quoteId", "reservation"),
        new("release", CapabilityFamily.Reservation, "reservationId", "status"),
        new("book_ride", CapabilityFamily.Execution, "intent,authorisation", "trip"),
        new("cancel_ride", CapabilityFamily.Lifecycle, "trip,authorisation", "cancellation"),
        new("get_trip_status", CapabilityFamily.Lifecycle, "trip", "status")
    ];

    public async Task<CapabilityInvocationResult> InvokeAsync(string capability, JsonElement input, CancellationToken cancellationToken = default)
    {
        try
        {
            return capability switch
            {
                "resolve_location" => Ok(new { locations = await ResolveLocationAsync(input.GetProperty("query").GetString()!, cancellationToken) }),
                "get_ride_options" => Ok(new { options = await GetRideOptionsAsync(input.Deserialize<RideRequest>(JsonOptions)!, cancellationToken) }),
                "get_ride_quote" => Ok(await GetRideQuoteAsync(input.Deserialize<RideQuoteRequest>(JsonOptions)!, cancellationToken)),
                "reserve" => Reserve(input),
                "release" => Release(input),
                "book_ride" => Book(input),
                "cancel_ride" => Fail("SIGNED_CANCELLATION_REQUIRED"),
                "get_trip_status" => Status(input),
                _ => Fail("CAPABILITY_NOT_SUPPORTED")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    public Task<IReadOnlyList<TransportLocation>> ResolveLocationAsync(string query, CancellationToken ct = default)
    {
        var matches = Locations.Where(x => x.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || query.Contains(x.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return Task.FromResult<IReadOnlyList<TransportLocation>>(matches);
    }

    public Task<IReadOnlyList<RideOption>> GetRideOptionsAsync(RideRequest request, CancellationToken ct = default)
    {
        var accessible = request.AccessibilityRequirements.Contains("wheelchair");
        return Task.FromResult<IReadOnlyList<RideOption>>(Options.Where(x => x.Capacity >= request.PassengerCount
            && (!accessible || x.WheelchairAccessible)).ToArray());
    }

    public Task<RideQuote> GetRideQuoteAsync(RideQuoteRequest request, CancellationToken ct = default)
    {
        var option = Options.Single(x => x.RideOptionId == request.RideOptionId);
        var baseFare = option.EstimatedFare ?? throw new InvalidOperationException("FARE_UNAVAILABLE");
        var surge = request.PickupTime.Hour is >= 7 and <= 9 ? 3m : 0m;
        var bookingFee = 1.25m;
        var total = baseFare + surge + bookingFee;
        var quote = new RideQuote($"ride_quote_{Guid.NewGuid():N}", ProviderId, option.RideOptionId, "GBP", baseFare,
            bookingFee, 0, surge, 0, 0, total, DateTimeOffset.UtcNow.AddMinutes(option.EstimatedPickupMinutes),
            DateTimeOffset.UtcNow.AddMinutes(option.EstimatedPickupMinutes + option.EstimatedDurationMinutes),
            DateTimeOffset.UtcNow.AddMinutes(2), $"fare_{Guid.NewGuid():N}");
        _quotes[quote.QuoteId] = quote;
        return Task.FromResult(quote);
    }

    public Task<RideBookingResult> BookRideAsync(RideBookingIntent intent, ServiceActionAuthorisation authorisation, CancellationToken ct = default)
    {
        if (!_authorisations.Verify(authorisation, DateTimeOffset.UtcNow) || authorisation.QuoteId != intent.QuoteId || authorisation.Amount != intent.TotalAmount)
            throw new InvalidOperationException("AUTHORISATION_INVALID");
        var result = _tripsByIntent.GetOrAdd(intent.IntentId, _ =>
        {
            BookRideCount++;
            return new($"trip_{Guid.NewGuid():N}", RideStatus.DriverAssigned, $"trip_ref_{Guid.NewGuid():N}", "Demo Driver", "Demo vehicle", "DEMO 123", DateTimeOffset.UtcNow.AddMinutes(5));
        });
        _tripsById[result.ProviderTripId] = result;
        return Task.FromResult(result);
    }

    public Task<RideCancellationResult> CancelRideAsync(string providerTripId, ServiceActionAuthorisation authorisation, CancellationToken ct = default)
    {
        if (!_authorisations.Verify(authorisation, DateTimeOffset.UtcNow)
            || authorisation.ProviderId != ProviderId
            || authorisation.Action != "cancel_ride")
            throw new InvalidOperationException("AUTHORISATION_INVALID");
        if (!_tripsById.TryGetValue(providerTripId, out var trip)) throw new KeyNotFoundException("TRIP_NOT_FOUND");
        var fee = trip.Status is RideStatus.DriverArriving or RideStatus.DriverWaiting ? 5m : 0m;
        _tripsById[providerTripId] = trip with { Status = RideStatus.Cancelled };
        return Task.FromResult(new RideCancellationResult(providerTripId, true, fee, "GBP", trip.ProviderReference));
    }

    public Task<RideBookingResult?> GetTripStatusAsync(string providerTripId, CancellationToken ct = default) =>
        Task.FromResult(_tripsById.GetValueOrDefault(providerTripId));

    private CapabilityInvocationResult Reserve(JsonElement input)
    {
        var quoteId = input.GetProperty("quoteId").GetString()!;
        if (!_quotes.ContainsKey(quoteId)) return Fail("QUOTE_NOT_FOUND");
        var id = $"ride_hold_{Guid.NewGuid():N}";
        _holds[id] = quoteId;
        return Ok(new { reservationId = id });
    }
    private CapabilityInvocationResult Release(JsonElement input) { _holds.TryRemove(input.GetProperty("reservationId").GetString()!, out _); return Ok(new { status = "released" }); }
    private CapabilityInvocationResult Book(JsonElement input)
    {
        var proposal = input.GetProperty("proposal").Deserialize<ServiceActionProposal>()!;
        var quote = input.GetProperty("quote").Deserialize<ServiceQuote>()!;
        var auth = input.GetProperty("authorisation").Deserialize<ServiceActionAuthorisation>()!;
        var providerQuote = _quotes.GetValueOrDefault(quote.QuoteId) ?? throw new InvalidOperationException("QUOTE_NOT_FOUND");
        if (providerQuote.ExpiresAt <= DateTimeOffset.UtcNow) return Fail("QUOTE_EXPIRED");
        var intent = new RideBookingIntent(proposal.IdempotencyKey, proposal.PrincipalId, ProviderId, quote.QuoteId,
            Hash(providerQuote), proposal.Configuration.GetProperty("pickupReference").GetString()!,
            proposal.Configuration.GetProperty("dropoffReference").GetString()!, providerQuote.EstimatedPickupAt,
            providerQuote.RideOptionId, quote.Currency, quote.Total, quote.ExpiresAt);
        return Ok(BookRideAsync(intent, auth).GetAwaiter().GetResult());
    }
    private CapabilityInvocationResult Status(JsonElement input) => _tripsById.TryGetValue(input.GetProperty("providerTripId").GetString()!, out var trip) ? Ok(trip) : Fail("TRIP_NOT_FOUND");
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static CapabilityInvocationResult Ok(object value) => new(true, JsonSerializer.SerializeToElement(value, JsonOptions));
    private static CapabilityInvocationResult Fail(string error) => new(false, JsonSerializer.SerializeToElement(new { error }), error);
}

public sealed class TaxiDomainCapability : IServiceDomainCapability
{
    private static readonly Regex Route = new(@"\bfrom\s+(?<pickup>.+?)\s+to\s+(?<dropoff>.+?)(?=\.|,|\s+(?:do not|under|for|at|tomorrow)\b|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public string DomainId => "taxi";
    public bool CanHandle(IServiceConnector provider, ServicePlanningContext context) =>
        new[] { "resolve_location", "get_ride_options", "get_ride_quote", "book_ride" }.All(name => provider.Capabilities.Any(x => x.Name == name));

    public async Task<ServicePlan> PlanAsync(IServiceConnector provider, ServicePlanningContext context, CancellationToken cancellationToken = default)
    {
        var route = Route.Match(context.Instruction);
        if (!route.Success) throw new ArgumentException("A clear pickup and drop-off are required.");
        var pickup = await Resolve(provider, route.Groups["pickup"].Value.Trim(), cancellationToken);
        var dropoff = await Resolve(provider, route.Groups["dropoff"].Value.Trim(), cancellationToken);
        var passengersMatch = Regex.Match(context.Instruction, @"(?<n>\d+)\s*(?:passengers?|people|seater)", RegexOptions.IgnoreCase);
        var passengers = passengersMatch.Success ? int.Parse(passengersMatch.Groups["n"].Value) : 1;
        IReadOnlyList<string> accessibility = context.Instruction.Contains("wheelchair", StringComparison.OrdinalIgnoreCase)
            ? ["wheelchair"]
            : [];
        var request = new RideRequest(pickup, dropoff, DateTimeOffset.UtcNow.AddMinutes(10), passengers, accessibility);
        var optionsResult = await provider.InvokeAsync("get_ride_options", JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken);
        if (!optionsResult.Success) throw new InvalidOperationException(optionsResult.Error);
        var options = optionsResult.Value.GetProperty("options").EnumerateArray().ToArray();
        if (options.Length == 0) throw new InvalidOperationException("NO_SUITABLE_RIDE");
        var option = options.OrderBy(x => x.GetProperty("estimatedFare").GetDecimal()).First();
        var optionId = option.GetProperty("rideOptionId").GetString()!;
        var quoteRequest = new RideQuoteRequest(optionId, pickup, dropoff, request.RequestedPickupTime, passengers, accessibility);
        var quoteResult = await provider.InvokeAsync("get_ride_quote", JsonSerializer.SerializeToElement(quoteRequest, new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken);
        if (!quoteResult.Success) throw new InvalidOperationException(quoteResult.Error);
        var quoteValue = quoteResult.Value;
        var budget = ParseBudget(context.Instruction);
        var configuration = JsonSerializer.SerializeToElement(new { pickupReference = pickup.LocationId, dropoffReference = dropoff.LocationId, rideOptionId = optionId, passengers });
        var proposal = new ServiceActionProposal($"proposal_{Guid.NewGuid():N}", context.PrincipalId, context.AgentId, provider.ProviderId,
            "book_ride", context.Instruction, configuration, budget, quoteValue.GetProperty("currency").GetString()!, StableId(context));
        var quote = new ServiceQuote(quoteValue.GetProperty("quoteId").GetString()!, provider.ProviderId, "book_ride",
            quoteValue.GetProperty("total").GetDecimal(), quoteValue.GetProperty("currency").GetString()!,
            quoteValue.GetProperty("expiresAt").GetDateTimeOffset(), quoteValue);
        return new(proposal, quote, ["resolve_location", "get_ride_options", "get_ride_quote"]);
    }

    private static async Task<TransportLocation> Resolve(IServiceConnector provider, string query, CancellationToken token)
    {
        var result = await provider.InvokeAsync("resolve_location", JsonSerializer.SerializeToElement(new { query }), token);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        var matches = result.Value.GetProperty("locations").EnumerateArray().ToArray();
        if (matches.Length != 1) throw new ArgumentException(matches.Length == 0 ? "LOCATION_NOT_FOUND" : "LOCATION_AMBIGUOUS");
        return matches[0].Deserialize<TransportLocation>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
    private static decimal ParseBudget(string instruction)
    {
        var match = Regex.Match(instruction, @"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase);
        return match.Success ? decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : throw new ArgumentException("A maximum GBP budget is required.");
    }
    private static string StableId(ServicePlanningContext context) => $"ride_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.PrincipalId}|{context.ProviderId}|{context.Instruction}"))).ToLowerInvariant()[..32]}";
}
