# Trustworthy Agentic Payments — Prototype

C#/.NET reference implementation of the trust and authorisation layer described in
`Trustworthy_Agentic_Payments_PhD_Standalone.docx.pdf`: agent identity, principal binding,
delegated financial authority, deterministic policy enforcement, evidence provenance, audit
reconstruction, and a human-approval workflow for autonomous financial agents.

The central design principle, enforced end-to-end: **the agent proposes, the trust layer
authorises.** A Semantic Kernel agent turns a natural-language instruction plus evidence into
a structured transaction intent; it never decides approve/deny/escalate. That decision belongs
entirely to the deterministic policy engine.

```
Natural-language instruction
         v
Semantic Kernel Agent  (AgentTrust.Agents)
         v
Structured output validation  (AgentOutputValidator)
         v
TransactionIntent
         v
Agent Identity -> Principal Binding -> Delegated Authority -> Policy Engine -> Evidence
         v
APPROVE / DENY / ESCALATE
         v
   DENY: stop.                 ESCALATE: pending ApprovalRequest,           APPROVE: continue
                                payment withheld until a human decides.
                                            |
                                 human APPROVE / REJECT
                                            v
Payment Adapter (mock/sandbox) -- only ever invoked after APPROVE, direct or human-approved
         v
Audit Record  ->  appended to a hash-chained Audit Ledger (persisted; tamper-evident)
```

## Structure

```
src/
  AgentTrust.Core          domain models (identity, binding, authority, intent, decision,
                            approval, principal, merchant, audit) + in-memory store interfaces
                            and implementations for every one of them (used by tests and as
                            the no-database fallback)
  AgentTrust.Policy         deterministic policy engine (identity -> binding -> authority ->
                            duplicate check -> scope -> merchant -> limits -> time window ->
                            evidence -> human-approval threshold)
  AgentTrust.Payments       mock/sandbox payment adapter with forced-failure injection
  AgentTrust.Evidence       evidence hashing, audit record construction, precision/recall/F1,
                            AuditLedger (hash-chained, tamper-evident, append-only,
                            persistence-backed via IAuditRecordStore)
  AgentTrust.Agents         IPaymentAgent + SemanticKernelPaymentAgent (real SK agent),
                            AgentOutputValidator (schema/evidence/currency validation),
                            AgentFactory (live OpenAI connector or deterministic scripted
                            connector), ScriptedChatCompletionService
  AgentTrust.Orchestration  TrustFramework: the shared orchestrator (identity -> ... -> audit,
                            plus the human-approval resolve path) used by both the Runner and
                            the Api so the transaction lifecycle is implemented exactly once
  AgentTrust.Data           EF Core: AgentTrustDbContext + entities + Ef*Store implementations
                            of every Core store interface, targeting PostgreSQL (Npgsql) in
                            production; the same context also runs against SQLite in tests
  AgentTrust.Api            ASP.NET Core Web API exposing the full lifecycle over HTTP
  AgentTrust.Runner         Experiment runner: scenario suite, cross-model experiment,
                            end-to-end demo (see modes below)
tests/
  AgentTrust.Tests          xUnit: policy-engine unit tests, agent-output-validator unit tests,
                            audit-ledger tamper-detection tests, approval-workflow tests,
                            EF-Core persistence tests (real SQLite database), API integration
                            tests (WebApplicationFactory), and a data-driven theory test running
                            every scenario in scenarios/ against its ground truth (54 tests total)
scenarios/
  s01..s15                  direct-injection scenarios (S1-S15 from the concept document)
  s16..s19                  agent-mode scenarios: valid proposal, malformed output, fabricated
                            evidence reference, prompt injection via evidence content
results/
  experiment_summary.json, scenario_results.json, cross_model_results.json  (gitignored)
Dockerfile                  builds AgentTrust.Runner
Dockerfile.api               builds AgentTrust.Api
docker-compose.yml           Api + PostgreSQL + Runner (see below)
```

## Running

```bash
dotnet build
dotnet test                                  # 54 tests: unit + persistence + API + scenario suite
dotnet run --project src/AgentTrust.Runner   # runs all 19 scenarios, prints pass/fail, writes results/*.json
```

### Three runner modes

```bash
dotnet run --project src/AgentTrust.Runner                 # scenario suite (default)
dotnet run --project src/AgentTrust.Runner -- --demo         # single end-to-end diesel-purchase walkthrough
dotnet run --project src/AgentTrust.Runner -- --cross-model  # compares agent "models" through IPaymentAgent
```

`--demo` narrates the full lifecycle from the concept document — business registers, agent
registered, authority delegated, natural-language instruction, agent observes evidence, agent
proposes, identity/authority/policy/evidence checks, transaction authorised, mock payment,
audit package, audit chain verification — ending in `APPROVED / Payment executed / Evidence
traceable / Audit chain valid`.

`--cross-model` runs a shared policy-engine metrics block once (from the 15 direct-injection
scenarios, which never touch an agent) plus per-model metrics (correct intent-generation rate,
end-to-end accuracy, evidence precision/recall/F1, agent/policy/total latency) for each
configured model profile, written to `results/cross_model_results.json`. Without
`OPENAI_API_KEY` it compares two scripted profiles (`scripted-baseline` vs a deliberately
`scripted-degraded` one) to demonstrate the mechanism; set the key to add a `live:<model>`
profile automatically.

### In Docker

```bash
docker build -t agentic-payment-trust:mvp .
docker run --rm agentic-payment-trust:mvp
# to keep the results on the host (Docker Desktop on Windows needs the leading //):
docker run --rm -v "//c/Payment Research/Agentic-Payments-Trust/results-docker:/app/results" agentic-payment-trust:mvp
```

### Running against a local SQL Server instance

If you have a local SQL Server (including a named instance) instead of PostgreSQL, point the
API at it via `SQLSERVER_CONNECTION` / `ConnectionStrings:SqlServer` — it takes priority over
`POSTGRES_CONNECTION` if both happen to be set. Two ways to set it:

**`appsettings.json`** (checked into this repo, since a `Trusted_Connection=True` Windows-auth
string carries no password/secret — this is the default for this machine):
```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=DESKTOP-3T0MF62\\MSSQLSERVER1;Database=AgentTrust;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
```
With this in place, `dotnet run --project src/AgentTrust.Api` just works, no environment
variable needed. This ties the default to this specific machine name — for a different
machine/server, use the environment variable instead rather than editing this file.

**Environment variable** (overrides `appsettings.json`; use this for a different server, CI, or
a connection string that *does* carry a secret — e.g. SQL auth with a password — which must
never go into a committed file):
```powershell
$env:SQLSERVER_CONNECTION = "Server=YOUR-MACHINE\INSTANCE_NAME;Database=AgentTrust;Trusted_Connection=True;TrustServerCertificate=True;"
dotnet run --project src/AgentTrust.Api
```

The API creates the `AgentTrust` database and all tables on first run (`Database.EnsureCreated()`
— see Known Limitations re: no migrations). Verified against the real local named instance
(Windows Authentication) both ways: registered a principal/agent/binding/authority, submitted a
transaction that escalated on the human-approval threshold, approved it via
`POST /api/approvals/{id}` (payment executed only at that point), and confirmed
`GET /api/audit/verify` reported a valid chain — including a second run reading the config
purely from `appsettings.json` with no environment variable set, which returned the same
already-persisted chain (`entryCount: 2`), confirming the file-based config path works
end-to-end.

**Bug found and fixed while adding SQL Server support:** `EfTransactionLedger.AmountSpentToday`
originally built its "same calendar day" filter two different ways, and *both* failed on at
least one supported provider — `DateOnly.FromDateTime(...)` inside the query is rejected
outright by SQL Server's LINQ translator, and a plain `DateTimeOffset` range comparison is
rejected by SQLite's provider (no native `DateTimeOffset` comparison support). The fix
materialises the agent's transaction rows first, then filters by date and decision in memory —
sidestepping provider-specific SQL translation entirely. Has a regression test
(`TransactionLedgerAmountSpentTodayTranslatesToSql`) that runs against a real SQLite database,
so a future change that reintroduces a non-translatable expression fails CI immediately instead
of only failing against whichever provider a developer happens to be running.

### Full stack with docker-compose (API + PostgreSQL)

```bash
cp .env.example .env   # set POSTGRES_PASSWORD (and OPENAI_API_KEY if you want live agent calls)
docker compose up -d postgres api
curl http://localhost:8080/api/audit/verify
docker compose run --rm runner --demo   # one-shot: runs the Runner's own Dockerfile, talks to the same Postgres if you wire POSTGRES_CONNECTION into it too
docker compose down
```

The `runner` service uses the `tools` profile (it's a one-shot CLI, not a long-running
service) — start it explicitly with `docker compose run --rm runner [args]`. All configuration
(`POSTGRES_CONNECTION`, `OPENAI_API_KEY`, `OPENAI_MODEL`) comes from environment variables /
`.env`; nothing is hard-coded, and `.env` is gitignored so no secret is ever committed.

### Running the agent against a real LLM

By default, agent-mode scenarios use `ScriptedAgentResponse` — a deterministic canned model
output baked into the scenario JSON, so the suite is fully reproducible without any API key.
`AgentFactory.CreateLive` only runs when `OPENAI_API_KEY` is set; `OPENAI_MODEL` is optional
(defaults to `gpt-4o-mini`). No key is required, or ever committed, for the default reproducible
run — pick whichever of these fits how you're running the project:

**PowerShell, current session only** (gone when you close the terminal — good for a quick test):
```powershell
$env:OPENAI_API_KEY = "sk-..."
dotnet run --project src/AgentTrust.Runner -- --demo
```

**PowerShell, persisted for your Windows user account** (available in every new terminal from
then on; needs a fresh terminal window to take effect):
```powershell
setx OPENAI_API_KEY "sk-..."
```
Or via the GUI: Windows Settings → System → About → Advanced system settings → Environment
Variables → New (under "User variables").

**`.env` file, for Docker / docker-compose** — copy [`.env.example`](.env.example) to `.env`
and fill in your key. `.env` is already gitignored, so it never gets committed. Then:
```bash
docker run --rm --env-file .env agentic-payment-trust:mvp
# or, for the full stack, docker-compose reads .env automatically:
docker compose up -d
```

**Do not** put the key directly in `appsettings.json`, a scenario JSON file, or anywhere else
that lives inside the repo.

## Two scenario modes

- **Direct-injection** (`s01`-`s15`): the scenario supplies the `TransactionIntent` directly.
  Isolates **policy-engine correctness** from agent behaviour.
- **Agent mode** (`s16`-`s19`): the scenario supplies a natural-language `UserInstruction` +
  evidence; a real `SemanticKernelPaymentAgent` proposes the intent, which is validated
  (`AgentOutputValidator`) before it can reach the policy engine. Isolates
  **agent-intent-generation correctness** in addition to policy correctness. Each scenario also
  carries `ExpectedAgentOutputValid`, the ground truth the cross-model experiment uses to score
  intent-generation correctness independently of the eventual policy decision.

## API

Base path `/api`. Falls back to in-memory stores if `POSTGRES_CONNECTION` (or
`ConnectionStrings:Postgres`) isn't set, so it's runnable without a database for exploration.

| Endpoint | Purpose |
|---|---|
| `POST /agents` | Register an agent identity |
| `GET /agents/{id}` | Fetch an agent |
| `POST /agents/{id}/revoke` | Revoke an agent's credential |
| `POST /principals` | Register a principal |
| `POST /bindings` | Bind an agent to a principal |
| `POST /authorities` | Grant delegated authority |
| `GET /authorities/{id}` | Fetch an authority |
| `POST /authorities/{id}/revoke` | Revoke an authority |
| `POST /transactions/request` | Submit a transaction — set `userInstruction` for agent-driven natural-language execution, or leave it null and fill `action`/`merchant`/`amount` directly |
| `GET /transactions/{id}` | Fetch intent + decision + payment + approval state |
| `POST /approvals/{transactionId}` | Resolve a pending ESCALATE (`approve`, `approver`, `reason`) — payment only executes here, on approve |
| `GET /audit/{transactionId}` | Fetch the audit record for a transaction |
| `GET /audit/verify` | Verify the entire persisted audit chain |

## Human approval workflow

When the policy engine returns `ESCALATE`, `TrustFramework` creates a pending `ApprovalRequest`
and the payment adapter is **not** invoked. `POST /approvals/{transactionId}` (or
`TrustFramework.ResolveApproval` directly) resolves it:

- **Approve** — resumes the originally stored intent and evidence, executes payment for the
  first time, records approver/timestamp/reason/original-decision/final-outcome, and appends a
  new audit entry with `HUMAN_APPROVED` in the reason codes.
- **Reject** — finalises the transaction as `Deny`; payment never executes.
- Resolving an already-resolved approval throws (`409 Conflict` at the API layer).

`ApprovalWorkflowTests` proves the payment adapter is never called before resolution (by
asserting `MockPaymentAdapter.SubmittedTransactionIds` is empty while a transaction is
pending), and that it's called exactly once after approval.

## What's implemented

- Agent identity, principal-agent binding, delegated authority (scope, limits, merchant/time
  window, human-approval threshold, expiry, revocation) — all persisted through EF Core
- Deterministic policy engine producing Approve / Deny / Escalate with reason codes
- Idempotency/duplicate-payment detection; mock payment adapter with forced-failure injection
- Evidence manifest with precision/recall/F1 traceability metrics
- **Real Semantic Kernel agent** with structured-output validation, pluggable live/scripted
  connector (`AgentFactory`)
- **Hash-chained, persistence-backed audit ledger** (`AuditLedger` + `IAuditRecordStore`):
  `Verify()` detects changed/deleted/reordered entries; rehydrates correctly across process/
  request boundaries (a real bug during this build — a fresh per-request `AuditLedger` was
  restarting sequence numbers at zero instead of loading prior history; fixed by loading
  existing entries from the persistent store in the constructor, with a regression test)
- **Persistent storage** (`AgentTrust.Data`): EF Core + PostgreSQL (Npgsql) in production,
  SQLite in tests, covering agents, principals, bindings, authorities, merchants, transaction
  intents, policy decisions, payment outcomes, evidence manifests, approvals, and the audit
  chain. In-memory implementations remain available and are the default for tests/no-DB runs.
- **`AgentTrust.Api`**: full ASP.NET Core Web API per the endpoint table above, including
  natural-language-driven transaction requests
- **Human approval workflow**: pending approvals, approve/reject with approver/timestamp/reason,
  payment gated on resolution
- **Cross-model experiment harness** (`--cross-model`): shared policy metrics + per-model
  intent-generation/accuracy/evidence/latency metrics through the same `IPaymentAgent` seam
- **End-to-end demo** (`--demo`): single command narrating the full diesel-purchase lifecycle
  from registration to a verified audit chain
- **`docker-compose.yml`**: API + PostgreSQL + Runner (one-shot `tools` profile), config and
  secrets from environment variables only, verified running in this environment
- 19 ground-truth scenarios (15 direct-injection + 4 agent-mode), 54 automated tests across
  policy correctness, agent validation, persistence, approvals, audit tampering, and API
  integration

## Known limitations

- Store interfaces (`IAgentRegistry`, etc.) are synchronous; the EF-Core implementations call
  EF Core's sync APIs rather than being async end-to-end. Fine for this prototype's throughput,
  worth revisiting before any real load.
- No authentication/authorization on the API itself (no API keys, no auth middleware) — anyone
  who can reach it can call every endpoint. Out of scope for the trust-layer research question,
  but a real deployment needs it.
- The cross-model harness's "degraded" profile is a hand-authored scripted variant, not a
  second real model — meaningful multi-model comparison needs a second live connector
  (Azure OpenAI, Anthropic via an OpenAI-compatible shim, etc.) configured through
  `AgentFactory`.
- EF Core migrations aren't set up (`Database.EnsureCreated()` is used instead) — fine for a
  research prototype, not for a production schema-change workflow.
- No merchant-registry enforcement inside the policy engine yet — `IMerchantStore`/`Merchant`
  exist and are exposed for future use, but `PolicyEngine` still checks `DelegatedAuthority.
  ApprovedMerchants` (a name list on the authority itself), not the merchant registry.
