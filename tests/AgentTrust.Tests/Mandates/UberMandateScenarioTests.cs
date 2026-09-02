using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Orchestration;
using AgentTrust.Payments;
using AgentTrust.Tasks;
using Xunit;

namespace AgentTrust.Tests.Mandates;

/// <summary>
/// Reproduces the vision doc's three recurring-Uber-booking scenarios exactly (sections 13-15):
/// a legitimate £18.70 Monday ride, a £31.40 surge-priced ride requiring human approval, and a
/// £22 ride (within limit) to a changed destination/recipient that must still escalate because
/// context — not amount — is what's wrong. All three run through the real, unmodified
/// TrustFramework via TaskExecutionOrchestrator; nothing here bypasses or mocks the trust layer.
/// </summary>
public class UberMandateScenarioTests
{
    private static (TaskExecutionOrchestrator Orchestrator, IMandateStore Mandates, ITaskStore Tasks, IMandateUsageTracker Usage) BuildHarness()
    {
        var agents = new InMemoryAgentRegistry();
        var bindings = new InMemoryPrincipalBindingStore();
        var authorities = new InMemoryDelegatedAuthorityStore();
        var ledger = new InMemoryTransactionLedger();
        var framework = new TrustFramework(agents, bindings, authorities, ledger, new MockPaymentAdapter());

        agents.Register(new AgentIdentity("mobility_agent_01", "user_103", "consumer", "production",
            CredentialStatus.Active, DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-12-31T00:00:00Z"), "ca"));
        bindings.Bind(new PrincipalBinding("mobility_agent_01", "user_103", DateTimeOffset.UtcNow, true, "kyc"));

        var mandates = new InMemoryMandateStore();
        var tasks = new InMemoryTaskStore();
        var usage = new InMemoryMandateUsageTracker();
        var orchestrator = new TaskExecutionOrchestrator(mandates, usage, authorities, framework);
        return (orchestrator, mandates, tasks, usage);
    }

    private static FinancialMandate UberMandate() => new(
        "mandate_8821", "user_103", "mobility_agent_01", "Uber", "transport", "pm_92xxxx",
        PerTransactionLimit: 25m, WeeklyLimit: 25m, MonthlyLimit: null, Currency: "GBP",
        TaskParameters: new Dictionary<string, string> { ["pickup"] = "Location A", ["destination"] = "Location B", ["recipient"] = "girlfriend" },
        AboveLimit: AboveLimitAction.RequireApproval, Status: MandateStatus.Active,
        CreatedAt: DateTimeOffset.Parse("2027-01-01T00:00:00Z"), ExpiresAt: DateTimeOffset.Parse("2027-12-31T00:00:00Z"));

    private static AgentTask RideTask(IReadOnlyDictionary<string, string>? parametersOverride = null) => new(
        "task_uber_monday", "mobility_agent_01", "user_103", "mandate_8821", "recurring_ride",
        parametersOverride ?? new Dictionary<string, string> { ["pickup"] = "Location A", ["destination"] = "Location B", ["recipient"] = "girlfriend" },
        AgentTaskStatus.Active, DateTimeOffset.Parse("2027-01-01T00:00:00Z"));

    [Fact]
    public void LegitimateMondayRideIsApprovedAndPaid()
    {
        var (orchestrator, mandates, _, _) = BuildHarness();
        mandates.Save(UberMandate());
        var task = RideTask();

        var result = orchestrator.Execute(task, 18.70m, task.Parameters, DateTimeOffset.Parse("2027-06-07T07:30:00Z"));

        Assert.Equal(TaskExecutionDecision.Approve, result.Decision);
        Assert.Equal(Decision.Approve, result.TrustLayerDecision);
        Assert.Equal(PaymentStatus.Success, result.PaymentStatus);
        Assert.False(result.AwaitingHumanApproval);
    }

    [Fact]
    public void SurgePricingEscalatesThenHumanApprovalLetsItThroughWithoutRaisingTheStandingLimit()
    {
        var (orchestrator, mandates, _, _) = BuildHarness();
        mandates.Save(UberMandate());
        var task = RideTask();
        var now = DateTimeOffset.Parse("2027-06-07T07:30:00Z");

        var escalated = orchestrator.Execute(task, 31.40m, task.Parameters, now);
        Assert.Equal(TaskExecutionDecision.Escalate, escalated.Decision);
        Assert.True(escalated.AwaitingHumanApproval);
        Assert.Contains("ABOVE_PER_TRANSACTION_LIMIT", escalated.Reasons);
        Assert.Null(escalated.TrustLayerDecision); // must not have touched the trust layer / executed payment yet

        var approved = orchestrator.ResolveEscalation(escalated.TaskExecutionId, approve: true);
        Assert.Equal(TaskExecutionDecision.Approve, approved.Decision);
        Assert.Equal(PaymentStatus.Success, approved.PaymentStatus);

        // The mandate's own standing limit must be untouched by the one-off approval.
        var secondRideNextWeek = orchestrator.Execute(
            task with { TaskId = "task_uber_next" }, 26m, task.Parameters, now.AddDays(8));
        Assert.Equal(TaskExecutionDecision.Escalate, secondRideNextWeek.Decision);
    }

    [Fact]
    public void SurgePricingCanBeRejectedByTheHuman()
    {
        var (orchestrator, mandates, _, _) = BuildHarness();
        mandates.Save(UberMandate());
        var task = RideTask();

        var escalated = orchestrator.Execute(task, 31.40m, task.Parameters, DateTimeOffset.Parse("2027-06-07T07:30:00Z"));
        var rejected = orchestrator.ResolveEscalation(escalated.TaskExecutionId, approve: false);

        Assert.Equal(TaskExecutionDecision.Deny, rejected.Decision);
        Assert.Equal(PaymentStatus.NotAttempted, rejected.PaymentStatus);
    }

    [Fact]
    public void InLimitPriceWithChangedContextStillEscalatesInsteadOfApproving()
    {
        // The doc's key point: £22 is within the £25 limit, but pickup/destination/recipient
        // changed — context makes it wrong even though the amount alone would pass.
        var (orchestrator, mandates, _, _) = BuildHarness();
        mandates.Save(UberMandate());
        var changedContext = new Dictionary<string, string> { ["pickup"] = "Location C", ["destination"] = "Location D", ["recipient"] = "unknown-contact" };
        var task = RideTask(changedContext);

        var result = orchestrator.Execute(task, 22.00m, changedContext, DateTimeOffset.Parse("2027-06-07T07:30:00Z"));

        Assert.Equal(TaskExecutionDecision.Escalate, result.Decision);
        Assert.True(result.AwaitingHumanApproval);
        Assert.Contains(result.Reasons, r => r.StartsWith("CONTEXT_MISMATCH"));
        Assert.False(result.MandateCheck.ContextMatches);
        Assert.True(result.MandateCheck.WithinPerTransactionLimit); // amount alone would have passed
    }

    [Fact]
    public void ExpiredMandateIsBlockedOutright()
    {
        var (orchestrator, mandates, _, _) = BuildHarness();
        mandates.Save(UberMandate() with { Status = MandateStatus.Expired });
        var task = RideTask();

        var result = orchestrator.Execute(task, 18.70m, task.Parameters, DateTimeOffset.Parse("2027-06-07T07:30:00Z"));

        Assert.Equal(TaskExecutionDecision.Deny, result.Decision);
        Assert.Contains("MANDATE_INACTIVE", result.Reasons);
        Assert.Null(result.TrustLayerDecision);
    }
}
