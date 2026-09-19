using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentTrust.Commerce;
namespace AgentTrust.Connectors;

public sealed class DemoHotelConnector : IServiceConnector
{
    private sealed record Room(string Id, string Name, decimal NightlyRate, int Capacity, bool Accessible);
    private sealed record Hold(string Id, string QuoteId, DateTimeOffset ExpiresAt, string Status);
    private readonly IServiceActionAuthorisationService _authorisations; private readonly ConcurrentDictionary<string, Hold> _holds = []; private readonly ConcurrentDictionary<string, string> _bookings = [];
    private static readonly Room[] Rooms = [new("value-double", "Value double room", 72, 2, false), new("accessible-double", "Accessible double room", 89, 2, true), new("family-room", "Family room", 118, 4, true)];
    public DemoHotelConnector(IServiceActionAuthorisationService authorisations) => _authorisations = authorisations;
    public string ProviderId => "hotel-demo"; public string ProviderName => "Hotel Demo";
    public IReadOnlyCollection<CapabilityDescriptor> Capabilities { get; } = [new("search", CapabilityFamily.Discovery, "destination,checkIn,checkOut,guests,accessible", "rooms"), new("availability", CapabilityFamily.Discovery, "roomId,dates", "available"), new("get_quote", CapabilityFamily.Quote, "roomId,dates", "quote"), new("refresh_quote", CapabilityFamily.Quote, "quote", "quote"), new("reserve", CapabilityFamily.Reservation, "quoteId", "reservation"), new("release", CapabilityFamily.Reservation, "reservationId", "status"), new("book", CapabilityFamily.Execution, "proposal,quote,authorisation,reservationId", "confirmation"), new("status", CapabilityFamily.Lifecycle, "booking", "status"), new("amend", CapabilityFamily.Lifecycle, "booking,dates", "quote"), new("cancel", CapabilityFamily.Lifecycle, "booking", "refund policy"), new("confirmation", CapabilityFamily.Evidence, "booking", "confirmation")];
    public Task<CapabilityInvocationResult> InvokeAsync(string capability, JsonElement input, CancellationToken token = default)
    {
        try
        {
            return Task.FromResult(capability switch
            { "search" => Search(input), "availability" => Availability(input), "get_quote" or "refresh_quote" => Quote(input), "reserve" => Reserve(input), "release" => Release(input), "book" => Book(input), "status" or "confirmation" => Status(input), "cancel" => Cancel(input), "amend" => Amend(input), _ => Fail("CAPABILITY_NOT_SUPPORTED") });
        }
        catch (KeyNotFoundException ex) { return Task.FromResult(Fail(ex.Message)); }
        catch (InvalidOperationException ex) { return Task.FromResult(Fail(ex.Message)); }
    }
    private static CapabilityInvocationResult Search(JsonElement input) { var guests = input.GetProperty("guests").GetInt32(); var accessible = input.GetProperty("accessible").GetBoolean(); var rooms = Rooms.Where(x => x.Capacity >= guests && (!accessible || x.Accessible)).OrderBy(x => x.NightlyRate).Select(x => new { id = x.Id, name = x.Name, nightlyRate = x.NightlyRate, capacity = x.Capacity, accessible = x.Accessible }); return Ok(new { rooms }); }
    private static CapabilityInvocationResult Availability(JsonElement input) => Ok(new { available = Rooms.Any(x => x.Id == input.GetProperty("roomId").GetString()) });
    private static CapabilityInvocationResult Quote(JsonElement input) { var room = Rooms.Single(x => x.Id == input.GetProperty("roomId").GetString()); var checkIn = input.GetProperty("checkIn").GetDateTime(); var checkOut = input.GetProperty("checkOut").GetDateTime(); var nights = (checkOut - checkIn).Days; if (nights < 1) throw new InvalidOperationException("INVALID_STAY_DATES"); var total = room.NightlyRate * nights; return Ok(new { quoteId = $"hq_{Guid.NewGuid():N}", roomId = room.Id, total, currency = "GBP", checkIn, checkOut, nights, expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), cancellation = new { freeUntil = checkIn.AddDays(-2), lateCancellationFee = total * .25m } }); }
    private CapabilityInvocationResult Reserve(JsonElement input) { var quoteId = input.GetProperty("quoteId").GetString()!; var hold = new Hold($"hr_{Guid.NewGuid():N}", quoteId, DateTimeOffset.UtcNow.AddMinutes(10), "Held"); _holds[hold.Id] = hold; return Ok(new { reservationId = hold.Id, hold.ExpiresAt, status = hold.Status }); }
    private CapabilityInvocationResult Release(JsonElement input) { var id = input.GetProperty("reservationId").GetString()!; if (!_holds.TryGetValue(id, out var hold)) return Fail("RESERVATION_NOT_FOUND"); _holds[id] = hold with { Status = "Released" }; return Ok(new { reservationId = id, status = "released" }); }
    private CapabilityInvocationResult Book(JsonElement input) { var auth = input.GetProperty("authorisation").Deserialize<ServiceActionAuthorisation>(); if (auth is null || auth.ProviderId != ProviderId || auth.Action != "book" || !_authorisations.Verify(auth, DateTimeOffset.UtcNow)) return Fail("AUTHORISATION_INVALID"); var reservation = input.GetProperty("reservationId").GetString()!; if (!_holds.TryGetValue(reservation, out var hold) || hold.Status != "Held" || hold.ExpiresAt <= DateTimeOffset.UtcNow) return Fail("RESERVATION_INVALID"); var reference = $"hotel_{Guid.NewGuid():N}"; _bookings[reference] = "Confirmed"; _holds[reservation] = hold with { Status = "Consumed" }; return Ok(new { providerReference = reference, reservationId = reservation, status = "confirmed", quoteId = auth.QuoteId }); }
    private CapabilityInvocationResult Status(JsonElement input) { var reference = input.GetProperty("providerReference").GetString()!; return _bookings.TryGetValue(reference, out var status) ? Ok(new { providerReference = reference, status }) : Fail("BOOKING_NOT_FOUND"); }
    private CapabilityInvocationResult Cancel(JsonElement input) { var reference = input.GetProperty("providerReference").GetString()!; if (!_bookings.ContainsKey(reference)) return Fail("BOOKING_NOT_FOUND"); _bookings[reference] = "Cancelled"; return Ok(new { providerReference = reference, status = "cancelled", refundStatus = "pending_policy_evaluation" }); }
    private static CapabilityInvocationResult Amend(JsonElement input) => Ok(new { status = "requote_required", providerReference = input.GetProperty("providerReference").GetString() });
    private static CapabilityInvocationResult Ok(object value) => new(true, JsonSerializer.SerializeToElement(value)); private static CapabilityInvocationResult Fail(string error) => new(false, JsonSerializer.SerializeToElement(new { error }), error);
}

public sealed class HotelDomainCapability : IServiceDomainCapability
{
    private static readonly Regex Dates = new(@"(?<in>\d{4}-\d{2}-\d{2}).*?(?<out>\d{4}-\d{2}-\d{2})", RegexOptions.Compiled);
    public string DomainId => "hotel"; public bool CanHandle(IServiceConnector provider, ServicePlanningContext context) => new[] { "search", "get_quote", "reserve", "book" }.All(required => provider.Capabilities.Any(x => x.Name == required));
    public async Task<ServicePlan> PlanAsync(IServiceConnector provider, ServicePlanningContext context, CancellationToken token = default)
    {
        var dates = Dates.Match(context.Instruction); if (!dates.Success) throw new ArgumentException("Provide check-in and check-out dates as YYYY-MM-DD."); var checkIn = DateOnly.ParseExact(dates.Groups["in"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture); var checkOut = DateOnly.ParseExact(dates.Groups["out"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture); if (checkIn < DateOnly.FromDateTime(DateTime.UtcNow) || checkOut <= checkIn) throw new ArgumentException("Check-out must be after a future check-in date.");
        var guestMatch = Regex.Match(context.Instruction, @"(?<n>\d+)\s+(?:guest|guests|people|person)", RegexOptions.IgnoreCase); var guests = guestMatch.Success ? int.Parse(guestMatch.Groups["n"].Value) : 1; if (guests < 1 || guests > 8) throw new ArgumentException("Guests must be between 1 and 8."); var accessible = context.Instruction.Contains("accessible", StringComparison.OrdinalIgnoreCase) || context.Instruction.Contains("wheelchair", StringComparison.OrdinalIgnoreCase);
        var budgetMatch = Regex.Match(context.Instruction, @"(?:£|GBP\s*)(\d+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase); if (!budgetMatch.Success) throw new ArgumentException("A maximum GBP budget is required."); var budget = decimal.Parse(budgetMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var searchInput = JsonSerializer.SerializeToElement(new { destination = Destination(context.Instruction), checkIn, checkOut, guests, accessible }); var search = await provider.InvokeAsync("search", searchInput, token); if (!search.Success) throw new InvalidOperationException(search.Error); var rooms = search.Value.GetProperty("rooms"); if (rooms.GetArrayLength() == 0) throw new InvalidOperationException("NO_MATCHING_ROOM"); var room = rooms[0];
        var quoteResult = await provider.InvokeAsync("get_quote", JsonSerializer.SerializeToElement(new { roomId = room.GetProperty("id").GetString(), checkIn, checkOut }), token); if (!quoteResult.Success) throw new InvalidOperationException(quoteResult.Error); var value = quoteResult.Value; var currency = value.GetProperty("currency").GetString()!; var total = value.GetProperty("total").GetDecimal();
        var configuration = JsonSerializer.SerializeToElement(new { roomId = room.GetProperty("id").GetString(), checkIn, checkOut, guests, accessible, destination = Destination(context.Instruction) }); var proposal = new ServiceActionProposal($"proposal_{Guid.NewGuid():N}", context.PrincipalId, context.AgentId, provider.ProviderId, "book", context.Instruction, configuration, budget, currency, $"hotel_{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{context.PrincipalId}|{context.Instruction}"))).ToLowerInvariant()[..32]}"); var quote = new ServiceQuote(value.GetProperty("quoteId").GetString()!, provider.ProviderId, "book", total, currency, value.GetProperty("expiresAt").GetDateTimeOffset(), value); return new(proposal, quote, ["search", "get_quote"]);
    }
    private static string Destination(string instruction) { var match = Regex.Match(instruction, @"\bin\s+(?<place>[A-Za-z][A-Za-z ]+?)(?=\s+(?:from|between|on)\s+\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase); return match.Success ? match.Groups["place"].Value.Trim() : "unspecified"; }
}
