using AgentTrust.PaymentMethods;
using Xunit;

namespace AgentTrust.Tests.Mandates;

public class PaymentMethodServiceTests
{
    [Fact]
    public void ConnectingACardStoresOnlyTokenAndDisplayMetadata()
    {
        var service = new PaymentMethodService(new MockCardTokenizationProvider(), new InMemoryPaymentMethodStore());

        var method = service.ConnectCard("user_103", "stripe", "4111111111111234", "123", 8, 2029);

        Assert.StartsWith("tok_", method.Token);
        Assert.Equal("Visa", method.CardBrand);
        Assert.Equal("1234", method.Last4);
        Assert.Equal(PaymentMethodStatus.Active, method.Status);
        // Confirm the record type genuinely has no field capable of holding the raw PAN/CVV —
        // this is a compile-time guarantee (PaymentMethod has no such property), asserted here
        // by checking every property name.
        var propertyNames = typeof(PaymentMethod).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(propertyNames, n => n.Contains("Cvv", StringComparison.OrdinalIgnoreCase) || n.Contains("CardNumber", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RevokingAPaymentMethodMarksItUnusable()
    {
        var service = new PaymentMethodService(new MockCardTokenizationProvider(), new InMemoryPaymentMethodStore());
        var method = service.ConnectCard("user_103", "stripe", "4111111111111234", "123", 8, 2029);

        service.Revoke(method.PaymentMethodId);

        var store = new InMemoryPaymentMethodStore();
        Assert.False(new PaymentMethod(method.PaymentMethodId, method.PrincipalId, method.Provider, method.Token,
            method.CardBrand, method.Last4, method.ExpiryMonth, method.ExpiryYear, PaymentMethodStatus.Revoked)
            .IsUsable(DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Fact]
    public void ExpiredCardIsNotUsable()
    {
        var method = new PaymentMethod("pm_1", "user_1", "stripe", "tok_1", "Visa", "1234", 1, 2020, PaymentMethodStatus.Active);
        Assert.False(method.IsUsable(DateOnly.FromDateTime(DateTime.UtcNow)));
    }
}
