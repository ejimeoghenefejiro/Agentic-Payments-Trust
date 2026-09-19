using System.Security.Cryptography;
using System.Text;
using AgentTrust.Commerce;
namespace AgentTrust.Data;
public sealed class EfHotelBookingStore(AgentTrustDbContext db) : IHotelBookingStore
{
    public bool TryCreateBooking(HotelBooking booking, out HotelBooking persisted)
    {
        if (FindByIdempotencyKey(booking.IdempotencyKey) is { } existing)
        {
            persisted = existing;
            return false;
        }

        var row = ToEntity(booking);
        db.HotelBookings.Add(row);
        try
        {
            db.SaveChanges();
            persisted = booking;
            return true;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            db.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            var concurrent = FindByIdempotencyKey(booking.IdempotencyKey);
            if (concurrent is null) throw;
            persisted = concurrent;
            return false;
        }
    }
    public HotelBooking? FindOwned(string id, string principal) => db.HotelBookings.Where(x => x.BookingId == id && x.PrincipalId == principal).Select(Map).SingleOrDefault();
    public HotelBooking? FindByIdempotencyKey(string key) => db.HotelBookings.Where(x => x.IdempotencyKey == key).Select(Map).SingleOrDefault();
    public void SaveBooking(HotelBooking b) { var row = db.HotelBookings.Find(b.BookingId); if (row is null) db.HotelBookings.Add(ToEntity(b)); else Apply(row, b); db.SaveChanges(); }
    public void SaveQuote(HotelQuoteRecord q) { if (db.HotelQuotes.Find(q.QuoteId) is null) { db.HotelQuotes.Add(new() { QuoteId = q.QuoteId, BookingId = q.BookingId, ProviderId = q.ProviderId, Total = q.Total, Currency = q.Currency, ExpiresAt = q.ExpiresAt, DetailsJson = q.DetailsJson, CreatedAt = q.CreatedAt }); db.SaveChanges(); } }
    public void SaveReservation(HotelReservationRecord r) { var row = db.HotelReservations.Find(r.ReservationId); if (row is null) db.HotelReservations.Add(new() { ReservationId = r.ReservationId, BookingId = r.BookingId, ProviderId = r.ProviderId, ExpiresAt = r.ExpiresAt, Status = r.Status, CreatedAt = r.CreatedAt }); else row.Status = r.Status; db.SaveChanges(); }
    public HotelQuoteRecord? FindQuote(string id) => db.HotelQuotes.Where(x => x.QuoteId == id).Select(x => new HotelQuoteRecord(x.QuoteId, x.BookingId, x.ProviderId, x.Total, x.Currency, x.ExpiresAt, x.DetailsJson, x.CreatedAt)).SingleOrDefault();
    public HotelReservationRecord? FindReservation(string id) => db.HotelReservations.Where(x => x.ReservationId == id).Select(x => new HotelReservationRecord(x.ReservationId, x.BookingId, x.ProviderId, x.ExpiresAt, x.Status, x.CreatedAt)).SingleOrDefault();
    public IReadOnlyList<HotelBookingAuditEvent> Audit(string id) => db.HotelBookingAudits.Where(x => x.BookingId == id).OrderBy(x => x.Sequence).Select(x => new HotelBookingAuditEvent(x.Sequence, x.EventId, x.BookingId, x.PrincipalId, x.EventType, x.PreviousHash, x.CurrentHash, x.DataJson, x.Timestamp)).ToArray();
    public void AppendAudit(string booking, string principal, string type, string data, DateTimeOffset now) { var previous = db.HotelBookingAudits.Where(x => x.BookingId == booking).OrderByDescending(x => x.Sequence).Select(x => x.CurrentHash).FirstOrDefault() ?? "GENESIS"; var hash = HotelBookingAuditHash.Compute(previous,booking,principal,type,data,now); db.HotelBookingAudits.Add(new() { EventId = $"hba_{Guid.NewGuid():N}", BookingId = booking, PrincipalId = principal, EventType = type, PreviousHash = previous, CurrentHash = hash, DataJson = data, Timestamp = now }); db.SaveChanges(); }
    public IReadOnlyList<HotelBooking> Recoverable(DateTimeOffset now) => db.HotelBookings.Where(x => x.Status == "Reserved" || x.Status == "Authorised" || x.Status == "PaymentProcessing").Select(Map).ToArray();
    private static HotelBooking Map(HotelBookingEntity x) => new(x.BookingId, x.PrincipalId, x.AgentId, x.MandateId, x.PaymentMethodId, x.ProviderId, x.Objective, x.RoomId, x.CheckIn, x.CheckOut, x.Guests, x.Accessible, x.Total, x.Currency, x.QuoteId, x.ReservationId, x.ProviderReference, x.PaymentReference, x.IdempotencyKey, Enum.Parse<HotelBookingStatus>(x.Status), x.CreatedAt, x.UpdatedAt, x.FailureReason);
    private static HotelBookingEntity ToEntity(HotelBooking b) { var x = new HotelBookingEntity(); Apply(x, b); return x; }
    private static void Apply(HotelBookingEntity x, HotelBooking b) { x.BookingId = b.BookingId; x.PrincipalId = b.PrincipalId; x.AgentId = b.AgentId; x.MandateId = b.MandateId; x.PaymentMethodId = b.PaymentMethodId; x.ProviderId = b.ProviderId; x.Objective = b.Objective; x.RoomId = b.RoomId; x.CheckIn = b.CheckIn; x.CheckOut = b.CheckOut; x.Guests = b.Guests; x.Accessible = b.Accessible; x.Total = b.Total; x.Currency = b.Currency; x.QuoteId = b.QuoteId; x.ReservationId = b.ReservationId; x.ProviderReference = b.ProviderReference; x.PaymentReference = b.PaymentReference; x.IdempotencyKey = b.IdempotencyKey; x.Status = b.Status.ToString(); x.CreatedAt = b.CreatedAt; x.UpdatedAt = b.UpdatedAt; x.FailureReason = b.FailureReason; }
}
