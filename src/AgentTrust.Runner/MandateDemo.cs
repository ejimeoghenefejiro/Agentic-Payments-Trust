using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Orchestration;
using AgentTrust.PaymentMethods;
using AgentTrust.Payments;
using AgentTrust.Scheduling;
using AgentTrust.Tasks;

namespace AgentTrust.Runner;

/// <summary>
/// Single-command demonstration of the vision doc's Phase 2 (sections 13-17): connect a card,
/// create a recurring Financial Mandate, then run all three worked scenarios — a legitimate
/// Monday booking, a surge-priced ride that escalates for human approval, and an in-limit price
/// with changed context that escalates anyway. Every authorisation decision is made by the same,
/// unmodified TrustFramework used everywhere else in this repo.
/// </summary>
public static class MandateDemo
{
    public static void Run()
    {
        Step("1. User connects a card", "Raw card details are tokenised and immediately discarded.");
        var paymentMethods = new PaymentMethodService(new MockCardTokenizationProvider(), new InMemoryPaymentMethodStore());
        var card = paymentMethods.ConnectCard("user_103", "stripe", "4111111111119876", "123", 9, 2029);
        Print($"Stored: {card.CardBrand} ...{card.Last4}, token {card.Token} (no PAN/CVV retained)");

        Step("2. Agent and mandate are created", "\"Book an Uber for my girlfriend every Monday at 07:30. Spend up to £25. If it costs more, ask me first.\"");
        var agents = new InMemoryAgentRegistry();
        var bindings = new InMemoryPrincipalBindingStore();
        var authorities = new InMemoryDelegatedAuthorityStore();
        var ledger = new InMemoryTransactionLedger();
        var framework = new TrustFramework(agents, bindings, authorities, ledger, new MockPaymentAdapter());
        agents.Register(new AgentIdentity("mobility_agent_01", "user_103", "consumer", "production",
            CredentialStatus.Active, DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-12-31T00:00:00Z"), "ca"));
        bindings.Bind(new PrincipalBinding("mobility_agent_01", "user_103", DateTimeOffset.UtcNow, true, "kyc"));

        var routeContext = new Dictionary<string, string> { ["pickup"] = "Location A", ["destination"] = "Location B", ["recipient"] = "girlfriend" };
        var mandate = new FinancialMandate(
            "mandate_8821", "user_103", "mobility_agent_01", "Uber", "transport", card.PaymentMethodId,
            PerTransactionLimit: 25m, WeeklyLimit: 25m, MonthlyLimit: null, Currency: "GBP",
            TaskParameters: routeContext, AboveLimit: AboveLimitAction.RequireApproval, Status: MandateStatus.Active,
            CreatedAt: DateTimeOffset.Parse("2027-01-01T00:00:00Z"), ExpiresAt: DateTimeOffset.Parse("2027-12-31T00:00:00Z"));
        var mandates = new InMemoryMandateStore();
        mandates.Save(mandate);
        var usageTracker = new InMemoryMandateUsageTracker();
        var orchestrator = new TaskExecutionOrchestrator(mandates, usageTracker, authorities, framework);
        Print($"Mandate {mandate.MandateId}: {mandate.Merchant}/{mandate.Purpose}, per-trip £{mandate.PerTransactionLimit}, weekly £{mandate.WeeklyLimit}, above limit -> {mandate.AboveLimit}");

        // Scenario A: legitimate Monday ride.
        Step("3. Scenario A — legitimate Monday ride", "Route, recipient and price all match; price £18.70 is within the £25 limit.");
        var taskA = new AgentTask("task_monday_a", "mobility_agent_01", "user_103", mandate.MandateId, "recurring_ride", routeContext, AgentTaskStatus.Active, DateTimeOffset.UtcNow);
        var resultA = orchestrator.Execute(taskA, 18.70m, routeContext, DateTimeOffset.Parse("2027-06-07T07:30:00Z"));
        Print($"Decision: {resultA.Decision}  Payment: {resultA.PaymentStatus}");

        // Scenario B: surge pricing.
        Step("4. Scenario B — surge pricing", "Same route and recipient, but the live quote is £31.40 (above the £25 limit).");
        var taskB = new AgentTask("task_monday_b", "mobility_agent_01", "user_103", mandate.MandateId, "recurring_ride", routeContext, AgentTaskStatus.Active, DateTimeOffset.UtcNow);
        var resultB = orchestrator.Execute(taskB, 31.40m, routeContext, DateTimeOffset.Parse("2027-06-14T07:30:00Z"));
        Print($"Decision: {resultB.Decision}  Reasons: {string.Join(", ", resultB.Reasons)}");
        Print("\"Uber currently costs £31.40. Your automatic spending limit is £25. Approve £31.40?\" -> user approves.");
        var resultBApproved = orchestrator.ResolveEscalation(resultB.TaskExecutionId, approve: true);
        Print($"After approval — Decision: {resultBApproved.Decision}  Payment: {resultBApproved.PaymentStatus}");
        Print("The mandate's own £25 limit is unchanged for every future ride — this approval covered only this one trip.");

        // Scenario C: context override.
        Step("5. Scenario C — context can override apparent normality", "Price £22 is within the £25 limit, but pickup, destination and recipient have all changed.");
        var changedContext = new Dictionary<string, string> { ["pickup"] = "Location C", ["destination"] = "Location D", ["recipient"] = "unknown-contact" };
        var taskC = new AgentTask("task_monday_c", "mobility_agent_01", "user_103", mandate.MandateId, "recurring_ride", changedContext, AgentTaskStatus.Active, DateTimeOffset.UtcNow);
        var resultC = orchestrator.Execute(taskC, 22.00m, changedContext, DateTimeOffset.Parse("2027-06-21T07:30:00Z"));
        Print($"Decision: {resultC.Decision}  Reasons: {string.Join(", ", resultC.Reasons)}");
        Print("A standing order only understands amount + date. This mandate understands who + why + merchant + task + recipient + route + context.");

        Console.WriteLine();
        Console.WriteLine("=== MANDATE DEMO RESULT ===");
        Console.WriteLine($"Scenario A (legitimate):        {resultA.Decision}");
        Console.WriteLine($"Scenario B (surge, approved):   {resultBApproved.Decision}");
        Console.WriteLine($"Scenario C (context mismatch):  {resultC.Decision}");
    }

    private static void Step(string title, string detail)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
        Console.WriteLine(detail);
    }

    private static void Print(string line) => Console.WriteLine($"  {line}");
}
