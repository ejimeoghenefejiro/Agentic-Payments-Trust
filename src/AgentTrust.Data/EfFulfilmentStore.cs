using System.Text.Json;
using AgentTrust.Commerce;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Data;

public sealed class EfFulfilmentStore(AgentTrustDbContext db) : IFulfilmentStore, IFulfilmentWebhookHandler
{
    public void SaveQuote(FulfilmentQuote quote)
    {
        var existing = db.FulfilmentQuotes.SingleOrDefault(x => x.QuoteId == quote.QuoteId);
        if (existing is null)
        {
            db.FulfilmentQuotes.Add(new FulfilmentQuoteEntity
            {
                QuoteId = quote.QuoteId, ProviderId = quote.ProviderId, Mode = quote.Mode.ToString(),
                Currency = quote.Currency, Total = quote.Total, QuoteHash = HashQuote(quote),
                ProviderReference = quote.ProviderReference, QuoteJson = JsonSerializer.Serialize(quote),
                ExpiresAt = quote.ExpiresAt, CreatedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
            return;
        }

        if (existing.QuoteHash != HashQuote(quote))
            throw new InvalidOperationException("QUOTE_MUTATION_DETECTED");
    }

    public void SaveIntent(FulfilmentIntent intent)
    {
        var existing = db.FulfilmentIntents.SingleOrDefault(x => x.FulfilmentIntentId == intent.FulfilmentIntentId);
        if (existing is not null)
        {
            if (existing.QuoteHash != intent.QuoteHash || existing.ProviderId != intent.ProviderId || existing.TotalAmount != intent.TotalAmount)
                throw new InvalidOperationException("IDEMPOTENCY_CONFLICT");
            return;
        }

        db.FulfilmentIntents.Add(new FulfilmentIntentEntity
        {
            FulfilmentIntentId = intent.FulfilmentIntentId, PrincipalId = intent.PrincipalId, AgentId = intent.AgentId,
            ProviderId = intent.ProviderId, MerchantOrderId = intent.MerchantOrderId, Mode = intent.Mode.ToString(),
            QuoteId = intent.QuoteId, QuoteHash = intent.QuoteHash, DestinationReference = intent.DestinationReference,
            PickupLocationId = intent.PickupLocationId, Currency = intent.Currency, TotalAmount = intent.TotalAmount,
            CreatedAt = intent.CreatedAt, ExpiresAt = intent.ExpiresAt
        });
        db.SaveChanges();
    }

    public void SaveExecution(FulfilmentExecutionResult execution, string fulfilmentIntentId)
    {
        var intent = db.FulfilmentIntents.SingleOrDefault(x => x.FulfilmentIntentId == fulfilmentIntentId)
            ?? throw new InvalidOperationException("FULFILMENT_INTENT_NOT_FOUND");
        var row = db.FulfilmentExecutions.SingleOrDefault(x => x.FulfilmentIntentId == fulfilmentIntentId);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            db.FulfilmentExecutions.Add(new FulfilmentExecutionEntity
            {
                FulfilmentIntentId = fulfilmentIntentId, FulfilmentId = execution.FulfilmentId,
                IdempotencyKey = fulfilmentIntentId, ProviderId = intent.ProviderId, Status = execution.Status.ToString(),
                ProviderReference = execution.ProviderReference, CourierReference = execution.CourierReference,
                FailureReason = execution.FailureReason, CreatedAt = now, UpdatedAt = now
            });
        }
        else
        {
            var current = Enum.Parse<FulfilmentStatus>(row.Status);
            FulfilmentStateMachine.EnsureTransition(current, execution.Status);
            if (row.ProviderReference is not null && row.ProviderReference != execution.ProviderReference)
                throw new InvalidOperationException("IDEMPOTENCY_CONFLICT");
            row.FulfilmentId = execution.FulfilmentId;
            row.Status = execution.Status.ToString();
            row.ProviderReference = execution.ProviderReference;
            row.CourierReference = execution.CourierReference;
            row.FailureReason = execution.FailureReason;
            row.NextReconciliationAt = null;
            row.UpdatedAt = now;
            row.Version++;
        }
        db.SaveChanges();
    }

    public FulfilmentExecutionResult? FindExecution(string fulfilmentIntentId)
    {
        var row = db.FulfilmentExecutions.AsNoTracking().SingleOrDefault(x => x.FulfilmentIntentId == fulfilmentIntentId);
        return row is null ? null : ToResult(row);
    }

    public FulfilmentIntent? FindIntent(string fulfilmentIntentId)
    {
        var row = db.FulfilmentIntents.AsNoTracking().SingleOrDefault(x => x.FulfilmentIntentId == fulfilmentIntentId);
        return row is null ? null : ToIntent(row);
    }

    public IReadOnlyList<FulfilmentIntent> FindReconciliationCandidates(DateTimeOffset now, int limit) =>
        (from intent in db.FulfilmentIntents.AsNoTracking()
         join execution in db.FulfilmentExecutions.AsNoTracking() on intent.FulfilmentIntentId equals execution.FulfilmentIntentId
         where (execution.Status == nameof(FulfilmentStatus.Unknown) || execution.Status == nameof(FulfilmentStatus.Pending))
             && (execution.NextReconciliationAt == null || execution.NextReconciliationAt <= now)
             && execution.ReconciliationAttempts < 8
         orderby execution.UpdatedAt
         select intent).Take(limit).AsEnumerable().Select(ToIntent).ToArray();

    public void MarkUnknown(string fulfilmentIntentId, string reason, DateTimeOffset nextAttemptAt)
    {
        var intent = db.FulfilmentIntents.SingleOrDefault(x => x.FulfilmentIntentId == fulfilmentIntentId)
            ?? throw new InvalidOperationException("FULFILMENT_INTENT_NOT_FOUND");
        var row = db.FulfilmentExecutions.SingleOrDefault(x => x.FulfilmentIntentId == fulfilmentIntentId);
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new FulfilmentExecutionEntity { FulfilmentIntentId = fulfilmentIntentId, IdempotencyKey = fulfilmentIntentId,
                ProviderId = intent.ProviderId, Status = nameof(FulfilmentStatus.Unknown), FailureReason = reason,
                ReconciliationAttempts = 1, NextReconciliationAt = nextAttemptAt, CreatedAt = now, UpdatedAt = now };
            db.FulfilmentExecutions.Add(row);
        }
        else
        {
            if (Enum.Parse<FulfilmentStatus>(row.Status) != FulfilmentStatus.Unknown)
                FulfilmentStateMachine.EnsureTransition(Enum.Parse<FulfilmentStatus>(row.Status), FulfilmentStatus.Unknown);
            row.Status = nameof(FulfilmentStatus.Unknown); row.FailureReason = reason;
            row.ReconciliationAttempts++; row.NextReconciliationAt = nextAttemptAt; row.UpdatedAt = now; row.Version++;
        }
        db.SaveChanges();
    }

    public bool IsOwnedByProvider(string fulfilmentId, string providerId) =>
        (from execution in db.FulfilmentExecutions.AsNoTracking()
         join intent in db.FulfilmentIntents.AsNoTracking() on execution.FulfilmentIntentId equals intent.FulfilmentIntentId
         where execution.FulfilmentId == fulfilmentId && intent.ProviderId == providerId
         select execution).Any();

    public IReadOnlyList<FulfilmentStatusEvent> History(string fulfilmentId) => db.FulfilmentStatusHistory.AsNoTracking()
        .Where(x => x.FulfilmentId == fulfilmentId).OrderBy(x => x.SequenceNumber)
        .Select(x => new FulfilmentStatusEvent(x.ProviderEventId, x.FulfilmentId, Enum.Parse<FulfilmentStatus>(x.Status), x.ProviderTimestamp, x.PayloadHash)).ToArray();

    public bool AppendStatus(FulfilmentStatusEvent statusEvent)
    {
        if (db.FulfilmentWebhookEvents.Any(x => x.ProviderEventId == statusEvent.ProviderEventId)) return false;
        var execution = db.FulfilmentExecutions.SingleOrDefault(x => x.FulfilmentId == statusEvent.FulfilmentId)
            ?? throw new InvalidOperationException("FULFILMENT_NOT_FOUND");
        var current = Enum.Parse<FulfilmentStatus>(execution.Status);
        FulfilmentStateMachine.EnsureTransition(current, statusEvent.Status);
        var now = DateTimeOffset.UtcNow;
        using var transaction = db.Database.IsRelational() ? db.Database.BeginTransaction() : null;
        db.FulfilmentWebhookEvents.Add(new FulfilmentWebhookEventEntity
        {
            ProviderEventId = statusEvent.ProviderEventId, ProviderId = execution.ProviderId,
            PayloadHash = statusEvent.PayloadHash, Status = "Processed", ReceivedAt = now, ProcessedAt = now
        });
        db.FulfilmentStatusHistory.Add(new FulfilmentStatusHistoryEntity
        {
            ProviderEventId = statusEvent.ProviderEventId, FulfilmentId = statusEvent.FulfilmentId,
            PreviousStatus = current.ToString(), Status = statusEvent.Status.ToString(),
            ProviderTimestamp = statusEvent.ProviderTimestamp, PayloadHash = statusEvent.PayloadHash, RecordedAt = now
        });
        execution.Status = statusEvent.Status.ToString(); execution.UpdatedAt = now; execution.Version++;
        db.SaveChanges(); transaction?.Commit();
        return true;
    }

    public bool HandleFulfilmentEvent(FulfilmentStatusEvent statusEvent) => AppendStatus(statusEvent);

    private static string HashQuote(FulfilmentQuote quote) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(quote))));
    private static FulfilmentIntent ToIntent(FulfilmentIntentEntity x) => new(x.FulfilmentIntentId, x.PrincipalId, x.AgentId, x.ProviderId, x.MerchantOrderId, Enum.Parse<FulfilmentMode>(x.Mode), x.QuoteId, x.QuoteHash, x.DestinationReference, x.PickupLocationId, x.Currency, x.TotalAmount, x.CreatedAt, x.ExpiresAt);
    private static FulfilmentExecutionResult ToResult(FulfilmentExecutionEntity x) => new(x.FulfilmentId ?? $"unknown_{x.FulfilmentIntentId}", Enum.Parse<FulfilmentStatus>(x.Status), x.ProviderReference ?? "", x.CourierReference, FailureReason: x.FailureReason);
}
