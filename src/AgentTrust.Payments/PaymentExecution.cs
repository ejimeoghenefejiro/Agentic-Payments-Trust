using AgentTrust.Core.Models;

namespace AgentTrust.Payments;

public enum PaymentAttemptStatus { Created, Submitted, Captured, Declined, RequiresAction, Unknown, Refunded, Disputed }
public sealed record PaymentAttempt(string AttemptId, string TransactionId, string IdempotencyKey,
    PaymentAttemptStatus Status, PaymentResult? Result, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public interface IPaymentAttemptStore
{
    PaymentAttempt? FindByIdempotencyKey(string idempotencyKey);
    void Save(PaymentAttempt attempt);
}

public sealed class InMemoryPaymentAttemptStore : IPaymentAttemptStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PaymentAttempt> _attempts = new();
    public PaymentAttempt? FindByIdempotencyKey(string key) { lock (_gate) return _attempts.GetValueOrDefault(key); }
    public void Save(PaymentAttempt attempt) { lock (_gate) _attempts[attempt.IdempotencyKey] = attempt; }
}

/// <summary>Idempotent payment state transition wrapper. A durable store and the same key must be
/// shared across servers in production; retries then return the original terminal result.</summary>
public sealed class PaymentExecutionCoordinator
{
    private readonly object _gate = new();
    private readonly IPaymentAdapter _adapter;
    private readonly IPaymentAttemptStore _store;
    public PaymentExecutionCoordinator(IPaymentAdapter adapter, IPaymentAttemptStore store) { _adapter = adapter; _store = store; }

    public PaymentResult Submit(TransactionIntent intent)
    {
        var key = intent.IdempotencyKey ?? intent.TransactionId;
        lock (_gate)
        {
            var existing = _store.FindByIdempotencyKey(key);
            if (existing?.Result is not null) return existing.Result;
            var now = DateTimeOffset.UtcNow;
            var attempt = existing ?? new PaymentAttempt($"pay_{Guid.NewGuid():N}", intent.TransactionId,
                key, PaymentAttemptStatus.Created, null, now, now);
            _store.Save(attempt with { Status = PaymentAttemptStatus.Submitted, UpdatedAt = now });
            try
            {
                var result = _adapter.Submit(intent);
                var status = result.Status == PaymentStatus.Success ? PaymentAttemptStatus.Captured : PaymentAttemptStatus.Declined;
                _store.Save(attempt with { Status = status, Result = result, UpdatedAt = DateTimeOffset.UtcNow });
                return result;
            }
            catch
            {
                _store.Save(attempt with { Status = PaymentAttemptStatus.Unknown, UpdatedAt = DateTimeOffset.UtcNow });
                throw;
            }
        }
    }
}
