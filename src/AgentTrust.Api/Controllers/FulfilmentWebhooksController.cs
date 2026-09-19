using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentTrust.Commerce;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentTrust.Api.Controllers;

[ApiController]
[Route("api/webhooks/fulfilment/{providerId}")]
public sealed class FulfilmentWebhooksController(
    IFulfilmentWebhookHandler handler,
    IFulfilmentStore store,
    IConfiguration configuration,
    ILogger<FulfilmentWebhooksController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> Receive(string providerId)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync(HttpContext.RequestAborted);
        var secret = configuration[$"Webhooks:Fulfilment:{providerId}:Secret"]
            ?? Environment.GetEnvironmentVariable($"FULFILMENT_WEBHOOK_SECRET_{Normalise(providerId)}");
        if (string.IsNullOrWhiteSpace(secret)) return Problem(statusCode: 503, title: "Webhook is not configured", extensions: new Dictionary<string, object?> { ["code"] = "PROVIDER_UNAVAILABLE" });
        if (!TryVerify(raw, secret, Request.Headers["X-AgentTrust-Timestamp"].ToString(), Request.Headers["X-AgentTrust-Signature"].ToString()))
            return Problem(statusCode: 401, title: "Invalid webhook signature", extensions: new Dictionary<string, object?> { ["code"] = "INVALID_WEBHOOK_SIGNATURE" });

        FulfilmentWebhookPayload? payload;
        try { payload = JsonSerializer.Deserialize<FulfilmentWebhookPayload>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return Problem(statusCode: 400, title: "Invalid webhook payload", extensions: new Dictionary<string, object?> { ["code"] = "INVALID_WEBHOOK_PAYLOAD" }); }
        if (payload is null || string.IsNullOrWhiteSpace(payload.EventId) || string.IsNullOrWhiteSpace(payload.FulfilmentId)
            || !Enum.TryParse<FulfilmentStatus>(payload.Status, true, out var status))
            return Problem(statusCode: 400, title: "Invalid webhook payload", extensions: new Dictionary<string, object?> { ["code"] = "INVALID_WEBHOOK_PAYLOAD" });
        if (!store.IsOwnedByProvider(payload.FulfilmentId, providerId))
            return Problem(statusCode: 404, title: "Fulfilment not found", extensions: new Dictionary<string, object?> { ["code"] = "RESOURCE_NOT_FOUND" });

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        try
        {
            var inserted = handler.HandleFulfilmentEvent(new(payload.EventId, payload.FulfilmentId, status, payload.ProviderTimestamp, hash));
            logger.LogInformation("Fulfilment webhook {ProviderEventId} for provider {ProviderId} accepted={Accepted}", payload.EventId, providerId, inserted);
            return Ok(new { accepted = true, duplicate = !inserted });
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("INVALID_STATE_TRANSITION", StringComparison.Ordinal))
        {
            return Problem(statusCode: 409, title: "Invalid state transition", extensions: new Dictionary<string, object?> { ["code"] = "INVALID_STATE_TRANSITION" });
        }
    }

    private static bool TryVerify(string raw, string secret, string timestampHeader, string signatureHeader)
    {
        if (!long.TryParse(timestampHeader, out var seconds)) return false;
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (Math.Abs((DateTimeOffset.UtcNow - timestamp).TotalMinutes) > 5) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{seconds}.{raw}"));
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signatureHeader)); }
        catch (FormatException) { return false; }
    }

    private static string Normalise(string value) => value.Replace('-', '_').ToUpperInvariant();
    public sealed record FulfilmentWebhookPayload(string EventId, string FulfilmentId, string Status, DateTimeOffset ProviderTimestamp);
}
