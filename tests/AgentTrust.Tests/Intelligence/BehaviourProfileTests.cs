using AgentTrust.Intelligence.Behaviour;
using Xunit;

namespace AgentTrust.Tests.Intelligence;

public class BehaviourProfileTests
{
    private static TransactionEvent Event(string customerId, decimal amount, DateTimeOffset timestamp, string device, string location, string? beneficiary = null) =>
        new("tx_" + Guid.NewGuid().ToString("N")[..8], customerId, "M14", amount, "GBP", timestamp, device, "1.2.3.4", location, beneficiary, null, false, 0);

    [Fact]
    public void BuildsCustomerProfileMatchingDocExample()
    {
        // "Customer C10391: normal £30-£400, Manchester/Salford, devices D44/D71, typical
        // merchants M14/M18/M33, regular beneficiaries B101/B201, typical time 07:00-23:00."
        var baseTime = new DateTimeOffset(2027, 5, 1, 10, 0, 0, TimeSpan.Zero);
        var history = new List<TransactionEvent>();
        var rnd = new Random(1);
        for (var i = 0; i < 40; i++)
        {
            var amount = 30m + (decimal)rnd.NextDouble() * 370m;
            var hour = 7 + rnd.Next(0, 16);
            var device = i % 2 == 0 ? "D44" : "D71";
            var location = i % 2 == 0 ? "Manchester" : "Salford";
            var beneficiary = i % 3 == 0 ? "B101" : "B201";
            history.Add(Event("C10391", amount, baseTime.AddDays(i).AddHours(hour - 10), device, location, beneficiary));
        }

        var profile = BehaviourProfileBuilder.BuildCustomerProfile("C10391", history);

        Assert.True(profile.TypicalMinAmount >= 30m && profile.TypicalMinAmount < 100m);
        Assert.True(profile.TypicalMaxAmount > 300m && profile.TypicalMaxAmount <= 400m);
        Assert.Contains("D44", profile.TypicalDevices);
        Assert.Contains("D71", profile.TypicalDevices);
        Assert.Contains("Manchester", profile.TypicalLocations);
        Assert.Contains("B101", profile.RegularBeneficiaries);
        Assert.True(profile.IsKnownDevice("D44"));
        Assert.False(profile.IsKnownDevice("D99-unknown"));
    }

    [Fact]
    public void MerchantDeviationServiceFlagsDocSurgeExample()
    {
        // Baseline: 150 tx/day, £22 average, 2% refunds. Shift: 4,300 tx/day, £480 average, 18% refunds.
        var baseline = new MerchantBehaviourProfile("M-surge", 150, 22m, 0.02, new List<string> { "UK" }, 4500);
        var current = new MerchantBehaviourProfile("M-surge", 4300, 480m, 0.18, new List<string> { "UK", "??" }, 4300);

        var deviations = BehaviourDeviationService.CompareMerchantProfiles(baseline, current);

        Assert.Contains(deviations, d => d.Aspect == "TRANSACTION_VOLUME");
        Assert.Contains(deviations, d => d.Aspect == "AVERAGE_AMOUNT");
        Assert.Contains(deviations, d => d.Aspect == "REFUND_RATE");
    }

    [Fact]
    public void MerchantDeviationServiceFindsNothingForStableMerchant()
    {
        var baseline = new MerchantBehaviourProfile("M-stable", 150, 22m, 0.02, new List<string> { "UK" }, 4500);
        var current = new MerchantBehaviourProfile("M-stable", 160, 23m, 0.025, new List<string> { "UK" }, 160);

        var deviations = BehaviourDeviationService.CompareMerchantProfiles(baseline, current);

        Assert.Empty(deviations);
    }
}
