using System.Security.Cryptography;
using System.Text.Json;
using AgentTrust.Commerce;
using AgentTrust.Connectors;
using AgentTrust.Consumer;
using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Mandates;
using AgentTrust.Orchestration;
using AgentTrust.PaymentMethods;
using AgentTrust.Payments;

namespace AgentTrust.Runner;

/// <summary>A deliberately narrow, fail-closed Stripe test-mode product demonstration.</summary>
public static class ConsumerPurchaseDemo
{
    private const string PrincipalId = "consumer-demo";
    private const string AgentId = "weekly-purchase-agent";
    private const string MerchantId = "GroceryDemo";
    private static readonly string[] RequiredAuditEvents =
    [
        "TaskTriggered", "BasketBuilt", "QuoteReceived", "PurchaseIntentCreated",
        "TrustEvaluationStarted", "TrustApproved", "PurchaseAuthorisationIssued",
        "PaymentSubmitted", "PurchaseCompleted"
    ];

    public static async Task RunAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        var settingsPath = Path.Combine(repoRoot, "src", "AgentTrust.Api", "appsettings.Development.json");
        var settings = await LoadSettings(settingsPath, cancellationToken);
        Require(settings.Provider.Equals("Stripe", StringComparison.OrdinalIgnoreCase),
            "Payments:Provider must be Stripe for this demo.");
        Require(settings.Mode == StripePaymentMode.Test, "This demo is restricted to Stripe test mode.");
        Require(settings.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal),
            "Stripe:SecretKey must contain a rotated Stripe test secret key.");
        Require(settings.PaymentMethodToken.StartsWith("pm_", StringComparison.Ordinal),
            "Stripe:TestPaymentMethodId must be a Stripe test PaymentMethod token (for example pm_card_visa).");

        var now = DateTimeOffset.UtcNow;
        var tasks = new InMemoryConsumerTaskStore();
        var executions = new InMemoryPurchaseExecutionStore();
        var mandates = new InMemoryMandateStore();
        var usage = new InMemoryMandateUsageTracker();
        var paymentMethods = new InMemoryPaymentMethodStore();
        var authorities = new InMemoryDelegatedAuthorityStore();
        var agents = new InMemoryAgentRegistry();
        var bindings = new InMemoryPrincipalBindingStore();
        var audit = new InMemoryPurchaseAuditSink();

        agents.Register(new AgentIdentity(AgentId, PrincipalId, "consumer-purchase", "stripe-test",
            CredentialStatus.Active, now.AddMinutes(-1), now.AddHours(1), "local-demo"));
        bindings.Bind(new PrincipalBinding(AgentId, PrincipalId, now, true, "demo-consent"));

        const string mandateId = "mandate-weekly-grocery-demo";
        const string methodId = "payment-method-stripe-test";
        const string addressReference = "demo-address-reference";
        paymentMethods.Save(new PaymentMethod(methodId, PrincipalId, "Stripe", settings.PaymentMethodToken,
            "Visa", "4242", 12, now.Year + 2, PaymentMethodStatus.Active));
        mandates.Save(new FinancialMandate(mandateId, PrincipalId, AgentId, MerchantId, "groceries", methodId,
            5m, 25m, 100m, "GBP",
            new Dictionary<string, string> { ["deliveryAddressReference"] = addressReference },
            AboveLimitAction.Block, MandateStatus.Active, now, now.AddMonths(1)));

        var task = new ConsumerPurchaseTask("weekly-grocery-demo", PrincipalId, AgentId,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MerchantId }, "weekly", "Europe/London",
            5m, "GBP", [new ShoppingListItem("milk", 1)],
            new PurchasePreference(addressReference, null, SubstitutionPolicy.SameOrLowerPrice,
                new Dictionary<string, string>()), mandateId, methodId, ConsumerTaskStatus.Active,
            now, now);
        tasks.Save(task);
        Console.WriteLine("1/6 Weekly purchase task created.");

        var authorisationKey = settings.AuthorisationKey ?? RandomNumberGenerator.GetBytes(32);
        var authorisations = new HmacPurchaseAuthorisationService(authorisationKey);
        var trust = new TrustFramework(agents, bindings, authorities, new InMemoryTransactionLedger(),
            new MockPaymentAdapter());
        var stripe = new StripePaymentAdapter(settings.SecretKey, new StripePaymentOptions(settings.Mode), paymentMethods);
        var connector = new DemoGroceryConnector(authorisations, stripe);
        var orchestrator = new AgentPurchaseOrchestrator(tasks, executions, mandates, usage, paymentMethods,
            authorities, trust, authorisations, audit,
            new LivePurchaseGate(new LivePurchaseOptions(true, 5m,
                new HashSet<string> { PrincipalId }, new HashSet<string> { MerchantId }, true)));

        var scheduledFor = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        var result = await orchestrator.RunAsync(task.TaskId, PrincipalId, scheduledFor, connector,
            new LiveExecutionContext(true, true), cancellationToken);
        var intent = result.Intent ?? throw new InvalidOperationException("The agent did not build a basket.");
        Require(intent.BasketItems.Count > 0, "The agent built an empty basket.");
        Console.WriteLine($"2/6 Basket built and quoted at {intent.TotalAmount:0.00} {intent.Currency}.");
        Require(result.Authorisation is not null, "The deterministic trust boundary did not approve the purchase.");
        Console.WriteLine("3/6 Deterministic trust approved an intent-bound purchase authorisation.");
        Require(result.Execution.State == PurchaseExecutionState.Purchased,
            $"Stripe test payment did not succeed (state={result.Execution.State}, reasons={string.Join(',', result.Execution.Reasons)}). ");
        Require(result.Execution.ProviderReference?.StartsWith("pi_", StringComparison.Ordinal) == true,
            "Stripe did not return a PaymentIntent reference.");
        Console.WriteLine($"4/6 Stripe test payment succeeded ({result.Execution.ProviderReference}).");
        var receipt = result.Receipt ?? throw new InvalidOperationException("A receipt was not created.");
        Require(receipt.ProviderReference == result.Execution.ProviderReference,
            "The receipt does not match the Stripe payment.");
        Console.WriteLine($"5/6 Receipt created ({receipt.ReceiptId}).");

        var events = audit.Find(result.Execution.PurchaseIntentId);
        var missing = RequiredAuditEvents.Except(events.Select(x => x.EventType), StringComparer.Ordinal).ToArray();
        Require(missing.Length == 0, $"Audit trail is incomplete: {string.Join(", ", missing)}.");
        Require(events.All(x => !string.IsNullOrWhiteSpace(x.IntentHash)), "An audit event has no intent hash.");
        Console.WriteLine($"6/6 Audit verified ({events.Count} events). Vertical slice completed.");
    }

    private static async Task<DemoSettings> LoadSettings(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"Ignored development settings not found: {path}");
        await using var stream = File.OpenRead(path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;
        var provider = Read(root, "Payments", "Provider") ?? "";
        var modeText = Read(root, "Payments", "Mode") ?? "";
        var secret = Read(root, "Stripe", "SecretKey") ?? "";
        var token = Read(root, "Stripe", "TestPaymentMethodId") ?? "pm_card_visa";
        byte[]? authorisationKey = null;
        var encodedKey = Read(root, "PurchaseAuthorisation", "Key");
        if (!string.IsNullOrWhiteSpace(encodedKey)) authorisationKey = Convert.FromBase64String(encodedKey);
        if (!Enum.TryParse<StripePaymentMode>(modeText, true, out var mode))
            throw new InvalidOperationException("Payments:Mode must be Test for this demo.");
        return new DemoSettings(provider, mode, secret, token, authorisationKey);
    }

    private static string? Read(JsonElement root, string section, string name) =>
        root.TryGetProperty(section, out var value) && value.TryGetProperty(name, out var property)
            ? property.GetString() : null;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record DemoSettings(string Provider, StripePaymentMode Mode, string SecretKey,
        string PaymentMethodToken, byte[]? AuthorisationKey);
}
