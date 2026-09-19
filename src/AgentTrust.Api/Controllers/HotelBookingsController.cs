using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentTrust.Commerce;
using AgentTrust.Core;
using AgentTrust.Mandates;
using AgentTrust.PaymentMethods;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/hotel/bookings")]
[Authorize(Policy = "Consumer")]
public sealed class HotelBookingsController(
    ServicePlanningRouter planner,
    ServiceConnectorRegistry providers,
    IHotelBookingStore bookings,
    IMandateStore mandates,
    IMandateUsageTracker usage,
    IPaymentMethodStore paymentMethods,
    IAgentRegistry agents,
    IPlatformPaymentProcessor payments,
    IServiceActionAuthorisationService authorisations) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "StepUp")]
    public async Task<ActionResult<object>> Book(HotelBookingRequest request, CancellationToken token)
    {
        var principal = PrincipalId();
        var now = DateTimeOffset.UtcNow;
        var agent = agents.Find(request.AgentId);
        if (agent is null || agent.PrincipalId != principal)
            return UnprocessableEntity(new { code = "AGENT_OWNERSHIP_MISMATCH" });

        var mandate = mandates.Find(request.MandateId);
        if (!IsSuitableMandate(mandate, principal, request, now))
            return UnprocessableEntity(new { code = "NO_SUITABLE_HOTEL_MANDATE" });

        var method = paymentMethods.Find(mandate!.PaymentMethodId);
        if (method is null
            || method.PrincipalId != principal
            || !method.IsUsable(DateOnly.FromDateTime(DateTime.UtcNow)))
            return UnprocessableEntity(new { code = "NO_USABLE_PAYMENT_METHOD" });

        ServicePlan plan;
        try
        {
            plan = await planner.PlanAsync(
                new(principal, request.AgentId, request.Instruction, request.ProviderId),
                token);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "INVALID_HOTEL_REQUEST", message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { code = "HOTEL_PROVIDER_UNAVAILABLE", message = ex.Message });
        }

        var proposal = BindIdempotency(mandate, plan.Proposal);
        if (!string.Equals(plan.Quote.Currency, mandate.Currency, StringComparison.OrdinalIgnoreCase)
            || plan.Quote.Total > mandate.PerTransactionLimit)
            return UnprocessableEntity(new
            {
                code = "MANDATE_LIMIT_EXCEEDED",
                total = plan.Quote.Total,
                currency = plan.Quote.Currency
            });

        var booking = CreateBooking(request, principal, mandate, method, proposal, plan, now);
        if (!bookings.TryCreateBooking(booking, out var persisted))
            return Ok(new { booking = persisted, replayed = true });

        booking = persisted;
        bookings.SaveQuote(new(
            plan.Quote.QuoteId,
            booking.BookingId,
            request.ProviderId,
            plan.Quote.Total,
            plan.Quote.Currency,
            plan.Quote.ExpiresAt,
            plan.Quote.Details.GetRawText(),
            now));
        AppendAudit(booking, "HotelQuoteAccepted", new
        {
            plan.Quote.QuoteId,
            plan.Quote.Total,
            plan.Quote.Currency
        });

        if (!usage.TryReserve(
                mandate,
                booking.BookingId,
                plan.Quote.Total,
                now,
                out var spendReservation,
                out var usageFailures))
        {
            booking = booking with
            {
                Status = HotelBookingStatus.Failed,
                FailureReason = string.Join(',', usageFailures),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            bookings.SaveBooking(booking);
            AppendAudit(booking, "HotelMandateUsageDenied", usageFailures);
            return UnprocessableEntity(new { booking, reasons = usageFailures });
        }

        var policy = new BoundedServiceActionTrustPolicy(
            new HashSet<string>([mandate.Merchant], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["book"], StringComparer.OrdinalIgnoreCase));
        var orchestrator = new TrustedServiceActionOrchestrator(policy, authorisations);
        var provider = providers.GetRequired(request.ProviderId);
        TrustedServiceActionResult outcome;
        try
        {
            outcome = await orchestrator.ExecuteAsync(
                proposal,
                plan.Quote,
                provider,
                now,
                token,
                async (_, quote, actionAuthorisation, providerReservationId, cancellationToken) =>
            {
                var reservedAt = DateTimeOffset.UtcNow;
                booking = booking with
                {
                    ReservationId = providerReservationId,
                    Status = HotelBookingStatus.Reserved,
                    UpdatedAt = reservedAt
                };
                bookings.SaveBooking(booking);
                bookings.SaveReservation(new(
                    providerReservationId,
                    booking.BookingId,
                    booking.ProviderId,
                    quote.ExpiresAt,
                    "Held",
                    reservedAt));
                AppendAudit(booking, "HotelReservationHeld", new { providerReservationId, quote.ExpiresAt });
                AppendAudit(booking, "ServiceActionAuthorised", new
                {
                    actionAuthorisation.AuthorisationId,
                    actionAuthorisation.PolicyVersion
                });

                var intent = CreatePaymentIntent(
                    booking,
                    mandate,
                    method,
                    provider.ProviderName,
                    quote,
                    now);
                var payment = await payments.ProcessAsync(intent, cancellationToken);
                AppendAudit(booking, "HotelPaymentSubmitted", new
                {
                    payment.Status,
                    payment.ProviderReference
                });

                if (payment.Status == PlatformPaymentStatus.Succeeded)
                {
                    booking = booking with
                    {
                        PaymentReference = payment.ProviderReference,
                        Status = HotelBookingStatus.Authorised,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    bookings.SaveBooking(booking);
                    return null;
                }

                var status = payment.Status == PlatformPaymentStatus.Processing
                    ? HotelBookingStatus.PaymentProcessing
                    : HotelBookingStatus.Failed;
                booking = booking with
                {
                    PaymentReference = payment.ProviderReference,
                    Status = status,
                    FailureReason = payment.FailureReason,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                bookings.SaveBooking(booking);

                if (status == HotelBookingStatus.Failed)
                {
                    usage.Release(spendReservation!.ReservationId);
                    bookings.SaveReservation(new(
                        providerReservationId,
                        booking.BookingId,
                        booking.ProviderId,
                        quote.ExpiresAt,
                        "Released",
                        reservedAt));
                }

                return new ServiceExecutionResult(
                    false,
                    status.ToString(),
                    payment.ProviderReference,
                    JsonSerializer.SerializeToElement(payment),
                    payment.FailureReason ?? payment.RequiredAction);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PlatformRefundResult? compensation = null;
            if (booking.PaymentReference is not null)
            {
                compensation = await payments.RefundAsync(
                    booking.PaymentReference,
                    booking.Total,
                    booking.Currency,
                    $"{booking.IdempotencyKey}_exception_compensation",
                    token);
                if (compensation.Status == PlatformRefundStatus.Succeeded)
                    usage.Release(spendReservation!.ReservationId);
                else if (compensation.Status == PlatformRefundStatus.Failed)
                    usage.Commit(spendReservation!.ReservationId);
            }
            else
            {
                usage.Release(spendReservation!.ReservationId);
            }

            if (booking.ReservationId is not null)
            {
                await provider.InvokeAsync(
                    "release",
                    JsonSerializer.SerializeToElement(new { reservationId = booking.ReservationId }),
                    token);
                bookings.SaveReservation(new(
                    booking.ReservationId,
                    booking.BookingId,
                    booking.ProviderId,
                    plan.Quote.ExpiresAt,
                    "Released",
                    now));
            }

            booking = booking with
            {
                Status = compensation?.Status switch
                {
                    PlatformRefundStatus.Succeeded => HotelBookingStatus.Refunded,
                    PlatformRefundStatus.Processing or PlatformRefundStatus.Failed => HotelBookingStatus.RefundPending,
                    _ => HotelBookingStatus.Failed
                },
                FailureReason = "HOTEL_EXECUTION_ERROR",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            bookings.SaveBooking(booking);
            AppendAudit(booking, "HotelExecutionError", new { error = ex.GetType().Name, compensation });
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                code = "HOTEL_EXECUTION_ERROR",
                booking
            });
        }

        if (!outcome.TrustDecision.Approved)
        {
            usage.Release(spendReservation!.ReservationId);
            booking = booking with
            {
                Status = HotelBookingStatus.Failed,
                FailureReason = string.Join(',', outcome.TrustDecision.Reasons),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            bookings.SaveBooking(booking);
            AppendAudit(booking, "HotelTrustDenied", outcome.TrustDecision);
            return UnprocessableEntity(new { booking, outcome.TrustDecision });
        }

        if (outcome.Execution?.Succeeded == true)
        {
            var evidence = outcome.Execution.Evidence;
            var reservationId = evidence.GetProperty("reservationId").GetString()!;
            usage.Commit(spendReservation!.ReservationId);
            booking = booking with
            {
                ReservationId = reservationId,
                ProviderReference = outcome.Execution.ProviderReference,
                Status = HotelBookingStatus.Confirmed,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            bookings.SaveBooking(booking);
            bookings.SaveReservation(new(
                reservationId,
                booking.BookingId,
                booking.ProviderId,
                plan.Quote.ExpiresAt,
                "Consumed",
                now));
            AppendAudit(booking, "HotelBookingConfirmed", evidence);
            return CreatedAtAction(nameof(Get), new { id = booking.BookingId }, new { booking, confirmation = evidence });
        }

        if (booking.Status == HotelBookingStatus.PaymentProcessing)
            return AcceptedAtAction(nameof(Get), new { id = booking.BookingId }, new { booking, outcome.Execution });

        if (booking.PaymentReference is not null)
        {
            var refund = await payments.RefundAsync(
                booking.PaymentReference,
                booking.Total,
                booking.Currency,
                $"{booking.IdempotencyKey}_compensation",
                token);
            if (refund.Status == PlatformRefundStatus.Succeeded)
                usage.Release(spendReservation!.ReservationId);
            else if (refund.Status == PlatformRefundStatus.Failed)
                usage.Commit(spendReservation!.ReservationId);
            booking = booking with
            {
                Status = refund.Status == PlatformRefundStatus.Succeeded
                    ? HotelBookingStatus.Refunded
                    : HotelBookingStatus.RefundPending,
                FailureReason = outcome.Execution?.Error,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            AppendAudit(booking, "HotelBookingCompensation", refund);
        }
        else
        {
            usage.Release(spendReservation!.ReservationId);
            booking = booking with
            {
                Status = HotelBookingStatus.Failed,
                FailureReason = outcome.Execution?.Error,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        if (booking.ReservationId is not null)
        {
            bookings.SaveReservation(new(
                booking.ReservationId,
                booking.BookingId,
                booking.ProviderId,
                plan.Quote.ExpiresAt,
                "Released",
                now));
        }

        bookings.SaveBooking(booking);
        AppendAudit(booking, "HotelBookingFailed", outcome.Execution);
        return UnprocessableEntity(new { booking, outcome.Execution });
    }

    [HttpGet("{id}")]
    public ActionResult<HotelBooking> Get(string id) =>
        bookings.FindOwned(id, PrincipalId()) is { } booking ? Ok(booking) : NotFound();

    [HttpGet("{id}/confirmation")]
    public async Task<ActionResult<object>> Confirmation(string id, CancellationToken token)
    {
        var booking = bookings.FindOwned(id, PrincipalId());
        if (booking?.ProviderReference is null) return NotFound();

        var result = await providers.GetRequired(booking.ProviderId).InvokeAsync(
            "confirmation",
            JsonSerializer.SerializeToElement(new { providerReference = booking.ProviderReference }),
            token);
        return result.Success ? Ok(result.Value) : UnprocessableEntity(new { result.Error });
    }

    [HttpGet("{id}/audit")]
    public ActionResult<object> Audit(string id)
    {
        if (bookings.FindOwned(id, PrincipalId()) is null) return NotFound();

        var events = bookings.Audit(id);
        var previous = "GENESIS";
        var valid = true;
        foreach (var item in events)
        {
            var expected = HotelBookingAuditHash.Compute(
                previous,
                item.BookingId,
                item.PrincipalId,
                item.EventType,
                item.DataJson,
                item.Timestamp);
            valid &= item.PreviousHash == previous && item.CurrentHash == expected;
            previous = item.CurrentHash;
        }

        return Ok(new { isValid = valid, events });
    }

    [HttpPost("{id}/refresh-quote")]
    [Authorize(Policy = "StepUp")]
    public async Task<ActionResult<object>> RefreshQuote(string id, CancellationToken token)
    {
        var booking = bookings.FindOwned(id, PrincipalId());
        if (booking is null) return NotFound();
        if (booking.Status is HotelBookingStatus.Confirmed or HotelBookingStatus.Cancelled or HotelBookingStatus.Refunded)
            return Conflict(new { code = "BOOKING_ALREADY_TERMINAL" });

        var result = await providers.GetRequired(booking.ProviderId).InvokeAsync(
            "refresh_quote",
            JsonSerializer.SerializeToElement(new
            {
                roomId = booking.RoomId,
                checkIn = booking.CheckIn,
                checkOut = booking.CheckOut
            }),
            token);
        if (!result.Success) return UnprocessableEntity(new { result.Error });

        var quote = new HotelQuoteRecord(
            result.Value.GetProperty("quoteId").GetString()!,
            booking.BookingId,
            booking.ProviderId,
            result.Value.GetProperty("total").GetDecimal(),
            result.Value.GetProperty("currency").GetString()!,
            result.Value.GetProperty("expiresAt").GetDateTimeOffset(),
            result.Value.GetRawText(),
            DateTimeOffset.UtcNow);
        bookings.SaveQuote(quote);
        var updated = booking with
        {
            QuoteId = quote.QuoteId,
            Total = quote.Total,
            Currency = quote.Currency,
            Status = HotelBookingStatus.Quoted,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        bookings.SaveBooking(updated);
        AppendAudit(updated, "HotelQuoteRefreshed", result.Value);
        return Ok(new { booking = updated, quote });
    }

    [HttpPost("{id}/amend")]
    [Authorize(Policy = "StepUp")]
    public async Task<ActionResult<object>> Amend(
        string id,
        HotelAmendmentRequest request,
        CancellationToken token)
    {
        var booking = bookings.FindOwned(id, PrincipalId());
        if (booking?.ProviderReference is null) return NotFound();
        if (request.CheckOut <= request.CheckIn)
            return BadRequest(new { code = "INVALID_STAY_DATES" });

        var result = await providers.GetRequired(booking.ProviderId).InvokeAsync(
            "amend",
            JsonSerializer.SerializeToElement(new
            {
                providerReference = booking.ProviderReference,
                request.CheckIn,
                request.CheckOut
            }),
            token);
        if (!result.Success) return UnprocessableEntity(new { result.Error });

        AppendAudit(booking, "HotelAmendmentRequested", result.Value);
        return Ok(new
        {
            status = "REQUOTE_REQUIRED",
            provider = result.Value,
            message = "The amendment is not applied until a fresh quote passes mandate, payment and trust checks."
        });
    }

    [HttpPost("{id}/cancel")]
    [Authorize(Policy = "StepUp")]
    public async Task<ActionResult<object>> Cancel(string id, CancellationToken token)
    {
        var booking = bookings.FindOwned(id, PrincipalId());
        if (booking?.ProviderReference is null) return NotFound();
        if (booking.Status is HotelBookingStatus.Cancelled or HotelBookingStatus.Refunded)
            return Ok(new { booking, replayed = true });

        var result = await providers.GetRequired(booking.ProviderId).InvokeAsync(
            "cancel",
            JsonSerializer.SerializeToElement(new { providerReference = booking.ProviderReference }),
            token);
        if (!result.Success) return UnprocessableEntity(new { result.Error });

        var refundAmount = CalculateRefund(booking, bookings.FindQuote(booking.QuoteId));
        PlatformRefundResult? refund = null;
        if (booking.PaymentReference is not null && refundAmount > 0)
        {
            refund = await payments.RefundAsync(
                booking.PaymentReference,
                refundAmount,
                booking.Currency,
                $"{booking.IdempotencyKey}_cancel",
                token);
        }

        var updated = booking with
        {
            Status = refund?.Status switch
            {
                PlatformRefundStatus.Succeeded => HotelBookingStatus.Refunded,
                PlatformRefundStatus.Processing or PlatformRefundStatus.Failed => HotelBookingStatus.RefundPending,
                _ => HotelBookingStatus.Cancelled
            },
            UpdatedAt = DateTimeOffset.UtcNow
        };
        bookings.SaveBooking(updated);
        AppendAudit(updated, "HotelBookingCancelled", new { provider = result.Value, refundAmount, refund });
        return Ok(new { booking = updated, provider = result.Value, refundAmount, refund });
    }

    private void AppendAudit(HotelBooking booking, string eventType, object? data) =>
        bookings.AppendAudit(
            booking.BookingId,
            booking.PrincipalId,
            eventType,
            JsonSerializer.Serialize(data),
            DateTimeOffset.UtcNow);

    private string PrincipalId() =>
        User.FindFirstValue("agenttrust_principal_id")
        ?? throw new UnauthorizedAccessException("Stable principal claim missing.");

    private static bool IsSuitableMandate(
        FinancialMandate? mandate,
        string principal,
        HotelBookingRequest request,
        DateTimeOffset now) =>
        mandate is not null
        && mandate.PrincipalId == principal
        && mandate.AgentId == request.AgentId
        && mandate.IsActive(now)
        && string.Equals(mandate.Purpose, "hotel", StringComparison.OrdinalIgnoreCase)
        && string.Equals(mandate.Merchant, request.ProviderId, StringComparison.OrdinalIgnoreCase);

    private static ServiceActionProposal BindIdempotency(
        FinancialMandate mandate,
        ServiceActionProposal proposal)
    {
        var content = $"{proposal.PrincipalId}|{mandate.MandateId}|{proposal.Objective}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return proposal with { IdempotencyKey = $"hotel_{hash[..32]}" };
    }

    private static HotelBooking CreateBooking(
        HotelBookingRequest request,
        string principal,
        FinancialMandate mandate,
        PaymentMethod method,
        ServiceActionProposal proposal,
        ServicePlan plan,
        DateTimeOffset now)
    {
        var configuration = proposal.Configuration;
        return new(
            $"hotel_booking_{Guid.NewGuid():N}",
            principal,
            request.AgentId,
            mandate.MandateId,
            method.PaymentMethodId,
            request.ProviderId,
            request.Instruction,
            configuration.GetProperty("roomId").GetString()!,
            DateOnly.Parse(configuration.GetProperty("checkIn").GetString()!),
            DateOnly.Parse(configuration.GetProperty("checkOut").GetString()!),
            configuration.GetProperty("guests").GetInt32(),
            configuration.GetProperty("accessible").GetBoolean(),
            plan.Quote.Total,
            plan.Quote.Currency,
            plan.Quote.QuoteId,
            null,
            null,
            null,
            proposal.IdempotencyKey,
            HotelBookingStatus.Quoted,
            now,
            now);
    }

    private static PurchaseIntent CreatePaymentIntent(
        HotelBooking booking,
        FinancialMandate mandate,
        PaymentMethod method,
        string providerName,
        ServiceQuote quote,
        DateTimeOffset createdAt)
    {
        return new(
            $"hotel_payment_{booking.BookingId}",
            booking.PrincipalId,
            booking.AgentId,
            mandate.MandateId,
            booking.BookingId,
            booking.ProviderId,
            providerName,
            quote.Currency,
            [new(
                booking.RoomId,
                $"Hotel stay {booking.CheckIn:yyyy-MM-dd} to {booking.CheckOut:yyyy-MM-dd}",
                1,
                quote.Total,
                quote.Total,
                false)],
            quote.Total,
            0,
            quote.Total,
            "hotel-booking",
            null,
            method.PaymentMethodId,
            createdAt,
            quote.ExpiresAt,
            booking.IdempotencyKey);
    }

    private static decimal CalculateRefund(HotelBooking booking, HotelQuoteRecord? quote)
    {
        if (quote is null) return 0;

        try
        {
            using var document = JsonDocument.Parse(quote.DetailsJson);
            var cancellation = document.RootElement.GetProperty("cancellation");
            var freeUntil = cancellation.GetProperty("freeUntil").GetDateTimeOffset();
            var lateFee = cancellation.GetProperty("lateCancellationFee").GetDecimal();
            return DateTimeOffset.UtcNow <= freeUntil
                ? booking.Total
                : Math.Max(0, booking.Total - lateFee);
        }
        catch (JsonException)
        {
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }
}

public sealed record HotelBookingRequest(
    string Instruction,
    string AgentId,
    string MandateId,
    string ProviderId = "hotel-demo");

public sealed record HotelAmendmentRequest(DateOnly CheckIn, DateOnly CheckOut);
