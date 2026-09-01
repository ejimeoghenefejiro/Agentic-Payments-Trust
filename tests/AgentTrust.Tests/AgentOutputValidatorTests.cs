using AgentTrust.Agents;
using AgentTrust.Core.Models;
using Xunit;

namespace AgentTrust.Tests;

public class AgentOutputValidatorTests
{
    private static AgentProposalContext DefaultContext() => new(
        "tx_1", "agt_1", "org_1", "instruction",
        new[] { new EvidenceItem("ev_1", "sensor_reading", "reading", true) },
        new Dictionary<string, string>(), "NGN", DateTimeOffset.Parse("2027-06-01T10:00:00Z"));

    [Fact]
    public void ValidOutputProducesIntent()
    {
        var output = new RawAgentOutput("purchase", "fuel", "ABC Energy", 39500, "NGN", "reason", new[] { "ev_1" });

        var (isValid, intent, reasons) = AgentOutputValidator.Validate(output, DefaultContext());

        Assert.True(isValid);
        Assert.NotNull(intent);
        Assert.Equal("purchase:fuel", intent!.Action);
        Assert.Empty(reasons);
    }

    [Fact]
    public void NullOutputIsInvalid()
    {
        var (isValid, intent, reasons) = AgentOutputValidator.Validate(null, DefaultContext());

        Assert.False(isValid);
        Assert.Null(intent);
        Assert.Contains("INVALID_AGENT_OUTPUT", reasons);
    }

    [Fact]
    public void MissingAmountIsFlagged()
    {
        var output = new RawAgentOutput("purchase", "fuel", "ABC Energy", null, "NGN", "reason", new[] { "ev_1" });

        var (isValid, _, reasons) = AgentOutputValidator.Validate(output, DefaultContext());

        Assert.False(isValid);
        Assert.Contains("MISSING_TRANSACTION_AMOUNT", reasons);
    }

    [Fact]
    public void MissingMerchantIsFlagged()
    {
        var output = new RawAgentOutput("purchase", "fuel", "", 1000, "NGN", "reason", new[] { "ev_1" });

        var (isValid, _, reasons) = AgentOutputValidator.Validate(output, DefaultContext());

        Assert.False(isValid);
        Assert.Contains("UNKNOWN_MERCHANT", reasons);
    }

    [Fact]
    public void FabricatedEvidenceReferenceIsFlagged()
    {
        var output = new RawAgentOutput("purchase", "fuel", "ABC Energy", 1000, "NGN", "reason", new[] { "does_not_exist" });

        var (isValid, _, reasons) = AgentOutputValidator.Validate(output, DefaultContext());

        Assert.False(isValid);
        Assert.Contains("INVALID_EVIDENCE_REFERENCE", reasons);
    }

    [Fact]
    public void MissingEvidenceIsFlagged()
    {
        var output = new RawAgentOutput("purchase", "fuel", "ABC Energy", 1000, "NGN", "reason", Array.Empty<string>());

        var (isValid, _, reasons) = AgentOutputValidator.Validate(output, DefaultContext());

        Assert.False(isValid);
        Assert.Contains("MISSING_EVIDENCE", reasons);
    }

    [Fact]
    public void CurrencyMismatchIsFlagged()
    {
        var output = new RawAgentOutput("purchase", "fuel", "ABC Energy", 1000, "USD", "reason", new[] { "ev_1" });

        var (isValid, _, reasons) = AgentOutputValidator.Validate(output, DefaultContext());

        Assert.False(isValid);
        Assert.Contains("CURRENCY_MISMATCH", reasons);
    }
}
