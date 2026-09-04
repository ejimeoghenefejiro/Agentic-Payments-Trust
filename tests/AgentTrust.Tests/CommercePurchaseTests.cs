using System.Security.Cryptography;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Consumer;
using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Orchestration;
using AgentTrust.PaymentMethods;
using AgentTrust.Payments;
using Xunit;

namespace AgentTrust.Tests;

public sealed class CommercePurchaseTests
{
    [Fact]
    public async Task UnderLimitPurchaseTraversesTrustAndCompletesOnce()
    {
        var fixture = Build(maximum: 70);
        var scheduled = DateTimeOffset.Parse("2026-09-06T09:00:00Z");

        var first = await fixture.Orchestrator.RunAsync("task-1", "principal-1", scheduled,
            fixture.Connector, new LiveExecutionContext(false, false));
        var duplicate = await fixture.Orchestrator.RunAsync("task-1", "principal-1", scheduled,
            fixture.Connector, new LiveExecutionContext(false, false));

        Assert.Equal(PurchaseExecutionState.Purchased, first.Execution.State);
        Assert.NotNull(first.Authorisation);
        Assert.NotNull(first.Receipt);
        Assert.Equal(1, fixture.Payments.SubmissionCount);
        Assert.Equal(first.Execution.ExecutionId, duplicate.Execution.ExecutionId);
        Assert.Contains(fixture.Audit.Find(first.Execution.PurchaseIntentId), x => x.EventType == "TrustApproved");
        Assert.Contains(fixture.Audit.Find(first.Execution.PurchaseIntentId), x => x.EventType == "PurchaseCompleted");
    }

    [Fact]
    public async Task ConnectorRejectsModifiedBasketAfterAuthorisation()
    {
        var fixture = Build(maximum: 70);
        var result = await fixture.Orchestrator.RunAsync("task-1", "principal-1",
            DateTimeOffset.Parse("2026-09-06T10:00:00Z"), fixture.Connector, new(false, false));
        var modified = result.Intent! with { TotalAmount = result.Intent!.TotalAmount + 1 };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Connector.ExecutePurchaseAsync(modified, result.Authorisation!));
        Assert.Equal(1, fixture.Payments.SubmissionCount);
    }

    [Fact]
    public async Task AboveLimitEscalatesWithoutPaymentAndOneOffApprovalIsSingleUse()
    {
        var fixture = Build(maximum: 3);
        var result = await fixture.Orchestrator.RunAsync("task-1", "principal-1",
            DateTimeOffset.Parse("2026-09-13T09:00:00Z"), fixture.Connector, new(false, false));

        Assert.Equal(PurchaseExecutionState.AwaitingHumanApproval, result.Execution.State);
        Assert.Equal(0, fixture.Payments.SubmissionCount);
        var approved = await fixture.Orchestrator.ResolveAsync(result.Execution.PurchaseIntentId,
            "principal-1", true, "consumer-1");
        Assert.Equal(PurchaseExecutionState.Purchased, approved.Execution.State);
        Assert.Equal(1, fixture.Payments.SubmissionCount);
        Assert.Equal(3, fixture.Mandates.Find("mandate-1")!.PerTransactionLimit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Orchestrator.ResolveAsync(
            result.Execution.PurchaseIntentId, "principal-1", true, "consumer-1"));
    }

    [Fact]
    public async Task FinalQuoteCannotExceedUserBudgetEvenWhenMandateAllowsIt()
    {
        var fixture=Build(70,taskBudget:3);
        var result=await fixture.Orchestrator.RunAsync("task-1","principal-1",DateTimeOffset.UtcNow,fixture.Connector,new(false,false));
        Assert.Equal(PurchaseExecutionState.Denied,result.Execution.State);
        Assert.Contains("USER_BUDGET_EXCEEDED",result.Execution.Reasons);
        Assert.Equal(0,fixture.Payments.SubmissionCount);
    }

    [Theory]
    [InlineData(MandateStatus.Suspended)]
    [InlineData(MandateStatus.Expired)]
    public async Task InactiveMandateDeniesWithoutCheckout(MandateStatus status)
    {
        var fixture = Build(70, status);
        var result = await fixture.Orchestrator.RunAsync("task-1", "principal-1",
            DateTimeOffset.UtcNow, fixture.Connector, new(false, false));
        Assert.Equal(PurchaseExecutionState.Denied, result.Execution.State);
        Assert.Equal(0, fixture.Payments.SubmissionCount);
    }

    [Fact]
    public async Task WrongPrincipalAndWrongPaymentMethodOwnerAreRejected()
    {
        var fixture = Build(70);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Orchestrator.RunAsync(
            "task-1", "principal-other", DateTimeOffset.UtcNow, fixture.Connector, new(false, false)));
        fixture.PaymentMethods.Save(fixture.PaymentMethods.Find("pm-1")! with { PrincipalId = "principal-other" });
        var result = await fixture.Orchestrator.RunAsync("task-1", "principal-1",
            DateTimeOffset.UtcNow.AddMinutes(1), fixture.Connector, new(false, false));
        Assert.Equal(PurchaseExecutionState.Denied, result.Execution.State);
        Assert.Equal(0, fixture.Payments.SubmissionCount);
    }

    [Fact]
    public async Task RequiresActionDoesNotCompleteOrDuplicatePayment()
    {
        var fixture = Build(70); fixture.Payments.NextStatus = PlatformPaymentStatus.RequiresAction;
        var scheduled = DateTimeOffset.UtcNow;
        var result = await fixture.Orchestrator.RunAsync("task-1", "principal-1", scheduled,
            fixture.Connector, new(false, false));
        var retry = await fixture.Orchestrator.RunAsync("task-1", "principal-1", scheduled,
            fixture.Connector, new(false, false));
        Assert.Equal(PurchaseExecutionState.RequiresAction, result.Execution.State);
        Assert.NotNull(result.Execution.RequiredAction);
        Assert.Equal(PurchaseExecutionState.RequiresAction, retry.Execution.State);
        Assert.Equal(1, fixture.Payments.SubmissionCount);
    }

    [Fact]
    public async Task LiveGateRequiresEveryExplicitCondition()
    {
        var fixture = Build(70, live: new LivePurchaseOptions(false, 5,
            new HashSet<string>{"principal-1"}, new HashSet<string>{"GroceryDemo"}, true));
        var result = await fixture.Orchestrator.RunAsync("task-1", "principal-1", DateTimeOffset.UtcNow,
            fixture.Connector, new(true, true));
        Assert.Equal(PurchaseExecutionState.Denied, result.Execution.State);
        Assert.Contains("LIVE_PURCHASE_DISABLED", result.Execution.Reasons);
    }

    [Fact]
    public void IntelligenceProjectCannotReferenceConsumerCommerceOrConnectors()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "AgentTrust.Intelligence", "AgentTrust.Intelligence.csproj"));
        var project = File.ReadAllText(path);
        Assert.DoesNotContain("AgentTrust.Consumer", project);
        Assert.DoesNotContain("AgentTrust.Commerce", project);
        Assert.DoesNotContain("AgentTrust.Connectors", project);
        Assert.DoesNotContain("Stripe", project, StringComparison.OrdinalIgnoreCase);
    }

    private static Fixture Build(decimal maximum, MandateStatus status = MandateStatus.Active,
        LivePurchaseOptions? live = null,decimal taskBudget=70)
    {
        var now = DateTimeOffset.UtcNow; var agents = new InMemoryAgentRegistry(); var bindings = new InMemoryPrincipalBindingStore();
        var authorities = new InMemoryDelegatedAuthorityStore();
        agents.Register(new AgentIdentity("agent-1", "principal-1", "consumer", "pilot", CredentialStatus.Active, now.AddDays(-1), now.AddYears(1), "test"));
        bindings.Bind(new PrincipalBinding("agent-1", "principal-1", now, true, "test"));
        var trust = new TrustFramework(agents, bindings, authorities, new InMemoryTransactionLedger(), new MockPaymentAdapter());
        var mandates = new InMemoryMandateStore();
        mandates.Save(new FinancialMandate("mandate-1", "principal-1", "agent-1", "GroceryDemo", "groceries", "pm-1",
            maximum, 500, 2000, "GBP", new Dictionary<string,string>{{"deliveryAddressReference","address-1"}},
            AboveLimitAction.RequireApproval, status, now, status == MandateStatus.Expired ? now.AddDays(-1) : now.AddYears(1)));
        var methods = new InMemoryPaymentMethodStore(); methods.Save(new PaymentMethod("pm-1", "principal-1", "Stripe", "pm_test_token", "Visa", "4242", 12, now.Year + 2, PaymentMethodStatus.Active));
        var tasks = new InMemoryConsumerTaskStore(); tasks.Save(new ConsumerPurchaseTask("task-1", "principal-1", "agent-1",
            new HashSet<string>{"GroceryDemo"}, "0 10 * * SUN", "Europe/London", taskBudget, "GBP",
            [new ShoppingListItem("milk", 1)], new PurchasePreference("address-1", "Sunday 10:00-12:00", SubstitutionPolicy.SameOrLowerPrice, new Dictionary<string,string>()),
            "mandate-1", "pm-1", ConsumerTaskStatus.Active, now, now));
        var executions = new InMemoryPurchaseExecutionStore(); var usage = new InMemoryMandateUsageTracker();
        var auth = new HmacPurchaseAuthorisationService(RandomNumberGenerator.GetBytes(32)); var payments = new MockPlatformPaymentProcessor();
        var connector = new DemoGroceryConnector(auth, payments); var audit = new InMemoryPurchaseAuditSink();
        var orchestrator = new AgentPurchaseOrchestrator(tasks, executions, mandates, usage, methods, authorities,
            trust, auth, audit, new LivePurchaseGate(live ?? new LivePurchaseOptions()), new InMemoryOneOffAuthorisationStore());
        return new Fixture(orchestrator, connector, payments, audit, mandates, methods);
    }
    private sealed record Fixture(AgentPurchaseOrchestrator Orchestrator, DemoGroceryConnector Connector,
        MockPlatformPaymentProcessor Payments, InMemoryPurchaseAuditSink Audit,
        InMemoryMandateStore Mandates, InMemoryPaymentMethodStore PaymentMethods);
}
