using System.Security.Cryptography;
using System.Text;

namespace AgentTrust.Commerce;

public enum HotelBookingStatus
{
    Planned, Quoted, Reserved, Authorised, PaymentProcessing, Confirmed,
    Cancelled, Failed, Expired, RefundPending, Refunded
}

public sealed record HotelBooking(
    string BookingId, string PrincipalId, string AgentId, string MandateId,
    string PaymentMethodId, string ProviderId, string Objective, string RoomId,
    DateOnly CheckIn, DateOnly CheckOut, int Guests, bool Accessible,
    decimal Total, string Currency, string QuoteId, string? ReservationId,
    string? ProviderReference, string? PaymentReference, string IdempotencyKey,
    HotelBookingStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? FailureReason = null);

public sealed record HotelQuoteRecord(
    string QuoteId, string BookingId, string ProviderId, decimal Total,
    string Currency, DateTimeOffset ExpiresAt, string DetailsJson, DateTimeOffset CreatedAt);

public sealed record HotelReservationRecord(
    string ReservationId, string BookingId, string ProviderId, DateTimeOffset ExpiresAt,
    string Status, DateTimeOffset CreatedAt);

public sealed record HotelBookingAuditEvent(
    long Sequence, string EventId, string BookingId, string PrincipalId,
    string EventType, string PreviousHash, string CurrentHash, string DataJson,
    DateTimeOffset Timestamp);

public interface IHotelBookingStore
{
    bool TryCreateBooking(HotelBooking booking, out HotelBooking persisted);
    HotelBooking? FindOwned(string bookingId, string principalId);
    HotelBooking? FindByIdempotencyKey(string key);
    void SaveBooking(HotelBooking booking);
    void SaveQuote(HotelQuoteRecord quote);
    void SaveReservation(HotelReservationRecord reservation);
    HotelQuoteRecord? FindQuote(string quoteId);
    HotelReservationRecord? FindReservation(string reservationId);
    IReadOnlyList<HotelBookingAuditEvent> Audit(string bookingId);
    void AppendAudit(string bookingId, string principalId, string eventType, string dataJson, DateTimeOffset now);
    IReadOnlyList<HotelBooking> Recoverable(DateTimeOffset now);
}

public static class HotelBookingAuditHash
{
    public static string Compute(
        string previous, string booking, string principal, string type,
        string data, DateTimeOffset timestamp)
    {
        var content = $"{previous}|{booking}|{principal}|{type}|{data}|{timestamp:O}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}

public sealed class InMemoryHotelBookingStore : IHotelBookingStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HotelBooking> _bookings = [];
    private readonly Dictionary<string, HotelQuoteRecord> _quotes = [];
    private readonly Dictionary<string, HotelReservationRecord> _reservations = [];
    private readonly List<HotelBookingAuditEvent> _audit = [];

    public bool TryCreateBooking(HotelBooking booking, out HotelBooking persisted)
    {
        lock (_gate)
        {
            var existing = _bookings.Values.FirstOrDefault(x => x.IdempotencyKey == booking.IdempotencyKey);
            if (existing is not null)
            {
                persisted = existing;
                return false;
            }

            _bookings[booking.BookingId] = booking;
            persisted = booking;
            return true;
        }
    }

    public HotelBooking? FindOwned(string id, string principal)
    {
        lock (_gate)
            return _bookings.GetValueOrDefault(id) is { } booking && booking.PrincipalId == principal
                ? booking
                : null;
    }

    public HotelBooking? FindByIdempotencyKey(string key)
    {
        lock (_gate)
            return _bookings.Values.FirstOrDefault(x => x.IdempotencyKey == key);
    }

    public void SaveBooking(HotelBooking booking)
    {
        lock (_gate)
            _bookings[booking.BookingId] = booking;
    }

    public void SaveQuote(HotelQuoteRecord quote)
    {
        lock (_gate)
            _quotes.TryAdd(quote.QuoteId, quote);
    }

    public void SaveReservation(HotelReservationRecord reservation)
    {
        lock (_gate)
            _reservations[reservation.ReservationId] = reservation;
    }

    public HotelQuoteRecord? FindQuote(string id)
    {
        lock (_gate)
            return _quotes.GetValueOrDefault(id);
    }

    public HotelReservationRecord? FindReservation(string id)
    {
        lock (_gate)
            return _reservations.GetValueOrDefault(id);
    }

    public IReadOnlyList<HotelBookingAuditEvent> Audit(string id)
    {
        lock (_gate)
            return _audit.Where(x => x.BookingId == id).OrderBy(x => x.Sequence).ToArray();
    }

    public void AppendAudit(string booking, string principal, string type, string data, DateTimeOffset now)
    {
        lock (_gate)
        {
            var previous = _audit.LastOrDefault(x => x.BookingId == booking)?.CurrentHash ?? "GENESIS";
            var hash = HotelBookingAuditHash.Compute(previous, booking, principal, type, data, now);
            _audit.Add(new(
                _audit.Count + 1, $"hba_{Guid.NewGuid():N}", booking, principal, type,
                previous, hash, data, now));
        }
    }

    public IReadOnlyList<HotelBooking> Recoverable(DateTimeOffset now)
    {
        lock (_gate)
            return _bookings.Values
                .Where(x => x.Status is HotelBookingStatus.Reserved
                    or HotelBookingStatus.Authorised
                    or HotelBookingStatus.PaymentProcessing)
                .ToArray();
    }
}
