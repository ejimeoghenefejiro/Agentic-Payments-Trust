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

    [Fact]
    public void PlanningConversation_TurnsConstraintsAndProductHoldsAreDurable()
    {
        var store=new EfConsumerPlanningStore(_db);var now=DateTimeOffset.UtcNow;var conversation=store.Create("principal_1","Make wraps","{\"inventoryAtHome\":\"sauce\"}",now);
        store.Append(new("turn_1",conversation.ConversationId,1,"user","message","I have sauce",null,null,null,now));
        store.Remember("principal_1","diet","vegetarian",conversation.ConversationId,now);
        store.ReplaceReservations(conversation.ConversationId,[new("hold_1",conversation.ConversationId,"wraps",1,1.80m,"GBP","Reserved",now,now.AddMinutes(5))]);
        store.SavePolicy(new("principal_1","AUTO_WHEN_SAFE",true,true,now));
        _db.ChangeTracker.Clear();var reloaded=store.FindOwned(conversation.ConversationId,"principal_1");
        Assert.NotNull(reloaded);Assert.Contains("sauce",reloaded!.StateJson);Assert.Single(store.Turns(conversation.ConversationId));Assert.Single(store.Reservations(conversation.ConversationId));Assert.Equal("vegetarian",store.Preferences("principal_1")["diet"]);
        Assert.Equal(conversation.ConversationId,store.FindLatestOpen("principal_1",now.AddMinutes(-1))?.ConversationId);
        Assert.True(store.GetPolicy("principal_1").AskBeforeSubstitutions);Assert.True(store.GetPolicy("principal_1").ShowBasketBeforePayment);
        Assert.Null(store.FindOwned(conversation.ConversationId,"other_principal"));
    }

    private static PurchaseIntent Intent() => new(
        "intent_1", "principal_1", "agent_1", "mandate_1", "task_1", "merchant_1", "Merchant", "GBP",
        [new BasketItem("product_1", "Milk", 1, 2.50m, 2.50m, false)], 2.50m, 0m, 2.50m,
        "address_1", null, "pm_1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10), "idem_1");

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}
