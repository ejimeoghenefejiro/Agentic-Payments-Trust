using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentTrust.Tests;

/// <summary>
/// End-to-end HTTP tests against AgentTrust.Api hosted in-process via WebApplicationFactory.
/// No POSTGRES_CONNECTION is set for this factory, so the API falls back to its in-memory
/// stores — this exercises the full controller/DI/routing stack without requiring a running
/// database, while PersistenceTests separately proves the EF-Core mapping against a real
/// relational engine (SQLite).
/// </summary>
public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task FullLifecycle_RegisterAgent_GrantAuthority_ApproveTransaction_VerifyAudit()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agentId = $"agt_api_{suffix}";
        var principalId = $"org_api_{suffix}";
        var authorityId = $"auth_api_{suffix}";
        var txId = $"tx_api_{suffix}";

        var registerPrincipal = await _client.PostAsJsonAsync("/api/principals", new { principalId, name = "API Test Org" });
        Assert.Equal(HttpStatusCode.Created, registerPrincipal.StatusCode);

        var registerAgent = await _client.PostAsJsonAsync("/api/agents", new { agentId, principalId, agentType = "procurement", environment = "production" });
        Assert.Equal(HttpStatusCode.Created, registerAgent.StatusCode);

        var getAgent = await _client.GetAsync($"/api/agents/{agentId}");
        Assert.Equal(HttpStatusCode.OK, getAgent.StatusCode);

        var bind = await _client.PostAsJsonAsync("/api/bindings", new { agentId, principalId, bindingEvidenceRef = "doc_1" });
        Assert.Equal(HttpStatusCode.Created, bind.StatusCode);

        var grantAuthority = await _client.PostAsJsonAsync("/api/authorities", new
        {
            authorityId,
            agentId,
            permissions = new[] { "purchase:fuel" },
            perTransactionLimit = 50000,
            dailyLimit = 200000,
            approvedMerchants = new[] { "ABC Energy" },
            categoryScope = new[] { "fuel" },
            geographicScope = "NG",
            humanApprovalAbove = 40000,
            expiry = "2027-12-31"
        });
        Assert.Equal(HttpStatusCode.Created, grantAuthority.StatusCode);

        var getAuthority = await _client.GetAsync($"/api/authorities/{authorityId}");
        Assert.Equal(HttpStatusCode.OK, getAuthority.StatusCode);

        var submitTransaction = await _client.PostAsJsonAsync("/api/transactions/request", new
        {
            transactionId = txId,
            agentId,
            principalId,
            userInstruction = (string?)null,
            expectedCurrency = "NGN",
            action = "purchase:fuel",
            merchant = "ABC Energy",
            category = "fuel",
            amount = 20000,
            reason = "fuel_sensor_below_threshold",
            idempotencyKey = txId,
            evidence = new[] { new { evidenceId = "ev_1", type = "sensor_reading", description = "reading", exists = true } },
            context = (Dictionary<string, string>?)null,
            scriptedAgentResponse = (string?)null
        });
        Assert.Equal(HttpStatusCode.OK, submitTransaction.StatusCode);
        var body = await submitTransaction.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Approve", body.GetProperty("decision").GetString());

        var getTransaction = await _client.GetAsync($"/api/transactions/{txId}");
        Assert.Equal(HttpStatusCode.OK, getTransaction.StatusCode);

        var auditGet = await _client.GetAsync($"/api/audit/{txId}");
        Assert.Equal(HttpStatusCode.OK, auditGet.StatusCode);

        var verify = await _client.GetAsync("/api/audit/verify");
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var verifyBody = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(verifyBody.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task EscalatedTransaction_RequiresApprovalBeforePaymentExecutes()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agentId = $"agt_esc_{suffix}";
        var principalId = $"org_esc_{suffix}";
        var authorityId = $"auth_esc_{suffix}";
        var txId = $"tx_esc_{suffix}";

        await _client.PostAsJsonAsync("/api/principals", new { principalId, name = "Escalation Org" });
        await _client.PostAsJsonAsync("/api/agents", new { agentId, principalId, agentType = "procurement", environment = "production" });
        await _client.PostAsJsonAsync("/api/bindings", new { agentId, principalId, bindingEvidenceRef = "doc_1" });
        await _client.PostAsJsonAsync("/api/authorities", new
        {
            authorityId,
            agentId,
            permissions = new[] { "purchase:fuel" },
            perTransactionLimit = 50000,
            dailyLimit = 200000,
            approvedMerchants = new[] { "ABC Energy" },
            categoryScope = new[] { "fuel" },
            geographicScope = "NG",
            humanApprovalAbove = 40000,
            expiry = "2027-12-31"
        });

        var submit = await _client.PostAsJsonAsync("/api/transactions/request", new
        {
            transactionId = txId,
            agentId,
            principalId,
            userInstruction = (string?)null,
            expectedCurrency = "NGN",
            action = "purchase:fuel",
            merchant = "ABC Energy",
            category = "fuel",
            amount = 45000,
            reason = "large purchase",
            idempotencyKey = txId,
            evidence = new[] { new { evidenceId = "ev_1", type = "sensor_reading", description = "reading", exists = true } },
            context = (Dictionary<string, string>?)null,
            scriptedAgentResponse = (string?)null
        });
        var submitBody = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Escalate", submitBody.GetProperty("decision").GetString());
        Assert.Equal("NotAttempted", submitBody.GetProperty("paymentStatus").GetString());

        var approve = await _client.PostAsJsonAsync($"/api/approvals/{txId}", new { approve = true, approver = "supervisor@example.com", reason = "confirmed" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approveBody = await approve.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Approve", approveBody.GetProperty("finalDecision").GetString());
        Assert.Equal("Success", approveBody.GetProperty("paymentStatus").GetString());

        var secondApprove = await _client.PostAsJsonAsync($"/api/approvals/{txId}", new { approve = true, approver = "someone_else@example.com", reason = "duplicate attempt" });
        Assert.Equal(HttpStatusCode.Conflict, secondApprove.StatusCode);
    }
}
