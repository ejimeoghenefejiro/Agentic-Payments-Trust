using System.Text.Json;
using AgentTrust.Commerce;
namespace AgentTrust.Api;
public sealed class HotelBookingRecoveryWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<HotelBookingRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("HotelBookings:RecoveryIntervalSeconds", 60), 10, 3600));
        while (!stoppingToken.IsCancellationRequested) { try { await Recover(stoppingToken); } catch (Exception ex) { logger.LogError(ex, "Hotel booking recovery cycle failed."); } await Task.Delay(interval, stoppingToken); }
    }
    private async Task Recover(CancellationToken token)
    {
        using var scope = scopes.CreateScope(); var store = scope.ServiceProvider.GetRequiredService<IHotelBookingStore>(); var providers = scope.ServiceProvider.GetRequiredService<ServiceConnectorRegistry>(); var now = DateTimeOffset.UtcNow;
        foreach (var booking in store.Recoverable(now))
        {
            if (booking.ProviderReference is not null) { var status = await providers.GetRequired(booking.ProviderId).InvokeAsync("status", JsonSerializer.SerializeToElement(new { providerReference = booking.ProviderReference }), token); if (status.Success) { var value = status.Value.GetProperty("status").GetString(); var terminal = value?.Equals("Confirmed", StringComparison.OrdinalIgnoreCase) == true ? HotelBookingStatus.Confirmed : value?.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) == true ? HotelBookingStatus.Cancelled : booking.Status; if (terminal != booking.Status) { store.SaveBooking(booking with { Status = terminal, UpdatedAt = now }); store.AppendAudit(booking.BookingId, booking.PrincipalId, "HotelBookingReconciled", status.Value.GetRawText(), now); } } continue; }
            var quote = store.FindQuote(booking.QuoteId); if (quote?.ExpiresAt <= now) { if (booking.ReservationId is not null) await providers.GetRequired(booking.ProviderId).InvokeAsync("release", JsonSerializer.SerializeToElement(new { reservationId = booking.ReservationId }), token); store.SaveBooking(booking with { Status = HotelBookingStatus.Expired, FailureReason = "QUOTE_OR_RESERVATION_EXPIRED", UpdatedAt = now }); store.AppendAudit(booking.BookingId, booking.PrincipalId, "HotelBookingExpired", "{}", now); }
        }
    }
}
