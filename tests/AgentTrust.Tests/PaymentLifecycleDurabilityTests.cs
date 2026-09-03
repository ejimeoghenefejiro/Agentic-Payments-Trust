using AgentTrust.Commerce;
using AgentTrust.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Tests;

public sealed class PaymentLifecycleDurabilityTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AgentTrustDbContext _db;
    private readonly EfCommerceDurability _durability;

    public PaymentLifecycleDurabilityTests()
    {
        _connection.Open();
        _db = new AgentTrustDbContext(new DbContextOptionsBuilder<AgentTrustDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _durability = new EfCommerceDurability(_db);
    }

    [Fact]
    public void RetryWithSameIdempotencyKey_UsesOneAttemptAndOneCheckout()
    {
        var intent = Intent();

        _durability.BeginPaymentSubmission(intent, "Stripe");
        _durability.RecordPaymentUnknown(intent, "TIMEOUT");
        _durability.BeginPaymentSubmission(intent, "Stripe");

        var attempt = Assert.Single(_db.ConsumerPaymentAttempts);
        var checkout = Assert.Single(_db.CheckoutExecutions);
        Assert.Equal(intent.IdempotencyKey, attempt.PaymentIdempotencyKey);
        Assert.Equal(checkout.CheckoutExecutionId, attempt.CheckoutExecutionId);
        Assert.Equal(2, checkout.SubmissionCount);
    }

    [Fact]
    public void TimeoutThenSuccessfulRetry_ConvergesToSameTerminalLifecycle()
    {
        var intent = Intent();
        _durability.BeginPaymentSubmission(intent, "Stripe");
        _durability.RecordPaymentUnknown(intent, "NETWORK_TIMEOUT");
        _durability.BeginPaymentSubmission(intent, "Stripe");
        _durability.RecordPaymentResult(intent, new(PlatformPaymentStatus.Succeeded, "pi_recovered", null, null));

        var attempt = Assert.Single(_db.ConsumerPaymentAttempts);
        var checkout = Assert.Single(_db.CheckoutExecutions);
        Assert.Equal("Captured", attempt.LatestStatus);
        Assert.Equal("pi_recovered", attempt.ProviderPaymentId);
        Assert.Equal("Succeeded", checkout.Status);
        Assert.Equal(2, checkout.SubmissionCount);
    }

    [Fact]
    public void RepeatedSuccess_CreatesOnlyOneReceiptForPurchaseIntent()
    {
        var intent = Intent();
        _durability.SaveReceipt(new("receipt_1", intent.PurchaseIntentId, intent.MerchantId, intent.TotalAmount, intent.Currency, "pi_1", DateTimeOffset.UtcNow), intent.PrincipalId);
        _durability.SaveReceipt(new("receipt_2", intent.PurchaseIntentId, intent.MerchantId, intent.TotalAmount, intent.Currency, "pi_1", DateTimeOffset.UtcNow), intent.PrincipalId);

        var receipt = Assert.Single(_db.PurchaseReceipts);
        Assert.Equal("receipt_1", receipt.ReceiptId);
    }

    private static PurchaseIntent Intent() => new(
        "intent_1", "principal_1", "agent_1", "mandate_1", "task_1", "merchant_1", "Merchant", "GBP",
        [new BasketItem("product_1", "Milk", 1, 2.50m, 2.50m, false)], 2.50m, 0m, 2.50m,
        "address_1", null, "pm_1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10), "idem_1");

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
