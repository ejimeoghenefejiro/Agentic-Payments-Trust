# AgentTrust black-box E2E tests

The harness calls a separately running API over HTTP and has no project reference to API,
domain, persistence or connector code. The supported entry point is `run-e2e.ps1`. It publishes the current API to an isolated
temporary directory, applies migrations, starts that exact binary on a dedicated port,
checks its SHA-256 through `/health/version`, runs the selected black-box scenarios, and
stops only the API process that it started.

```powershell
$env:AGENTTRUST_E2E_SQLSERVER_CONNECTION = "Server=...;Database=AgentTrustE2E;..."
$env:AGENTTRUST_E2E_SUBJECT = "e2e-user@example.test"
$env:AGENTTRUST_E2E_PASSWORD = "use-a-local-test-password"
.\tests\AgentTrust.E2E\run-e2e.ps1
```

The database must contain a fully configured test principal (agent, reusable payment
method and active mandate). Secrets are supplied only through environment variables and
are not written to the JSON report.
