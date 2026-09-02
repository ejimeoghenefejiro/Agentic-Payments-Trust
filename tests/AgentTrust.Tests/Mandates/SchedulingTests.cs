using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Orchestration;
using AgentTrust.Payments;
using AgentTrust.Scheduling;
using AgentTrust.Tasks;
using Xunit;

namespace AgentTrust.Tests.Mandates;

public class SchedulingTests
{
    [Fact]
    public void RecurringScheduleIsDueOnlyWithinToleranceOfTheRightDayAndTime()
    {
        var schedule = new RecurringSchedule(DayOfWeek.Monday, new TimeOnly(7, 30));

        Assert.True(schedule.IsDue(new DateTimeOffset(2027, 6, 7, 7, 32, 0, TimeSpan.Zero))); // Monday, within tolerance
        Assert.False(schedule.IsDue(new DateTimeOffset(2027, 6, 7, 9, 0, 0, TimeSpan.Zero))); // Monday, wrong time
        Assert.False(schedule.IsDue(new DateTimeOffset(2027, 6, 8, 7, 30, 0, TimeSpan.Zero))); // Tuesday
    }

    [Fact]
    public void NextOccurrenceAfterSkipsToTheFollowingWeekOncePassed()
    {
        var schedule = new RecurringSchedule(DayOfWeek.Monday, new TimeOnly(7, 30));
        var afterThisWeeksSlot = new DateTimeOffset(2027, 6, 7, 8, 0, 0, TimeSpan.Zero); // Monday, just after 07:30

        var next = schedule.NextOccurrenceAfter(afterThisWeeksSlot);

        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2027, 6, 14, 7, 30, 0, TimeSpan.Zero), next);
    }

    private sealed class FixedPriceProvider : IPriceQuoteProvider
    {
        public decimal Price { get; set; } = 18.70m;
        public decimal GetQuote(string merchant, IReadOnlyDictionary<string, string> context) => Price;
    }

    [Fact]
    public void ScheduledTaskRunnerExecutesOnlyDueTasksAndRequestsALivePriceQuote()
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
        mandates.Save(new FinancialMandate(
            "mandate_8821", "user_103", "mobility_agent_01", "Uber", "transport", "pm_1",
            25m, 25m, null, "GBP",
            new Dictionary<string, string> { ["pickup"] = "Location A", ["destination"] = "Location B", ["recipient"] = "girlfriend" },
            AboveLimitAction.RequireApproval, MandateStatus.Active,
            DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-12-31T00:00:00Z")));

        var tasks = new InMemoryTaskStore();
        var dueTask = new AgentTask("task_due", "mobility_agent_01", "user_103", "mandate_8821", "recurring_ride",
            new Dictionary<string, string> { ["pickup"] = "Location A", ["destination"] = "Location B", ["recipient"] = "girlfriend" },
            AgentTaskStatus.Active, DateTimeOffset.Parse("2027-01-01T00:00:00Z"));
        var notDueTask = dueTask with { TaskId = "task_not_due" };
        tasks.Save(dueTask);
        tasks.Save(notDueTask);

        var schedules = new InMemoryScheduleStore();
        schedules.Attach(dueTask.TaskId, new RecurringSchedule(DayOfWeek.Monday, new TimeOnly(7, 30)));
        schedules.Attach(notDueTask.TaskId, new RecurringSchedule(DayOfWeek.Wednesday, new TimeOnly(7, 30)));

        var orchestrator = new TaskExecutionOrchestrator(mandates, new InMemoryMandateUsageTracker(), authorities, framework);
        var quoteProvider = new FixedPriceProvider();
        var runner = new ScheduledTaskRunner(tasks, schedules, mandates, orchestrator, quoteProvider);

        var monday730 = new DateTimeOffset(2027, 6, 7, 7, 30, 0, TimeSpan.Zero);
        var results = runner.RunDueTasks("mobility_agent_01", monday730);

        Assert.Single(results);
        Assert.Equal("task_due", results[0].TaskId);
        Assert.Equal(TaskExecutionDecision.Approve, results[0].Decision);
    }
}
