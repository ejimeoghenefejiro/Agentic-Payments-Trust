using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var options = E2eOptions.FromEnvironment(args);
var runner = new E2eRunner(options);
var report = await runner.RunAsync();
Directory.CreateDirectory(options.OutputDirectory);
var reportPath = Path.Combine(options.OutputDirectory, $"e2e-{report.RunId}.json");
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions.Pretty));
Console.WriteLine($"Run: {report.RunId}");
foreach (var scenario in report.Scenarios)
    Console.WriteLine($"{(scenario.Passed ? "PASS" : "FAIL")} {scenario.Name}: {scenario.Detail}");
Console.WriteLine($"Report: {Path.GetFullPath(reportPath)}");
Environment.ExitCode = report.Scenarios.All(x => x.Passed) ? 0 : 1;

internal sealed class E2eRunner(E2eOptions options)
{
    private readonly HttpClient _client = new() { BaseAddress = options.BaseUri, Timeout = TimeSpan.FromSeconds(120) };
    private readonly List<ScenarioResult> _results = [];
    private readonly string _runId = $"run_{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}_{Guid.NewGuid():N}";
    private string? _principalId;
    private RuntimeBuildInfo? _build;

    public async Task<E2eReport> RunAsync()
    {
        await Scenario("API readiness", ["GET /health/live", "GET /health/ready"], async () =>
        {
            await Expect(HttpMethod.Get, "/health/live", HttpStatusCode.OK);
            await Expect(HttpMethod.Get, "/health/ready", HttpStatusCode.OK);
            return "The live API and SQL system of record are healthy.";
        });

        if (!_results.Last().Passed) return Report();
        await Scenario("Build/version verification", ["GET /health/version"], async () =>
        {
            var response = await Expect(HttpMethod.Get, "/health/version", HttpStatusCode.OK);
            _build = JsonSerializer.Deserialize<RuntimeBuildInfo>(await response.Content.ReadAsStringAsync(), JsonOptions.Web)
                ?? throw new E2eFailure("API_BUILD_MISMATCH: version response was unreadable.");
            if (string.IsNullOrWhiteSpace(options.ExpectedBinarySha256))
                throw new E2eFailure("API_BUILD_MISMATCH: AGENTTRUST_E2E_EXPECTED_API_SHA256 is required.");
            if (!string.Equals(_build.BinarySha256, options.ExpectedBinarySha256, StringComparison.OrdinalIgnoreCase))
                throw new E2eFailure($"API_BUILD_MISMATCH: expected {options.ExpectedBinarySha256}, running {_build.BinarySha256}.");
            return $"Verified {_build.ApplicationVersion}; migration {_build.SchemaMigrationVersion ?? "none"}; binary {_build.BinarySha256}.";
        });
        if (!_results.Last().Passed) return Report();
        await Authenticate();
        if (_client.DefaultRequestHeaders.Authorization is null) return Report();

        await Scenario("Authenticated identity", ["GET /api/identity/me"], async () =>
        {
            var response = await Expect(HttpMethod.Get, "/api/identity/me", HttpStatusCode.OK);
            var json = await Json(response); _principalId = Text(json, "principalId");
            if (string.IsNullOrWhiteSpace(_principalId)) throw new E2eFailure("Identity response did not contain principalId.");
            return $"Authenticated principal {_principalId}.";
        });

        await Scenario("Consumer setup", ["GET /api/consumer/setup/status"], async () =>
        {
            var response = await Expect(HttpMethod.Get, "/api/consumer/setup/status", HttpStatusCode.OK);
            var json = await Json(response);
            if (!json.TryGetProperty("isReady", out var ready) || !ready.GetBoolean())
                throw new E2eFailure("The authenticated test principal is not fully configured. Complete agent, payment-method and mandate setup first.");
            return "Agent, reusable payment method and active mandate are present.";
        });

        await RequiredSurfaceAudit();
        if (options.Scenarios.Contains("grocery", StringComparer.OrdinalIgnoreCase)) await Grocery();
        if (options.Scenarios.Contains("ownership", StringComparer.OrdinalIgnoreCase)) await Ownership();
        if (options.Scenarios.Contains("rate-limit", StringComparer.OrdinalIgnoreCase)) await RateLimit();
        return Report();
    }

    private async Task Authenticate()
    {
        await Scenario("JWT authentication", ["POST /api/development/token"], async () =>
        {
            if (string.IsNullOrWhiteSpace(options.Subject) || string.IsNullOrWhiteSpace(options.Password))
                throw new E2eFailure("Set AGENTTRUST_E2E_SUBJECT and AGENTTRUST_E2E_PASSWORD. Credentials are never written to the report.");
            var response = await _client.PostAsJsonAsync("/api/development/token", new { subject = options.Subject, password = options.Password, displayName = options.DisplayName });
            if (response.StatusCode != HttpStatusCode.OK) throw await Failure(response, "Authentication failed");
            var json = await Json(response); var token = Text(json, "accessToken");
            _principalId = Text(json, "principalId");
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return $"JWT issued for principal {_principalId}.";
        });
    }

    private async Task Grocery()
    {
        var correlation = Guid.NewGuid().ToString("N");
        await Scenario("Grocery customer journey", ["POST /api/consumer/purchases/request", "GET /api/consumer/purchases/{id}", "GET /api/consumer/purchases/{id}/receipt", "GET /api/consumer/purchases/{id}/audit"], async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/consumer/purchases/request")
            { Content = new StringContent("New order. I have £20. Buy enough groceries for dinner and deliver them. Choose good value. Use your best judgement.", Encoding.UTF8, "text/plain") };
            request.Headers.Add("X-Correlation-ID", correlation);
            request.Headers.Add("Idempotency-Key", $"{_runId}-grocery");
            var response = await _client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.OK) throw await Failure(response, "Grocery request failed");
            var json = await Json(response);
            var execution = Property(json, "execution") ?? Property(Property(json, "result"), "execution");
            var purchaseId = execution is { } value ? Text(value, "executionId") : null;
            if (string.IsNullOrWhiteSpace(purchaseId)) throw new E2eFailure($"Request did not reach durable execution; response was planning/clarification only: {json}");
            await Expect(HttpMethod.Get, $"/api/consumer/purchases/{purchaseId}", HttpStatusCode.OK);
            await Expect(HttpMethod.Get, $"/api/consumer/purchases/{purchaseId}/receipt", HttpStatusCode.OK);
            var audit = await Expect(HttpMethod.Get, $"/api/consumer/purchases/{purchaseId}/audit", HttpStatusCode.OK);
            var auditJson = await Json(audit);
            if (!auditJson.GetProperty("isValid").GetBoolean()) throw new E2eFailure("Purchase audit chain is invalid.");
            return $"Purchase {purchaseId} completed with a durable receipt and valid audit chain.";
        }, correlation);
    }

    private async Task Ownership()
    {
        await Scenario("Cross-principal ownership", ["GET /api/consumer/purchases/{id}"], () =>
            throw new E2eFailure("A second authenticated user is required. Set AGENTTRUST_E2E_SECOND_SUBJECT and AGENTTRUST_E2E_SECOND_PASSWORD."));
    }

    private async Task RateLimit()
    {
        await Scenario("Public API rate limit", ["GET /health/live x 140"], async () =>
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 140).Select(_ => _client.GetAsync("/health/live")));
            if (!responses.Any(x => x.StatusCode == HttpStatusCode.TooManyRequests)) throw new E2eFailure("No HTTP 429 response was observed.");
            return "Burst traffic was limited without stopping the API.";
        });
    }

    private async Task RequiredSurfaceAudit()
    {
        var probes = new[]
        {
            ("Restaurant HTTP journey", "/api/consumer/restaurant/orders"),
            ("Home-service HTTP journey", "/api/consumer/home-services/bookings"),
            ("Fulfilment status HTTP journey", "/api/consumer/fulfilments/probe"),
            ("Operator reconciliation HTTP journey", "/api/operations/reconciliation/probe")
        };
        foreach (var (name, path) in probes)
            await Scenario(name, [$"GET {path}"], async () =>
            {
                var response = await _client.GetAsync(path);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                    throw new E2eFailure("Required authenticated HTTP endpoint is not implemented.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new E2eFailure("Endpoint availability could not be verified because the request was rate limited.");
                if ((int)response.StatusCode >= 500) throw await Failure(response, "Endpoint failed");
                return $"Endpoint exists and returned HTTP {(int)response.StatusCode}.";
            });
    }

    private async Task<HttpResponseMessage> Expect(HttpMethod method, string path, HttpStatusCode status)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(method, path));
        if (response.StatusCode != status) throw await Failure(response, $"Expected HTTP {(int)status} from {path}");
        return response;
    }

    private async Task Scenario(string name, IReadOnlyList<string> endpoints, Func<Task<string>> action, string? correlationId = null)
    {
        try { _results.Add(new(name, true, await action(), endpoints, correlationId)); }
        catch (Exception ex) { _results.Add(new(name, false, ex.Message, endpoints, correlationId)); }
    }

    private E2eReport Report() => new(_runId, DateTimeOffset.UtcNow, options.BaseUri.ToString(), _principalId,
        _build?.Environment, _build?.ApplicationVersion, _build?.GitCommitSha, _build?.BinarySha256,
        _build?.SchemaMigrationVersion, _results);
    private static async Task<E2eFailure> Failure(HttpResponseMessage response, string prefix) => new($"{prefix}: HTTP {(int)response.StatusCode}; {await response.Content.ReadAsStringAsync()}");
    private static async Task<JsonElement> Json(HttpResponseMessage response) => JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    private static string? Text(JsonElement json, string name) => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static JsonElement? Property(JsonElement? json, string name) => json is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) ? property : null;
}

internal sealed record E2eOptions(Uri BaseUri, string? Subject, string? Password, string? DisplayName,
    string? ExpectedBinarySha256, IReadOnlyList<string> Scenarios, string OutputDirectory)
{
    public static E2eOptions FromEnvironment(string[] args)
    {
        var selected = args.FirstOrDefault(x => x.StartsWith("--scenarios=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? ["grocery", "rate-limit"];
        return new(new Uri(Environment.GetEnvironmentVariable("AGENTTRUST_E2E_BASE_URL") ?? "http://localhost:5104"),
            Environment.GetEnvironmentVariable("AGENTTRUST_E2E_SUBJECT"), Environment.GetEnvironmentVariable("AGENTTRUST_E2E_PASSWORD"),
            Environment.GetEnvironmentVariable("AGENTTRUST_E2E_DISPLAY_NAME") ?? "E2E User",
            Environment.GetEnvironmentVariable("AGENTTRUST_E2E_EXPECTED_API_SHA256"), selected,
            Environment.GetEnvironmentVariable("AGENTTRUST_E2E_RESULTS") ?? Path.Combine("results", "e2e"));
    }
}

internal sealed record ScenarioResult(string Name, bool Passed, string Detail, IReadOnlyList<string> Endpoints, string? CorrelationId);
internal sealed record E2eReport(string RunId, DateTimeOffset FinishedAt, string BaseUrl, string? PrincipalId,
    string? Environment, string? ApplicationVersion, string? GitCommitSha, string? BinarySha256,
    string? SchemaMigrationVersion, IReadOnlyList<ScenarioResult> Scenarios);
internal sealed record RuntimeBuildInfo(string Application,string ApplicationVersion,string AssemblyVersion,string? GitCommitSha,
    DateTime BuildTimestampUtc,string Environment,string? SchemaMigrationVersion,string BinarySha256,string ModuleVersionId);
internal sealed class E2eFailure(string message) : Exception(message);
internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
