using AgentTrust.Agents;
using AgentTrust.Core;
using AgentTrust.Core.Models;
using AgentTrust.Evidence;
using AgentTrust.Orchestration;
using AgentTrust.Payments;

namespace AgentTrust.Runner;

/// <summary>
/// Single-command demonstration of the full lifecycle described in the concept document:
/// business registers -> agent registered -> authority delegated -> natural-language
/// instruction -> agent observes evidence -> agent proposes purchase -> identity verified ->
/// authority verified -> policy checked -> evidence validated -> transaction authorised ->
/// mock payment succeeds -> audit package generated -> audit chain verified.
/// Uses the diesel example from the concept document: fuel level 22%, ABC Energy, NGN 39,500
/// quote, NGN 50,000 transaction limit, NGN 40,000 human-approval threshold.
/// </summary>
public static class EndToEndDemo
{
    public static async Task RunAsync()
    {
        Step("1. Business registers", "org_abc_logistics (ABC Logistics Ltd) is onboarded as a principal.");
        var principal = new Principal("org_abc_logistics", "ABC Logistics Ltd", DateTimeOffset.UtcNow);
        var principals = new InMemoryPrincipalStore();
        principals.Register(principal);
        Print($"Principal registered: {principal.PrincipalId} ({principal.Name})");

        Step("2. Agent registered", "A procurement agent is issued an identity credential.");
        var agents = new InMemoryAgentRegistry();
        var identity = new AgentIdentity(
            "agt_diesel_01", principal.PrincipalId, "procurement", "production",
            CredentialStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1), "agent-trust-ca");
        agents.Register(identity);
        Print($"Agent registered: {identity.AgentId}, credential status = {identity.CredentialStatus}");

        var bindings = new InMemoryPrincipalBindingStore();
        bindings.Bind(new PrincipalBinding(identity.AgentId, principal.PrincipalId, DateTimeOffset.UtcNow, true, "binding_doc_demo"));
        Print($"Principal-agent binding recorded: {identity.AgentId} acts for {principal.PrincipalId}");

        Step("3. Authority delegated", "NGN 50,000 per-transaction limit, NGN 40,000 human-approval threshold, ABC Energy approved.");
        var authorities = new InMemoryDelegatedAuthorityStore();
        var authority = new DelegatedAuthority(
            "auth_diesel_01", identity.AgentId, new[] { "purchase:fuel" }, 50000, 200000,
            new[] { "ABC Energy" }, new[] { "fuel" }, "NG", null, null, 40000,
            DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), false);
        authorities.Grant(authority);
        Print($"Authority granted: {authority.AuthorityId} — limit NGN {authority.PerTransactionLimit:N0}, human approval above NGN {authority.HumanApprovalAbove:N0}");

        Step("4. Natural-language instruction provided", "\"Whenever the fuel level falls below 25%, buy up to NGN 50,000 of diesel from an approved supplier.\"");
        const string instruction = "Whenever the fuel level falls below 25%, buy up to NGN 50,000 of diesel from an approved supplier.";
        Print(instruction);

        Step("5. Agent observes evidence", "Fuel sensor = 22%, supplier quote = NGN 39,500 from ABC Energy.");
        var evidence = new List<EvidenceItem>
        {
            new("sensor_883", "sensor_reading", "Fuel sensor 22%", true),
            new("quote_923", "supplier_quote", "ABC Energy quote NGN 39,500", true)
        };
        foreach (var e in evidence) Print($"Evidence: {e.EvidenceId} ({e.Type}) — {e.Description}");

        Step("6. Agent proposes purchase", "Semantic Kernel agent (scripted connector — no API key required for this demo) converts instruction + evidence into a structured proposal.");
        var scriptedResponse = "{\"action\":\"purchase\",\"category\":\"fuel\",\"merchant\":\"ABC Energy\",\"amount\":39500,\"currency\":\"NGN\",\"reason\":\"Fuel level is below the authorised threshold\",\"evidenceIds\":[\"sensor_883\",\"quote_923\"]}";
        IPaymentAgent agent = AgentFactory.IsLiveModeConfigured
            ? AgentFactory.CreateLive(identity.AgentId)
            : AgentFactory.CreateScripted(identity.AgentId, scriptedResponse);
        Print(AgentFactory.IsLiveModeConfigured
            ? $"Using live agent against OPENAI_MODEL={Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini"}"
            : "Using deterministic scripted connector (set OPENAI_API_KEY to run this demo against a real model)");

        var context = new AgentProposalContext(
            "TX-DEMO-DIESEL", identity.AgentId, principal.PrincipalId, instruction,
            evidence, new Dictionary<string, string> { ["vehicle"] = "truck-14" }, "NGN", DateTimeOffset.UtcNow);
        var proposal = await agent.ProposeTransactionAsync(context);

        if (proposal.Status == AgentOutputStatus.Invalid)
        {
            Print($"Agent proposal REJECTED before reaching the policy engine. Reason codes: {string.Join(", ", proposal.ValidationReasonCodes)}");
            Console.WriteLine();
            Console.WriteLine("DEMO RESULT: REJECTED (invalid agent output) — payment never attempted.");
            return;
        }
        Print($"Agent proposed: {proposal.Intent!.Action} at {proposal.Intent.Merchant} for NGN {proposal.Intent.Amount:N0} (agent latency {proposal.AgentLatencyMs} ms)");

        Step("7-10. Identity verified -> Authority verified -> Policy checked -> Evidence validated", "Deterministic policy engine evaluates the proposal.");
        var ledger = new InMemoryTransactionLedger();
        var paymentAdapter = new MockPaymentAdapter();
        var auditStore = new InMemoryAuditRecordStore();
        var framework = new TrustFramework(agents, bindings, authorities, ledger, paymentAdapter, persistentAuditStore: auditStore);

        var manifest = new EvidenceManifest(proposal.Intent.TransactionId, proposal.Intent.Evidence.ToList(), new[] { "sensor_reading", "supplier_quote" });
        var outcome = framework.ProcessTransaction(proposal.Intent, manifest);

        foreach (var check in outcome.PolicyDecision.Checks)
        {
            Print($"  [{(check.Passed ? "PASS" : "FAIL")}] {check.Name}: {check.Detail}");
        }

        Step("11. Transaction authorised?", $"Decision = {outcome.PolicyDecision.Decision}");
        if (outcome.PolicyDecision.Decision == Decision.Escalate)
        {
            Print("Transaction escalated for human approval — resolving as APPROVED for this demo.");
            outcome = framework.ResolveApproval(proposal.Intent.TransactionId, approve: true, approver: "demo-supervisor@abclogistics.example", reason: "Demo auto-approval");
        }
        else if (outcome.PolicyDecision.Decision == Decision.Deny)
        {
            Print($"Transaction DENIED. Reason codes: {string.Join(", ", outcome.PolicyDecision.ReasonCodes)}");
            Console.WriteLine();
            Console.WriteLine("DEMO RESULT: DENIED — payment never executed.");
            return;
        }

        Step("12. Mock payment", $"Status = {outcome.PaymentResult.Status}, provider reference = {outcome.PaymentResult.ProviderReference}");

        Step("13. Audit package generated", $"Evidence hash = {outcome.Audit.EvidenceHash}");
        Print($"Audit record: agent={outcome.Audit.AgentId}, principal={outcome.Audit.PrincipalId}, authority={outcome.Audit.AuthorityId}, decision={outcome.Audit.PolicyDecision.Decision}");

        Step("14. Audit chain verified", "Rehydrating the full chain from the persistent store and verifying hash linkage.");
        var reloadedLedger = AuditLedger.LoadExisting(auditStore.LoadAll());
        var verification = reloadedLedger.Verify();
        Print($"Chain entries: {auditStore.LoadAll().Count}, valid = {verification.IsValid}");
        if (!verification.IsValid)
        {
            foreach (var b in verification.Breaks) Print($"  BREAK: {b}");
        }

        Console.WriteLine();
        Console.WriteLine("=== DEMO RESULT ===");
        Console.WriteLine($"APPROVED");
        Console.WriteLine($"Payment executed: {outcome.PaymentResult.Status == PaymentStatus.Success}");
        Console.WriteLine($"Evidence traceable: {manifest.F1:P0} (precision {manifest.Precision:P0}, recall {manifest.Recall:P0})");
        Console.WriteLine($"Audit chain valid: {verification.IsValid}");
    }

    private static void Step(string title, string detail)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
        Console.WriteLine(detail);
    }

    private static void Print(string line) => Console.WriteLine($"  {line}");
}
