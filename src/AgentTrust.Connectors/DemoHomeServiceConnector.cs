using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentTrust.Commerce;

namespace AgentTrust.Connectors;

public sealed class DemoHomeServiceConnector : IServiceConnector, IHomeServiceDiscoveryCapability,
    IHomeServiceAvailabilityCapability, IHomeServiceQuoteCapability, IHomeServiceBookingCapability
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceActionAuthorisationService _authorisations;
    private readonly ConcurrentDictionary<string, HomeServiceQuote> _quotes = [];
    private readonly ConcurrentDictionary<string, HomeServiceBookingResult> _bookingsByIntent = [];
    private readonly ConcurrentDictionary<string, HomeServiceBookingResult> _bookingsById = [];
    private readonly ConcurrentDictionary<string, string> _holds = [];
    private static readonly HomeServiceProvider[] Providers =
    [
        new("provider-value", "Local Services One", ["cleaning", "plumbing"], 4.6m, "default-area", true, ["value"]),
        new("provider-premium", "Local Services Two", ["cleaning", "electrical"], 4.9m, "default-area", true, ["premium"])
    ];
    private static readonly HomeServiceOffering[] Services =
    [
        new("clean-hourly", "provider-value", "Home cleaning", "General home cleaning", ServicePricingType.Hourly, 16, "GBP", TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(1), true, ["cleaner", "cleaning"]),
        new("clean-fixed", "provider-premium", "Fixed home clean", "Fixed two-hour clean", ServicePricingType.Fixed, 48, "GBP", TimeSpan.FromHours(2), TimeSpan.FromHours(2), null, true, ["cleaner", "cleaning"]),
        new("plumber-callout", "provider-value", "Plumber visit", "Plumbing diagnosis", ServicePricingType.Fixed, 70, "GBP", null, null, null, true, ["plumber", "plumbing"])
    ];

    public DemoHomeServiceConnector(IServiceActionAuthorisationService authorisations) => _authorisations = authorisations;
    public string ProviderId => "home-service-demo";
    public string ProviderName => "Home Service Demo";
    public int BookingCount { get; private set; }
    public IReadOnlyCollection<CapabilityDescriptor> Capabilities { get; } =
    [
        new("search_services", CapabilityFamily.Discovery, "query,area", "services"),
        new("search_providers", CapabilityFamily.Discovery, "serviceId", "providers"),
        new("get_service", CapabilityFamily.Discovery, "serviceId", "service"),
        new("get_availability", CapabilityFamily.Configuration, "service,date,time,duration", "slots"),
        new("get_service_quote", CapabilityFamily.Quote, "provider,service,slot,duration", "quote"),
        new("reserve", CapabilityFamily.Reservation, "quoteId", "reservation"),
        new("release", CapabilityFamily.Reservation, "reservationId", "status"),
        new("book_service", CapabilityFamily.Execution, "intent,authorisation", "booking"),
        new("reschedule_service", CapabilityFamily.Lifecycle, "booking,slot,authorisation", "booking"),
        new("cancel_service", CapabilityFamily.Lifecycle, "booking,authorisation", "cancellation"),
        new("get_booking_status", CapabilityFamily.Lifecycle, "booking", "status")
    ];

    public async Task<CapabilityInvocationResult> InvokeAsync(string capability, JsonElement input, CancellationToken cancellationToken = default)
    {
        try
        {
            return capability switch
            {
                "search_services" => Ok(new { services = await SearchServicesAsync(input.Deserialize<HomeServiceSearchRequest>(JsonOptions)!, cancellationToken) }),
                "search_providers" => Ok(new { providers = await SearchProvidersAsync(input.GetProperty("serviceId").GetString()!, cancellationToken) }),
                "get_service" => Ok(await GetServiceAsync(input.GetProperty("serviceId").GetString()!, cancellationToken)),
                "get_availability" => Ok(new { slots = await GetAvailabilityAsync(input.Deserialize<ServiceAvailabilityRequest>(JsonOptions)!, cancellationToken) }),
                "get_service_quote" => Ok(await GetHomeServiceQuoteAsync(input.Deserialize<HomeServiceQuoteRequest>(JsonOptions)!, cancellationToken)),
                "reserve" => Reserve(input), "release" => Release(input), "book_service" => Book(input),
                "get_booking_status" => Status(input),
                _ => Fail("CAPABILITY_NOT_SUPPORTED")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { return Fail(ex.Message); }
    }

    public Task<IReadOnlyList<HomeServiceOffering>> SearchServicesAsync(HomeServiceSearchRequest request, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HomeServiceOffering>>(Services.Where(x => x.Available &&
            (x.Name.Contains(request.Query, StringComparison.OrdinalIgnoreCase) || x.Tags.Any(t => request.Query.Contains(t, StringComparison.OrdinalIgnoreCase)))).ToArray());
    public Task<IReadOnlyList<HomeServiceProvider>> SearchProvidersAsync(string serviceId, CancellationToken ct = default)
    { var service = Services.Single(x => x.ServiceId == serviceId); return Task.FromResult<IReadOnlyList<HomeServiceProvider>>(Providers.Where(x => x.ProviderId == service.ProviderId && x.Active).ToArray()); }
    public Task<HomeServiceOffering?> GetServiceAsync(string serviceId, CancellationToken ct = default) => Task.FromResult(Services.FirstOrDefault(x => x.ServiceId == serviceId));
    public Task<IReadOnlyList<ServiceAvailabilitySlot>> GetAvailabilityAsync(ServiceAvailabilityRequest request, CancellationToken ct = default)
    {
        var service = Services.Single(x => x.ServiceId == request.ServiceId); var date = request.RequestedDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var morning = new DateTimeOffset(date.AddHours(9)); var afternoon = new DateTimeOffset(date.AddHours(14));
        return Task.FromResult<IReadOnlyList<ServiceAvailabilitySlot>>([new($"slot_{service.ServiceId}_morning", service.ProviderId, service.ServiceId, morning, morning.Add(request.Duration), true), new($"slot_{service.ServiceId}_afternoon", service.ProviderId, service.ServiceId, afternoon, afternoon.Add(request.Duration), false)]);
    }
    public Task<HomeServiceQuote> GetHomeServiceQuoteAsync(HomeServiceQuoteRequest request, CancellationToken ct = default)
    {
        var service = Services.Single(x => x.ServiceId == request.ServiceId && x.ProviderId == request.ProviderId);
        var labour = service.PricingType == ServicePricingType.Hourly ? service.BasePrice * (decimal)request.Duration.TotalHours : service.BasePrice;
        var callout = service.Tags.Contains("plumber") ? 10m : 0m; var fee = decimal.Round(labour * .05m, 2); var total = labour + callout + fee;
        var quote = new HomeServiceQuote($"service_quote_{Guid.NewGuid():N}", request.ProviderId, request.ServiceId, "GBP", labour, callout, null, fee, 0, 0, total, DateTimeOffset.UtcNow.AddMinutes(5), $"provider_quote_{Guid.NewGuid():N}"); _quotes[quote.QuoteId] = quote; return Task.FromResult(quote);
    }
    public Task<HomeServiceBookingResult> BookServiceAsync(HomeServiceBookingIntent intent, ServiceActionAuthorisation authorisation, CancellationToken ct = default)
    {
        Validate(authorisation, "book_service", intent.QuoteId, intent.TotalAmount); var result = _bookingsByIntent.GetOrAdd(intent.IntentId, _ => { BookingCount++; return new($"service_booking_{Guid.NewGuid():N}", HomeServiceBookingStatus.Confirmed, $"booking_ref_{Guid.NewGuid():N}", $"confirm-{Guid.NewGuid():N}"[..14], intent.ScheduledStart, intent.ScheduledEnd); }); _bookingsById[result.ProviderBookingId] = result; return Task.FromResult(result);
    }
    public Task<HomeServiceBookingResult> RescheduleServiceAsync(string providerBookingId, string slotId, ServiceActionAuthorisation authorisation, CancellationToken ct = default)
    { Validate(authorisation, "reschedule_service"); var current = _bookingsById.GetValueOrDefault(providerBookingId) ?? throw new KeyNotFoundException("BOOKING_NOT_FOUND"); var updated = current with { ScheduledStart = current.ScheduledStart.AddDays(7), ScheduledEnd = current.ScheduledEnd.AddDays(7) }; _bookingsById[providerBookingId] = updated; return Task.FromResult(updated); }
    public Task<HomeServiceCancellationResult> CancelServiceAsync(string providerBookingId, ServiceActionAuthorisation authorisation, CancellationToken ct = default)
    { Validate(authorisation, "cancel_service"); var current = _bookingsById.GetValueOrDefault(providerBookingId) ?? throw new KeyNotFoundException("BOOKING_NOT_FOUND"); var fee = current.ScheduledStart - DateTimeOffset.UtcNow < TimeSpan.FromHours(24) ? 10m : 0m; _bookingsById[providerBookingId] = current with { Status = HomeServiceBookingStatus.Cancelled }; return Task.FromResult(new HomeServiceCancellationResult(providerBookingId, true, fee, null, "GBP", current.ProviderReference)); }
    public Task<HomeServiceBookingResult?> GetBookingStatusAsync(string providerBookingId, CancellationToken ct = default) => Task.FromResult(_bookingsById.GetValueOrDefault(providerBookingId));

    private CapabilityInvocationResult Reserve(JsonElement input) { var quoteId = input.GetProperty("quoteId").GetString()!; if (!_quotes.ContainsKey(quoteId)) return Fail("QUOTE_NOT_FOUND"); var id = $"service_hold_{Guid.NewGuid():N}"; _holds[id] = quoteId; return Ok(new { reservationId = id }); }
    private CapabilityInvocationResult Release(JsonElement input) { _holds.TryRemove(input.GetProperty("reservationId").GetString()!, out _); return Ok(new { status = "released" }); }
    private CapabilityInvocationResult Book(JsonElement input)
    {
        var proposal = input.GetProperty("proposal").Deserialize<ServiceActionProposal>()!; var quote = input.GetProperty("quote").Deserialize<ServiceQuote>()!; var auth = input.GetProperty("authorisation").Deserialize<ServiceActionAuthorisation>()!; var providerQuote = _quotes.GetValueOrDefault(quote.QuoteId) ?? throw new InvalidOperationException("QUOTE_NOT_FOUND"); if (providerQuote.ExpiresAt <= DateTimeOffset.UtcNow) return Fail("QUOTE_EXPIRED"); var slotStart = proposal.Configuration.GetProperty("scheduledStart").GetDateTimeOffset(); var slotEnd = proposal.Configuration.GetProperty("scheduledEnd").GetDateTimeOffset(); var intent = new HomeServiceBookingIntent(proposal.IdempotencyKey, proposal.PrincipalId, proposal.AgentId, ProviderId, providerQuote.ServiceId, proposal.Configuration.GetProperty("slotId").GetString()!, quote.QuoteId, Hash(providerQuote), proposal.Configuration.GetProperty("locationReference").GetString()!, slotStart, slotEnd, quote.Currency, quote.Total, DateTimeOffset.UtcNow, quote.ExpiresAt); return Ok(BookServiceAsync(intent, auth).GetAwaiter().GetResult());
    }
    private CapabilityInvocationResult Status(JsonElement input) => _bookingsById.TryGetValue(input.GetProperty("providerBookingId").GetString()!, out var booking) ? Ok(booking) : Fail("BOOKING_NOT_FOUND");
    private void Validate(ServiceActionAuthorisation auth, string action, string? quote = null, decimal? amount = null) { if (!_authorisations.Verify(auth, DateTimeOffset.UtcNow) || auth.ProviderId != ProviderId || auth.Action != action || (quote is not null && auth.QuoteId != quote) || (amount is not null && auth.Amount != amount)) throw new InvalidOperationException("AUTHORISATION_INVALID"); }
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static CapabilityInvocationResult Ok(object? value) => new(true, JsonSerializer.SerializeToElement(value, JsonOptions));
    private static CapabilityInvocationResult Fail(string error) => new(false, JsonSerializer.SerializeToElement(new { error }), error);
}

public sealed class HomeServiceDomainCapability : IServiceDomainCapability
{
    public string DomainId => "home-service";
    public bool CanHandle(IServiceConnector provider, ServicePlanningContext context) => new[] { "search_services", "search_providers", "get_availability", "get_service_quote", "book_service" }.All(name => provider.Capabilities.Any(x => x.Name == name));
    public async Task<ServicePlan> PlanAsync(IServiceConnector provider, ServicePlanningContext context, CancellationToken token = default)
    {
        var budget = Money(context.Instruction); var duration = Duration(context.Instruction); var date = NextRequestedDate(context.Instruction); var window = context.Instruction.Contains("afternoon", StringComparison.OrdinalIgnoreCase) ? "afternoon" : "morning";
        var search = await provider.InvokeAsync("search_services", JsonSerializer.SerializeToElement(new HomeServiceSearchRequest(context.Instruction, "default-area"), new JsonSerializerOptions(JsonSerializerDefaults.Web)), token); if (!search.Success) throw new InvalidOperationException(search.Error); var services = search.Value.GetProperty("services").EnumerateArray().ToArray(); if (services.Length == 0) throw new InvalidOperationException("NO_MATCHING_SERVICE");
        ServiceQuote? best = null; JsonElement bestService = default; JsonElement bestSlot = default;
        foreach (var service in services)
        {
            var serviceId = service.GetProperty("serviceId").GetString()!; var availability = await provider.InvokeAsync("get_availability", JsonSerializer.SerializeToElement(new ServiceAvailabilityRequest(serviceId, date, window, duration, "saved-home"), new JsonSerializerOptions(JsonSerializerDefaults.Web)), token); var slots = availability.Value.GetProperty("slots").EnumerateArray().Where(x => x.GetProperty("available").GetBoolean() && (window != "morning" || x.GetProperty("start").GetDateTimeOffset().Hour < 12) && (window != "afternoon" || x.GetProperty("start").GetDateTimeOffset().Hour >= 12));
            foreach (var slot in slots)
            {
                var request = new HomeServiceQuoteRequest(service.GetProperty("providerId").GetString()!, serviceId, slot.GetProperty("slotId").GetString()!, duration, "saved-home", []); var result = await provider.InvokeAsync("get_service_quote", JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), token); if (!result.Success) continue; var value = result.Value; var quote = new ServiceQuote(value.GetProperty("quoteId").GetString()!, provider.ProviderId, "book_service", value.GetProperty("total").GetDecimal(), value.GetProperty("currency").GetString()!, value.GetProperty("expiresAt").GetDateTimeOffset(), value); if (best is null || quote.Total < best.Total) { best = quote; bestService = service; bestSlot = slot; }
            }
        }
        if (best is null) throw new InvalidOperationException("NO_AVAILABLE_SLOT"); var configuration = JsonSerializer.SerializeToElement(new { serviceId = bestService.GetProperty("serviceId").GetString(), slotId = bestSlot.GetProperty("slotId").GetString(), locationReference = "saved-home", scheduledStart = bestSlot.GetProperty("start").GetDateTimeOffset(), scheduledEnd = bestSlot.GetProperty("end").GetDateTimeOffset(), duration }); var id = $"service_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.PrincipalId}|{context.ProviderId}|{context.Instruction}"))).ToLowerInvariant()[..32]}"; return new(new ServiceActionProposal($"proposal_{Guid.NewGuid():N}", context.PrincipalId, context.AgentId, provider.ProviderId, "book_service", context.Instruction, configuration, budget, best.Currency, id), best, ["search_services", "search_providers", "get_availability", "get_service_quote"]);
    }
    private static decimal Money(string text) { var match = Regex.Match(text, @"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase); return match.Success ? decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : throw new ArgumentException("A maximum GBP budget is required."); }
    private static TimeSpan Duration(string text)
    {
        var match = Regex.Match(text, @"(?<amount>\d+|one|two|three|four|five|six)\s*hours?", RegexOptions.IgnoreCase);
        if (!match.Success) return TimeSpan.FromHours(1);
        var value = match.Groups["amount"].Value.ToLowerInvariant() switch
        {
            "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6,
            var digits => int.Parse(digits, CultureInfo.InvariantCulture)
        };
        return TimeSpan.FromHours(value);
    }
    private static DateOnly NextRequestedDate(string text) { var today = DateOnly.FromDateTime(DateTime.UtcNow); if (!text.Contains("Saturday", StringComparison.OrdinalIgnoreCase)) return today.AddDays(1); var days = ((int)DayOfWeek.Saturday - (int)today.DayOfWeek + 7) % 7; return today.AddDays(days == 0 ? 7 : days); }
}
