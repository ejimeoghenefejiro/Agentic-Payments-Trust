# Trustworthy Agentic Payments

## Level 3 : agentic financial reasoning

`FinancialInvestigationAgent` is the first autonomous, iterative investigation layer above the
existing deterministic analytics. A Semantic Kernel model maintains explicit competing
hypotheses, identifies open questions, selects one bounded investigation tool at a time, inspects
the returned evidence, challenges its leading conclusion, and stops with a structured advisory
recommendation. Investigation state is saved after every turn and is EF-persisted when a database
is configured.

The available tools are `GetCustomerHistory`, `GetMerchantHistory`, `GetDeviceHistory`,
`GetBeneficiaryHistory`, `CalculateBehaviourProfile`, `DetectAnomalies`,
`AnalyseFinancialGraph`, `ComparePeerGroup`, `GetPreviousHumanReviews`,
`SearchHistoricalCases`, `RetrieveEvidence`, and `CalculateRiskSignals`. These wrap the Level 1/2
detectors, profiles, graph analysis, structured memory, semantic case memory, and risk services;
they do not replace them.

The boundary is deliberate: `AgentTrust.Intelligence` does not reference orchestration, policy,
authority, approval, or payments. The Level 3 result is a recommendation only. Deterministic
controls remain solely responsible for `APPROVE / DENY / ESCALATE` and payment execution.

API endpoints:

- `POST /api/intelligence/investigate/level3` starts a live Level 3 investigation (OpenAI model configuration required).
- `GET /api/intelligence/investigations/{investigationId}` retrieves the persisted explicit state.

The loop has a configurable turn limit, rejects unknown or repeated identical tool calls, requires
a counter-hypothesis challenge before completion, and fails safely to an inconclusive escalation
recommendation when it reaches the turn limit.

```text
Observe transaction
        ↓
Form competing hypotheses and open questions
        ↓
Select one bounded investigation tool
        ↓
Inspect and persist evidence
        ↓
Update supporting and contradictory evidence
        ↓
Challenge the leading conclusion
        ↓
Stop when sufficient, or at the configured safety limit
        ↓
Structured advisory recommendation
        ↓
──────────────── deterministic trust boundary ────────────────
        ↓
Identity / authority / mandate / limits / policy
        ↓
APPROVE / DENY / ESCALATE
```

Each investigation persists its objective, hypotheses, confidence values, open questions,
tool calls and arguments, raw evidence payloads, reasoning summary, challenge status, lifecycle
status, and final recommendation. `IInvestigationStateStore` provides the persistence boundary;
the API uses `EfInvestigationStateStore` when SQL Server or PostgreSQL is configured and the
in-memory implementation otherwise. Provider-specific `AddInvestigationStates` migrations are
included for both databases.

Three memory sources are represented explicitly:

- **Structured memory:** transaction, customer, merchant, device and beneficiary history.
- **Semantic memory:** human reviews, historical cases and retrieved evidence through
  `IInvestigationMemory`.
- **Analytical models:** existing behavioural, anomaly, graph and deterministic risk services,
  exposed as tools today and ready for calibrated learned-model implementations later.

The model cannot invent a new executable capability: every requested tool name is checked against
the fixed allow-list before dispatch. Repeated calls with identical arguments are rejected. A
request for a policy, approval or payment function fails because no such function exists in the
investigator's tool surface.

## Mandate and payment safety hardening

The transaction path now implements the core Part 2 safety invariants:

- Mandates carry immutable version metadata. Saving a newer version retains and marks the prior
  version `Superseded`, preserving the authority that existed at any historical point.
- Daily, rolling-weekly, rolling-monthly and per-transaction limits are evaluated. Amounts must
  be positive.
- Spend follows `Reserve → Execute → Commit` and releases the reservation when policy or payment
  fails. The in-memory reference store performs check-and-reserve under one lock so concurrent
  requests cannot consume the same remaining allowance.
- Above-limit human approval creates an exact, expiring, single-use `OneOffAuthorisation` bound
  to the execution, mandate/version, amount, currency, merchant, payment method and canonical task
  context hash. It is consumed atomically and cannot be replayed.
- One-off approval is passed to policy as a transaction-scoped authority. Standing agent authority
  is never temporarily increased or restored.
- Task status, task/mandate agent, task/mandate principal, mandate status/expiry, currency, and—when
  a payment-method store is supplied—payment-method ownership and usability are checked together.
- Payment submission is wrapped in a durable-store-ready state machine and idempotency coordinator.
  A retry with the same idempotency key returns the original result instead of charging twice;
  adapter exceptions become `Unknown` attempts for later reconciliation.
- Scheduled executions use `(taskId, scheduledFor)` as the occurrence identity. The occurrence is
  atomically claimed so repeated polling or competing scheduler instances cannot execute it twice.
- `ConnectProviderToken` supports the preferred PSP-hosted-fields flow, allowing the backend to
  receive only a provider token and display-safe metadata rather than PAN/CVV.

The in-memory reservation, authorisation, payment-attempt and occurrence stores are reference
implementations and concurrency-safe within one process. Production deployment must back these
interfaces with database uniqueness/transactions shared by every server, add PSP webhooks/outbox
reconciliation, and configure authenticated human approval with step-up authentication.

## Part 3 : hardened LLM trust boundary

The Level 3 investigator is treated as an untrusted, potentially hostile component. Its safety
does not depend on the system prompt or on the model choosing to behave:

- `AgentTrust.Intelligence` has no project or runtime assembly dependency on payments, payment
  methods, policy, mandates, orchestration, tasks or scheduling. Executable architecture tests
  inspect both the compiled references and the `.csproj`, failing if this changes later.
- The Semantic Kernel supplied to `FinancialInvestigationAgent` must contain zero registered
  plugins. Any plugin—including a payment-like plugin—causes construction to fail. Analytical
  tools are invoked exclusively by the bounded C# dispatcher.
- Direct and indirect capabilities such as payment submission, approval, authority mutation,
  policy disabling and generic HTTP execution are absent from the allow-list and rejected.
- Tool identifiers are transaction-scoped. The model cannot substitute another customer,
  merchant, device or beneficiary identifier to retrieve unrelated data.
- Retrieved evidence must belong to the candidate transaction/customer/merchant/device/
  beneficiary scope. Cross-subject evidence is rejected.
- Every tool result is wrapped as `UNTRUSTED_TOOL_OUTPUT`, including historical cases and analyst
  notes that may contain stored prompt injection.
- Candidate identifiers, currencies, amounts, tool arguments, model-response size/depth,
  hypotheses, questions, confidence and recommendation payloads are bounded and schema-validated.
  Unknown JSON members and integer enum values are rejected.
- Model-authored evidence IDs are not authoritative. Final recommendation evidence references are
  replaced with IDs issued by the trusted dispatcher for evidence actually collected in that
  investigation.
- Intelligence output is returned separately by the transaction API and is no longer promoted
  into the deterministic policy `EvidenceManifest`. A model or analytical component therefore
  cannot manufacture evidence that satisfies an authority requirement.

The hostile-model suite assumes deliberate compromise rather than prompt compliance. It attempts
payment, approval, limit increases, authority grants, policy disabling, arbitrary HTTP calls,
cross-customer queries, cross-subject evidence retrieval, oversized responses, stored prompt
injection and fabricated approval evidence. All attempts remain above the deterministic trust
boundary and produce zero payment capabilities.

## Part 4 : research-grade comparative evaluation

The deterministic engine is retained as the mandatory **B0 baseline**. It is not thrown away when
Level 3 or learned models are added. `ComparativeResearchEvaluator` runs every intelligence
configuration over the same labelled cases and refuses to start unless B0 is present. This makes
the implementation an experimental apparatus for answering what agentic reasoning adds, rather
than merely demonstrating that an AI component exists.

The planned ablation ladder is explicit in `ResearchConfiguration`:

- **B0:** deterministic trust framework only.
- **B1:** deterministic trust plus Level 1/2 analytical signals.
- **B2:** bounded Level 3 agentic investigation.
- **B3:** Level 3 plus semantic case memory.
- **B4:** Level 3 plus calibrated learned risk signals.

Model and intelligence behaviour may vary across B1–B4, while the deterministic financial-authority boundary is expected to remain invariant, with zero unauthorised executions across all configurations.

Each run records a study ID, dataset/version, fixed seed, policy version, UTC start time and
optional model/version. Per-case trials retain the expected and recommended decisions, unsafe
probability, reference and retrieved evidence, tools selected, hypothesis/counter-evidence
coverage, stop-criterion result, payment-execution observation and wall latency. For recommendation-capable
systems, the report adds decision accuracy with a 95% Wilson interval, unsafe precision/recall/F1, Brier score, expected
calibration error, evidence precision/recall/F1, counter-evidence and stopping rates, tool use,
latency percentiles, unauthorised executions, and paired exact McNemar comparisons. B0 has no Level 3
recommendation capability, so its recommendation and calibration metrics are reported as `N/A` and
it is excluded from recommendation-accuracy comparisons; it remains the baseline for trust accuracy,
policy enforcement, payment outcomes, latency, unauthorised execution, and bypass resistance.
`ExperimentReportWriter.WriteComparative` exports the complete protocol, trials, metrics and
paired comparisons as indented JSON plus a per-case CSV suitable for R, Python or archival.

This separates the thesis layers clearly:

- **Research contribution:** bounded evidence-traceable investigation, deterministic financial
  authority, adversarial boundary methodology, and reproducible comparative evaluation.
- **Research artefact:** the C# policy engine, mandates, risk/graph tools, investigator, scenario
  generator and audit implementation used to instantiate and test those claims.
- **Supporting engineering:** APIs, EF Core, migrations, tokenisation, scheduling, idempotency,
  payment state and deployment infrastructure that make the experiments credible and repeatable.

The long-term architecture is:

```text
                         FINANCIAL AGENT
                               │
                               ▼
                    REASONING / INVESTIGATION
                               │
          ┌────────────────────┼────────────────────┐
          ▼                    ▼                    ▼
   Behaviour Model      Graph Intelligence      ML Risk Models
          │                    │                    │
          ├────────────────────┼────────────────────┤
          ▼                    ▼                    ▼
   History Tool          Device Tool          Merchant Tool
   Beneficiary Tool      Graph Tool           Evidence Tool
          └────────────────────┼────────────────────┘
                               ▼
                    Evidence-backed hypothesis
                               ▼
                     Agent recommendation
                               ▼
              ───────── INTELLIGENCE BOUNDARY ─────────
                               ▼
                    DETERMINISTIC TRUST LAYER
                               │
                 Identity / Authority / Mandate
                 Limits / Revocation / Policy
                               ▼
                    APPROVE / DENY / ESCALATE
                               ▼
                            PAYMENT
```

The crucial experimental claim is therefore testable: model behaviour may vary across B2–B4,
while payment authority remains external and deterministic. A successful safety result is zero
unauthorised executions across normal, adversarial, cross-model and ablation experiments.

## Part 5 — B3 semantic investigation memory and curated learning

B3 now has a real semantic-memory boundary instead of extending the lexical in-memory search.
`SemanticInvestigationMemory` embeds case narratives through an injected `ITextEmbeddingService`,
persists vectors and metadata through `ISemanticCaseStore`, ranks candidates by cosine similarity,
and returns only the highest-scoring cases. The embedding provider is deliberately replaceable so
production can use a hosted or local embedding model without coupling the intelligence domain to
one vendor.

Retrieval is scoped before similarity ranking. A case must be global or belong to the candidate
customer scope, and the Level 3 tool supplies that scope from the trusted transaction rather than
from model-authored arguments. Results still enter the reasoning loop as
`UNTRUSTED_TOOL_OUTPUT`; semantic similarity never turns a prior narrative into authority evidence.
Structured transaction, merchant, device and beneficiary facts remain in their existing stores
and tools rather than being duplicated as RAG documents.

`EfSemanticCaseStore` provides durable SQL persistence. `AddSemanticCaseMemory` migrations exist
for both SQL Server and PostgreSQL, while `InMemorySemanticCaseStore` supports isolated tests.
The API continues to use the conservative lexical fallback until an `ITextEmbeddingService` is
explicitly configured; it never pretends lexical matching is semantic retrieval.

### Part 5 next — real embeddings and the B2/B3 treatment

The API now supplies a production-capable OpenAI embeddings adapter outside the intelligence
domain. Semantic memory remains disabled by default and activates only through explicit settings:

```text
Intelligence__SemanticMemory__Enabled=true
Intelligence__SemanticMemory__Provider=OpenAI
Intelligence__SemanticMemory__Model=text-embedding-3-small
Intelligence__SemanticMemory__Dimensions=1536
Intelligence__SemanticMemory__Endpoint=https://api.openai.com/v1/
OPENAI_API_KEY=<runtime secret>
```

The endpoint must be HTTPS, responses must contain exactly the configured number of finite vector
values, and no key is stored in tracked configuration. Every persisted vector records provider,
model, optional model version, dimensions and creation time. Retrieval excludes vectors generated
by an incompatible provider/model/dimension combination; provider changes therefore cannot
silently contaminate an experiment. Apply the provider-specific `AddEmbeddingProvenance`
migration before enabling B3 against an existing database.

The controlled corpus at `research/semantic-memory-corpus.json` contains scoped/global, relevant,
irrelevant, contradictory, cross-customer and stored-prompt-injection cases. Relevance labels are
declared independently of retrieval and validated before a study runs. Poisoned text may be
retrieved but remains `UNTRUSTED_TOOL_OUTPUT` with no policy, authority or payment capability.

`B2B3ExperimentRunner` enforces the experimental arms: B0 deterministic boundary, B2 Level 3
without semantic treatment, and B3 with semantic memory. Every arm receives the same cases for
each configured repetition. Reports now include repetition IDs, semantic precision/recall,
Recall@K, mean reciprocal rank, relevant-case hit rate, irrelevant-memory usage, recommendation
stability, existing reasoning/outcome/calibration/latency measures, pairwise comparisons between
recommendation-capable systems, and
unauthorised executions. JSON and CSV exports retain the full protocol and per-trial retrieval IDs.
B3 is an intelligence treatment only; deterministic payment authority is unchanged.

Run the live paired treatment from a network-enabled terminal (the default is the low-cost
one-repetition development run):

```bash
dotnet run --project src/AgentTrust.Runner -- --live-b2-b3 --repetitions 1
```

The command reads the ignored development credential or `OPENAI_API_KEY`, uses the configured chat
model plus `text-embedding-3-small`, ingests the controlled corpus, runs identical cases through
B2 and B3, and writes `comparative_report.json` and `comparative_trials.csv` under
`results/experiments/live-b2-b3-*`. The research default is five repetitions; pass
`--repetitions 1` only for a lower-cost smoke test.

The feedback pipeline now records investigation linkage, agent/human confidence, reason codes,
useful and misleading evidence, outcome source, and validation provenance. New feedback is
`Pending` by default and is excluded from the curated dataset until a named validator marks it
`Validated`; it may instead be rejected or superseded. This prevents an unverified analyst click
from silently becoming model-training or calibration ground truth. The API exposes validation at
`POST /api/intelligence/feedback/{transactionId}/validation` and curated evaluation at
`GET /api/intelligence/model-evaluation/curated`. `EfOutcomeStore` and the provider-specific
`AddCuratedOutcomeMemory` migrations retain that provenance across restarts when a database is configured.

C#/.NET reference implementation of the trust and authorisation layer described in
`Trustworthy_Agentic_Payments_PhD_Standalone.docx.pdf`: agent identity, principal binding,
delegated financial authority, deterministic policy enforcement, evidence provenance, audit
reconstruction, and a human-approval workflow for autonomous financial agents.

## Consumer live-purchase pilot

The consumer pilot preserves a strict three-stage boundary:

```text
consumer task -> AgentPurchaseOrchestrator proposes PurchaseIntent
              -> deterministic mandate + TrustFramework evaluation
              -> HMAC-bound PurchaseAuthorisation -> commerce connector checkout
```

`AgentTrust.Consumer` owns consumer tasks, connected-service references and purchase execution
state. `AgentTrust.Commerce` contains typed product/basket/quote capabilities, canonical purchase
intents, the signed authorisation service, live-pilot gate, purchase scheduler and orchestrator.
`AgentTrust.Connectors` contains the controlled `DemoGroceryConnector`, an idempotent mock platform
payment processor and the official-SDK `StripePaymentAdapter`. None of these projects is referenced
by `AgentTrust.Intelligence`.

The connector verifies that the authorisation signature, intent hash, principal, agent, mandate,
merchant, amount, currency and expiry all match before checkout. Mutating the basket, delivery,
payment method, merchant or amount after approval invalidates checkout. Task occurrence identity is
`TaskId + ScheduledFor`; purchase/payment idempotency uses the stable derived purchase intent ID.
Provider timeouts become `Unknown`, and `RequiresAction` is retained for SCA/3DS rather than being
treated as success or bypassed.

The API exposes authenticated, owner-scoped routes under `/api/consumer` for tasks, tokenised
payment-method setup, runs, purchase history and one-off approve/reject. A host authentication
scheme must supply the authenticated principal as the `NameIdentifier` claim; the repository does
not include a development backdoor authentication scheme.

Safe defaults in `appsettings.json` keep mock payments enabled and live purchase disabled. Stripe
test mode requires environment configuration only:

```powershell
$env:STRIPE_SECRET_KEY = "sk_test_..."
$env:PURCHASE_AUTHORISATION_KEY = "<base64 encoded 32+ random bytes>"
```

```json
{
  "Payments": { "Provider": "Stripe", "Mode": "Test" },
  "LivePurchase": {
    "Enabled": false,
    "MaxPilotAmountGbp": 5,
    "AllowedPrincipalIds": [],
    "AllowedMerchantIds": [],
    "RequireExplicitLiveConfirmation": true
  }
}
```

Live mode additionally requires an `sk_live_` key, enabling the gate, principal and merchant
allow-list membership, an active owned mandate and tokenised payment method, amount at or below the
pilot cap, deterministic approval, and explicit confirmation. Stripe is used only for platform
merchant payment; an external merchant connector remains responsible for its own checkout.

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
  AgentTrust.Intelligence   Level 1/2 behavioural, anomaly, graph and risk analytics plus the
                            Level 3 FinancialInvestigationAgent, bounded tool catalogue,
                            explicit hypothesis/evidence state, stop controls, semantic memory
                            interfaces and structured advisory recommendations
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
                            every scenario in scenarios/ against its ground truth, plus Level 3
                            tool-selection, counter-analysis, boundary and state-persistence tests
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

### Stripe test purchase vertical slice

The first product demo exercises one guarded path end to end:

```text
weekly grocery task -> basket and quote -> deterministic trust decision
                    -> intent-bound purchase authorisation -> Stripe test PaymentIntent
                    -> receipt -> verified purchase audit trail
```

Put only rotated test credentials in the gitignored
`src/AgentTrust.Api/appsettings.Development.json`:

```json
{
  "Payments": { "Provider": "Stripe", "Mode": "Test" },
  "Stripe": {
    "PublishableKey": "pk_test_REPLACE_ME",
    "SecretKey": "sk_test_REPLACE_ME",
    "TestPaymentMethodId": "pm_card_visa"
  }
}
```

Then run:

```powershell
dotnet run --project src\AgentTrust.Runner -- --consumer-purchase-demo
```

The runner is test-mode only, explicitly confirms the pilot execution, allowlists the demo
principal and merchant, caps the purchase at GBP 5, and exits with an error unless the basket,
trust approval, Stripe PaymentIntent, receipt, and all required audit events are present. Never
put Stripe credentials in `appsettings.json`, source code, logs, or a commit.

```bash
dotnet build
dotnet test                                  # 155 tests: unit + persistence + API + scenarios + adversarial and comparative research tests
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

**`appsettings.Development.json`** (gitignored and dockerignored — this is where machine-specific
local config belongs, not plain `appsettings.json`, which is loaded in every environment
including inside containers):
```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=DESKTOP-3T0MF62\\MSSQLSERVER1;Database=AgentTrust;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
```
With this in place, `dotnet run --project src/AgentTrust.Api` just works, no environment
variable needed, and it never affects a container build.

**Environment variable** (overrides `appsettings.json`; use this for a different server, CI, or
a connection string that *does* carry a secret — e.g. SQL auth with a password — which must
never go into a committed file):
```powershell
$env:SQLSERVER_CONNECTION = "Server=YOUR-MACHINE\INSTANCE_NAME;Database=AgentTrust;Trusted_Connection=True;TrustServerCertificate=True;"
dotnet run --project src/AgentTrust.Api
```

The API creates the `AgentTrust` database and all tables on first run via EF Core migrations
(`Database.Migrate()` — see the EF Core migrations section below). Verified against the real local named instance
(Windows Authentication) both ways: registered a principal/agent/binding/authority, submitted a
transaction that escalated on the human-approval threshold, approved it via
`POST /api/approvals/{id}` (payment executed only at that point), and confirmed
`GET /api/audit/verify` reported a valid chain — including a run reading the config purely from
`appsettings.Development.json` with no environment variable set, confirming the file-based
config path works end-to-end.

**Config-leak bug found and fixed:** the SQL Server connection string was originally added to
the plain, committed `appsettings.json` (loaded in *every* environment, including containers)
rather than `appsettings.Development.json` (loaded only when `ASPNETCORE_ENVIRONMENT=Development`,
never in a container by default). This broke `docker compose up api` — the containerised API
tried to reach `DESKTOP-3T0MF62\MSSQLSERVER1`, a hostname meaningless inside the container
network, instead of the `POSTGRES_CONNECTION` docker-compose actually supplies. Separately,
`dotnet publish` copies every `appsettings*.json` file into the build output by default — so
even after moving the OpenAI key to `appsettings.Development.json`, it was still landing inside
the Docker image (`docker run --entrypoint cat ... appsettings.Development.json` on the built
image printed the key in plain text), regardless of which environment the container ran in.
Fixed both: moved the SQL Server string to `appsettings.Development.json` (machine-specific
config, not a secret, but still wrong for every other environment), and added `.dockerignore`
excluding `**/appsettings.Development.json` so no build context ever includes it. Rebuilt both
images from scratch (`--no-cache`) and confirmed the key is no longer present in either.

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

**`appsettings.Development.json`, for the API** (same idea as the SQL Server config above):
```json
{
  "OpenAI": {
    "ApiKey": "sk-...",
    "Model": "gpt-4o-mini"
  }
}
```
`Program.cs` reads `OpenAI:ApiKey`/`OpenAI:Model` via `AgentFactory.ConfiguredApiKey`/
`ConfiguredModel`, which take priority over the environment variables. Run with
`ASPNETCORE_ENVIRONMENT=Development` (the default for `dotnet run`) and it's picked up with no
environment variable needed — verified with a real `gpt-4o-mini` call in this session (see
below). `appsettings.Development.json` is gitignored specifically so this never gets committed.

**Never** put a real key in plain `appsettings.json` (that file is meant to be committed) or in
a scenario JSON file.

**Verified live in this session:** submitted a natural-language transaction request
(`POST /api/transactions/request` with `userInstruction` set, no `scriptedAgentResponse`) against
the real OpenAI API — the model produced a valid structured proposal in ~3.4s, phrased
differently from the scripted baseline (proving it wasn't the canned response), and the
transaction was approved and paid through the normal policy pipeline.

## Two scenario modes

- **Direct-injection** (`s01`-`s15`): the scenario supplies the `TransactionIntent` directly.
  Isolates **policy-engine correctness** from agent behaviour.
- **Agent mode** (`s16`-`s19`): the scenario supplies a natural-language `UserInstruction` +
  evidence; a real `SemanticKernelPaymentAgent` proposes the intent, which is validated
  (`AgentOutputValidator`) before it can reach the policy engine. Isolates
  **agent-intent-generation correctness** in addition to policy correctness. Each scenario also
  carries `ExpectedAgentOutputValid`, the ground truth the cross-model experiment uses to score
  intent-generation correctness independently of the eventual policy decision.

## Research Evaluation (Phase 1)

The core architecture is frozen as of this phase — no changes to `AgentTrust.Policy`,
`AgentTrust.Orchestration`, or the domain models below. This phase is about generating
experimental evidence at scale from that frozen core, not extending it.

```bash
dotnet run --project src/AgentTrust.Runner -- --research-eval --seed 42 --count 1000
```

Generates `count` scenarios (default 1000) from a seeded, deterministic generator
(`AgentTrust.Runner.Experiments.ScenarioGenerator`) spread evenly across 16 ground-truth
categories — legitimate, per-transaction limit violation, daily limit violation, merchant
violation, expired authority, revoked authority, revoked agent, evidence deficiency, prompt
injection, duplicate payment, authority-scope violation, human-approval-required, conflicting
instructions, price anomaly, credential attack, provider failure — runs every one through the
same `TrustFramework` used everywhere else in this repo, and writes to
`results/experiments/run_seed{seed}_n{count}_inmemory/`:

- `results.csv` — one row per scenario: category, expected vs. actual decision/reason
  code/payment status, evidence P/R/F1, policy and wall-clock latency, audit reconstructability
- `per_category.csv` — accuracy and evidence F1 broken down by category
- `confusion_matrix.csv` — expected decision × actual decision
- `summary.json` — every aggregate metric below, plus the confusion matrix and per-category
  table, in one file

### Running the same experiment against a real database

```powershell
$env:SQLSERVER_CONNECTION = "Server=YOUR-MACHINE\INSTANCE_NAME;Database=AgentTrustExperiments;Trusted_Connection=True;TrustServerCertificate=True;"
dotnet run --project src/AgentTrust.Runner -- --research-eval --seed 42 --count 1000
```

Setting `SQLSERVER_CONNECTION` (or passing `--sql-server` once it's set) routes the exact same
generated dataset through the real `Ef*Store` implementations from `AgentTrust.Data` instead of
the in-memory ones — same `ExperimentRunner`, same `TrustFramework`, same metrics code, only the
storage backend changes. Output goes to `run_seed{seed}_n{count}_sqlserver/` so it never
overwrites the in-memory run for the same seed/count.

**The database named in the connection string is dropped and recreated at the start of every
run** (`EnsureDeleted()` + `Migrate()`) — a reproducible experiment can't depend on
leftover rows from a previous run (re-running the same seed regenerates the same transaction
ids, which collided with stale data before this was added). Point `SQLSERVER_CONNECTION` at a
dedicated experiments database, never at one holding data you want to keep — use a different
connection string / database name for the API's own persistent data (see the SQL Server section
above).

**Verified in this session** (real local named SQL Server instance, Windows Authentication):
1,000 scenarios completed in ~163s (~163ms/scenario — each scenario is several round trips:
identity, binding, authority, plus per-transaction writes for intent, evidence, policy decision,
payment outcome, and audit record). Decision/reason-code/evidence correctness were identical to
the in-memory run (100%, for the same ground-truth reason explained below); latency was not —
median policy latency went from ~0ms in-memory to ~85ms against real SQL Server (P95 ~209ms,
P99 ~257ms). That gap is a legitimate, useful measurement in its own right: it characterises the
persistence layer's overhead under this exact workload, something the in-memory run cannot show.
**A real bug was caught by this run**: `EfApprovalStore.Save` threw `InvalidOperationException`
the first time a stale approval row (same `TransactionId`, different `ApprovalId`) already
existed in the target database — EF Core refuses to change a tracked entity's primary key via
`SetValues`. Fixed by deleting and re-adding the row instead of updating in place when the key
differs; the database-reset behaviour above also prevents this specific case from recurring, but
the store fix makes `EfApprovalStore` correct regardless.

**Reproducibility:** the same seed always generates the same scenarios with the same expected
and actual outcomes — verified in this session by diffing two `--seed 42 --count 1000` in-memory
runs: every column was byte-identical except `wall_latency_ms` (timing noise, expected). There's
a regression test (`ResearchEvaluationTests.SameSeedProducesIdenticalDecisionsAndLabels`) that
checks this on every build.

**Metrics produced:** Policy Enforcement Accuracy, Unauthorized Transaction Prevention Rate,
Authorized Transaction Acceptance Rate, Revocation Enforcement Rate, Human Escalation Accuracy,
Reason-Code Accuracy, Evidence Precision/Recall/F1, Audit Reconstruction Rate + whole-chain
validity, median/P95/P99 latency. Plus an adversarial-subset breakdown (Priority 4 from the
brief) computed for free from the same dataset — Attack Success/Prevention Rate and False
Positive/Negative Rate over the prompt-injection, duplicate-payment, credential-attack, and
authority-scope-violation categories.

**Read this before citing the numbers in a results chapter:** at 1,000 scenarios, every headline
metric currently comes out at 100%. That is not a bug and not inflated — it is the expected
result of what this experiment actually tests. The ground truth for each generated scenario is
derived directly from the same `PolicyEngine` logic being measured (e.g. a `TransactionLimitViolation`
scenario is defined as "amount > PerTransactionLimit, expect Deny/TRANSACTION_LIMIT_EXCEEDED"
because that is what the code does), the same pattern the hand-authored `s01`-`s15` scenarios
already used at small scale. So this experiment answers **"does the deterministic implementation
match its own specification consistently at scale, with no regressions across categories"** — a
real and necessary claim for a trust-and-audit system, but not the same claim as "the policy
design correctly captures real-world intent," which is what the hand-authored scenarios (mapped
to the concept document's narrative) and human review of the category definitions establish
instead. The place genuine, non-trivial uncertainty enters this pipeline is the agent layer: the
`--cross-model` harness (real vs. scripted LLMs) already shows accuracy below 100% because a real
model doesn't always reproduce the intended behaviour. Extending the generator to drive scenarios
through a real agent instead of direct injection — at cost, since that means 1,000+ live API
calls — is the natural next step if non-trivial accuracy figures are needed for a results chapter;
ask before running that at scale.

**Not done in this phase** (flagged in the original brief as "eventually"/genuinely separate
scope, and left alone to respect the freeze):
- **Real multi-model comparison** (Model A/B/C/D). The mechanism exists (`AgentFactory`,
  `IPaymentAgent`, `ModelProfile`) and already runs a real `live:gpt-4o-mini` profile in
  `--cross-model`; a second/third/fourth *distinct* live model needs additional provider
  credentials this environment doesn't have, and running any of this at 1,000-scenario scale is
  a real, non-trivial API cost that should be an explicit decision, not a default.
- **Merchant registry wiring.** `IMerchantStore`/`Merchant` still exist unused by `PolicyEngine`,
  which still checks `DelegatedAuthority.ApprovedMerchants` directly, exactly as before. Real
  work, but it's a `PolicyEngine` change — out of scope while the core is frozen.

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
- 19 hand-authored ground-truth scenarios (15 direct-injection + 4 agent-mode), 60 automated
  tests across policy correctness, agent validation, persistence, approvals, audit tampering,
  research-evaluation reproducibility, and API integration
- **Research Evaluation Phase 1** (`--research-eval`): seeded generator + evaluation pipeline
  producing 16-category, 1,000+ scenario labelled datasets with full aggregate metrics,
  confusion matrix, per-category breakdown, and adversarial-subset metrics — publication-ready
  CSV/JSON, reproducible from a seed (see the dedicated section above)

## Financial Intelligence Layer (AgentTrust.Intelligence)

A new upstream layer implementing the long-term product vision (`C:\Payment Research\new work
flow to use to build.txt`): "the AI may have an opinion, the infrastructure has authority." It
produces a structured, evidence-backed `RiskAssessment` — never a decision. The frozen
`TrustFramework`/`PolicyEngine` used everywhere else in this repo is completely unmodified;
`Intelligence` only ever hands it evidence to reason over independently.

```bash
dotnet run --project src/AgentTrust.Runner -- --intelligence-demo
```

Reproduces the vision doc's worked example end-to-end: a customer behaviour profile built from
40 varied prior transactions (typical amount range, devices, locations, beneficiaries, time
window), then a 03:41 transaction for £8,700 to a brand-new beneficiary from a new device and
location — flagged with 7 concrete risk factors (new device, new beneficiary, beneficiary added
2 minutes earlier, unusual time, unusual location, prior failed attempts, amount anomaly),
risk score 100/100, recommendation ESCALATE. That evidence is then handed to the trust layer,
which independently escalates on its own hard human-approval threshold — the policy engine never
sees the risk score at all, only the evidence.

**Modules** (`src/AgentTrust.Intelligence/`):
- `Behaviour/` — `TransactionEvent` (the richer raw event the intelligence layer reasons over,
  distinct from the trust layer's narrow `TransactionIntent`), `CustomerBehaviourProfile`,
  `MerchantBehaviourProfile`, `BehaviourProfileBuilder` (percentile-based, so one outlier can't
  permanently widen "normal"), `BehaviourDeviationService` (flags a merchant's material shift —
  the doc's 150→4,300 tx/day surge-fraud example)
- `Anomaly/` — `TransactionAnomalyDetector` (contextual: new device/beneficiary/location/time,
  a beneficiary added minutes ago, prior failed attempts), `VelocityDetector` (transaction
  count/value in a trailing window), `AmountAnomalyDetector` (deviation from the customer's
  typical range, graded by how far outside it falls)
- `Risk/` — `TransactionRiskEngine` (aggregates every detector's factors into one `RiskAssessment`
  — score, confidence, advisory recommendation, evidence), matching the doc's JSON shape exactly
- `Investigation/` — `InvestigationAgent` (deterministic reasoning-loop orchestrator: fetch
  history → build profile → detect → assess → collect evidence), `EvidenceCollector` (turns risk
  factors into `AgentTrust.Core.Models.EvidenceItem`s — the same type the trust layer already
  consumes, no translation needed), `InvestigationTools` (the same building blocks exposed as
  Semantic Kernel `[KernelFunction]`s — the seam for a real LLM-driven investigation agent later,
  mirroring how `SemanticKernelPaymentAgent` already works for payment intents; not used by
  default, since `InvestigationAgent` is the free, reproducible, no-LLM-cost equivalent used by
  every test)

**Tests** (`tests/AgentTrust.Tests/Intelligence/`): behaviour-profile construction and
merchant-deviation detection against the doc's own worked numbers, each detector individually,
the risk engine end-to-end on both the night-time scenario and an ordinary transaction, and
`IntelligenceTrustLayerIntegrationTests` — the one that matters most: proves intelligence
evidence flowing into the *actual*, unmodified `TrustFramework` produces the correct escalation,
with the AI's evidence traceable in the resulting audit record.

## Consumer Financial Mandates (Phase 2: PaymentMethods, Mandates, Tasks, Scheduling)

Four new projects implementing the vision doc's Phase 2 (sections 11-17): tokenised payment
methods, the Financial Mandate concept, recurring tasks, and scheduling — culminating in the
doc's own worked example, reproduced exactly.

```bash
dotnet run --project src/AgentTrust.Runner -- --mandate-demo
```

Connects a card (tokenised — see below), creates a Financial Mandate ("book an Uber for my
girlfriend every Monday at 07:30, spend up to £25, ask me first above that"), then runs all
three of the doc's worked scenarios: a legitimate £18.70 ride (approved and paid), a £31.40
surge-priced ride (escalates, then a human approves *that specific ride* without raising the
mandate's own £25 limit for any future one), and a £22 ride — within the limit — to a different
pickup/destination/recipient (escalates anyway, because the amount was never what was wrong).
Every authorisation decision in all three is made by the same, unmodified `TrustFramework`.

**`AgentTrust.PaymentMethods`** — `PaymentMethod` (token + display metadata only — `CardBrand`,
`Last4`, `ExpiryMonth/Year` — no field capable of holding a PAN or CVV, checked by a test that
reflects over the type), `ICardTokenizationProvider`/`MockCardTokenizationProvider` (raw card
number and CVV exist only as parameters to one call and are never returned, logged, or stored),
`PaymentMethodService` (the doc's "connect card" flow).

**`AgentTrust.Mandates`** — `FinancialMandate` (answers "how may money be used for *this task*",
narrower than and layered on top of `DelegatedAuthority`, which answers "what can the agent do at
all"), `MandateToAuthorityMapper` (converts a mandate into the frozen core's `DelegatedAuthority`
so the *existing* `PolicyEngine` does the actual amount/merchant authorisation — the mandate layer
never reimplements it), `IMandateUsageTracker` (weekly/monthly cumulative spend — a concept the
frozen `DelegatedAuthority` doesn't have, so it's tracked here rather than by extending the frozen
core), `MandateEvaluationService` (checks what a policy engine structurally cannot: does this
task's context — route, recipient, whatever `TaskParameters` the mandate carries — match what was
authorised; implements the doc's "context can override apparent normality" scenario).

**`AgentTrust.Tasks`** — `AgentTask`, and `TaskExecutionOrchestrator`, the piece that makes the
one-off-approval guarantee real: `TrustFramework.ProcessTransaction` executes payment immediately
on Approve, so nothing is sent to it until a human has actually resolved an escalation. On
context-mismatch or above-limit-with-`RequireApproval`, the orchestrator holds the execution
pending rather than calling the trust layer speculatively; on approval, it grants a
*single-call-scoped* elevated authority (via `MandateToAuthorityMapper`'s one-off-amount
parameter), makes exactly one `ProcessTransaction` call, then immediately re-grants the mandate's
normal authority — verified by a test that a second ride the following week is still capped at
the original limit.

**`AgentTrust.Scheduling`** — `RecurringSchedule` (day-of-week + time, with tolerance — "every
Monday at 07:30"), `ScheduledTaskRunner` (checks every active task, and for the ones that are due,
requests a live price quote via `IPriceQuoteProvider` and executes through the orchestrator — the
doc's "07:20 → scheduled task activates → agent requests current price → ..." flow).

**Tests** (`tests/AgentTrust.Tests/Mandates/`, 11 tests): payment-method tokenisation and
expiry/revocation, and `UberMandateScenarioTests` — the three worked scenarios above, plus a
rejected-escalation case and an expired-mandate hard-block case, and a scheduling test proving
only due tasks execute.

**A real bug found while building this:** `MandateToAuthorityMapper` originally mapped a
mandate's `WeeklyLimit` onto the trust layer's `DailyLimit` field. That worked for the legitimate
scenario, but broke the surge-approval scenario — the one-off elevated per-transaction amount
(£31.40) was still checked against `DailyLimit` mapped from `WeeklyLimit` (£25), so the trust
layer denied a transaction the mandate layer had just approved. Weekly-cap enforcement already
happens correctly in `MandateEvaluationService` before anything reaches the trust layer; mapping
it onto `DailyLimit` too created a second, independent, conflicting cap. Fixed by mapping
`DailyLimit` to the same effective per-transaction limit instead, so it never competes with
weekly enforcement.

## Advanced Financial Intelligence (Phase 3: graph, richer risk engines, learning)

Extends `AgentTrust.Intelligence` (no new projects — these are new modules inside it) with every
item from the vision doc's Phase 3 list: graph/device/beneficiary/merchant intelligence,
behavioural-change detection, peer-group comparison, long-term memory, multi-step investigation,
feedback learning, stronger (multi-engine) risk models, and an additional specialist agent.

```bash
dotnet run --project src/AgentTrust.Runner -- --intelligence-phase3-demo
```

Reproduces the doc's own merchant fraud-ring example (sections 6-7) end-to-end: a merchant
shifting from 150 tx/day at £22 average and 2% refunds to 90 tx/day at £480 average, 17%
refunds, 90 customer accounts collapsing to 8 devices and 3 IPs, all settling to one account —
scored 100/100 by `MerchantInvestigationAgent`, which runs behavioural-change detection and
graph community-risk analysis together. Then a multi-step investigation on a transaction whose
initial risk score alone is ambiguous (22/100): the planner recognises the ambiguity, digs into
the relationship graph, discovers the device is shared across several other customer accounts,
and raises the final score to 59/100 (ESCALATE) — a decision the single-pass Phase 1
`InvestigationAgent` had no way to reach on its own. Finally, a feedback/model-evaluation pass
scores the AI's past recommendations against recorded real-world outcomes.

**`Graph/`** — `FinancialGraph` (typed nodes: Customer/Device/Merchant/Beneficiary/Account/
SettlementAccount/IpAddress; weighted, queryable edges), `RelationshipAnalyzer` (builds the graph
from raw `TransactionEvent`s; finds devices shared across a merchant's customers),
`CommunityRiskAnalyzer` (the doc's exact fraud-ring shape: many customer accounts collapsing to
few devices/IPs, all funnelling to one settlement account — invisible to any single transaction).

**`Risk/`** — the doc's remaining three risk engines beyond Phase 1's `TransactionRiskEngine`:
`MerchantRiskEngine` (own-history shift + graph community risk + optional peer comparison),
`DeviceRiskEngine` (device intelligence — a device shared across many otherwise-unrelated
customers), `CustomerRiskEngine` (behavioural-change-over-time + device-sharing risk, a
longer-horizon view distinct from scoring one transaction). All share a common
`EntityRiskAssessment`/`RiskFactor` currency with the Phase 1 engine.

**`Behaviour/`** additions — `BehaviourDeviationService.CompareCustomerProfiles` (behavioural
change detection: "behaviour should also change over time" — compares two snapshots of the
*same* customer, not one fixed lifetime baseline), `IProfileHistoryStore`/
`InMemoryProfileHistoryStore` (long-term memory: periodic profile snapshots over time, shaped so
a real deployment could back it with the same kind of EF-Core store already used for the trust
layer), `PeerGroupComparator` (cross-sectional comparison against similar entities — a merchant
can be consistent with its own history and still be a peer-group outlier).

**`Investigation/`** additions — `InvestigationPlanner` (the doc's full reasoning loop: only
reaches for a graph tool when the initial score is genuinely ambiguous, cheap/clear cases don't
pay for extra steps — a real multi-step investigation, not just a longer single pass),
`MerchantInvestigationAgent` (the "additional specialist agent": a merchant-focused investigator
distinct from the customer/transaction-focused Phase 1 `InvestigationAgent`).

**`Learning/`** — `DecisionFeedback`/`IOutcomeStore` (records what actually happened for a past
recommendation — "Agent: ESCALATE. Human: Legitimate. Store it."), `ModelEvaluation` (precision/
recall/F1/accuracy of the AI's ESCALATE calls against recorded real-world outcomes — how you'd
actually know whether the intelligence layer is getting better or worse over time).

**Tests** (in `tests/AgentTrust.Tests/Intelligence/`): the fraud-ring graph pattern with
the doc's exact numbers and a negative control (an ordinary merchant with no collapse), all three
new risk engines individually and combined, the ambiguous-vs-clear multi-step investigation
branch, the full merchant-investigation reproduction, long-term-memory-backed behavioural-change
detection, and model evaluation's precision/recall/F1/accuracy arithmetic checked against a
hand-computed 5-case confusion matrix. The current complete suite contains 111 passing tests.

These Level 1 and Level 2 capabilities now act as tools and evaluation baselines for the Level 3
agentic investigation objective rather than being treated as the final intelligence architecture.

## Intelligence wired into AgentTrust.Api

`AgentTrust.Intelligence` is now reachable over HTTP, both as its own endpoints and inline in the
main transaction flow — in every case advisory only; nothing here can authorise or block a
payment by itself, only the unmodified `TrustFramework` can.

**Dedicated endpoints** (`IntelligenceController`):
- `POST /api/intelligence/events` — record a raw `TransactionEvent` into history (the material
  every profile and investigation below is built from)
- `GET /api/intelligence/customers/{customerId}/profile` — the customer's behaviour profile,
  built live from recorded history
- `POST /api/intelligence/investigate` — runs `InvestigationPlanner` on a candidate transaction:
  single-pass unless the initial score is genuinely ambiguous, in which case it digs into the
  relationship graph built from the merchant's recorded history first
- `POST /api/intelligence/merchants/{merchantId}/investigate` — `MerchantInvestigationAgent`,
  splitting recorded history at a cutoff date into baseline/recent windows
- `POST /api/intelligence/feedback` / `GET /api/intelligence/model-evaluation` — the
  `DecisionFeedback`/`ModelEvaluation` feedback loop

**Inline in the transaction flow**: `POST /api/transactions/request` now accepts an optional
`candidateEvent` field. When set, the intelligence layer investigates it first (building on
whatever history was recorded via the events endpoint) and its evidence is merged into the same
`EvidenceManifest` the trust layer evaluates — the doc's Financial Intelligence Layer -> proposed
action -> Trust & Authorisation Layer flow, in one HTTP call. The response reports both decisions
side by side under an `intelligence` field, and they are allowed to disagree: verified in this
session with a brand-new customer (no history to judge against) where Intelligence recommended
`Approve` (its only finding was "no established behaviour profile") while the trust layer
independently escalated on its own £1,000 human-approval threshold — proof neither layer defers
to the other; a low-information "everything looks fine" from the AI never overrides a hard
control.

## Graph and profile history persisted via AgentTrust.Data

`ITransactionEventStore` (the source data `FinancialGraph` and every behaviour profile are
rebuilt on demand from — neither is itself a stored shape) and `IProfileHistoryStore` (long-term
profile snapshots for behavioural-change detection) are now backed by EF Core when a database is
configured, exactly like every trust-layer store:

- **`EfTransactionEventStore`** / **`EfProfileHistoryStore`** (`AgentTrust.Data`) — new
  `TransactionEventEntity`/`ProfileSnapshotEntity` tables on the same `AgentTrustDbContext` used
  everywhere else. Query filtering happens in the database (`WHERE CustomerId = ...`); ordering
  and closest-snapshot lookup happen after materialising rows into memory — the same lesson from
  `EfTransactionLedger`: SQL Server and SQLite each reject a different `DateTimeOffset`
  expression shape in-query, so anything beyond simple equality filters is done client-side.
- Falls back to the existing `InMemoryTransactionEventStore`/`InMemoryProfileHistoryStore` when
  no database is configured, same as every other store in this repo.
- `InvestigationAgent`/`InvestigationPlanner` (which depend on `ITransactionEventStore`) are now
  registered `Scoped` rather than `Singleton` — required once that store can be EF-backed
  (Scoped, tied to the request's `DbContext`): a Singleton can never safely capture a Scoped
  dependency. `TransactionRiskEngine`/`DeviceRiskEngine`/`MerchantRiskEngine` take all their input
  as method parameters rather than injected stores, so they stay Singleton.
- **`IProfileHistoryStore` is now actually wired into the API**, not just persisted: two new
  endpoints, `POST /api/intelligence/customers/{id}/profile/snapshot` (takes and stores a
  snapshot of the customer's current profile) and
  `GET /api/intelligence/customers/{id}/behavioural-change` (compares the current profile against
  the closest stored snapshot via `BehaviourDeviationService.CompareCustomerProfiles`, 404s if no
  snapshot has ever been recorded).

**A real bug found and fixed getting this working**: `ApiIntegrationTests` assumed no database was
configured for its `WebApplicationFactory` because no `POSTGRES_CONNECTION` environment variable
was set — but `WebApplicationFactory` defaults to the `"Development"` environment, which loads
`appsettings.Development.json`. On this machine that file already held a real
`ConnectionStrings:SqlServer` value (see the SQL Server section above), so the moment the new EF
stores existed, the whole test class started silently hitting a *real* database instead of the
in-memory fallback the tests assumed. It happened to still work for every pre-existing table
(created earlier in this same database), but `TransactionEvents`/`ProfileSnapshots` didn't exist
yet — `Database.EnsureCreated()` only creates a schema for a database that doesn't exist yet, it
never retroactively adds tables to one that does — so the two new intelligence tests failed with
500s. Fixed by making `ApiIntegrationTests` explicitly force `UseEnvironment("Testing")` and blank
both connection-string configuration keys, so the test class is hermetic regardless of what's
configured on the machine running it.

**Verified against the real local SQL Server instance** (fresh throwaway database, since the
existing `AgentTrust` database predates these tables and `EnsureCreated()` won't add them
retroactively): recorded history and a profile snapshot, then killed the API process entirely and
started a brand-new one against the same database — the customer's profile and the behavioural-
change comparison both came back correctly from the new process, proving this is genuine
persistence, not just a passing test.

**Tests** (7 new: 3 in `ApiIntegrationTests.cs` for the HTTP surface via `WebApplicationFactory`,
4 in `IntelligencePersistenceTests.cs` for the EF mapping against real SQLite, matching the
existing `PersistenceTests.cs` pattern): recording history then investigating a night-time
anomaly over HTTP, the combined transaction+intelligence flow proving the two decisions are
independent, the feedback/model-evaluation round trip, transaction-event round-trip and
upsert-by-id, profile-snapshot round-trip and closest-snapshot lookup, and behavioural-change
detection against a persisted (not in-memory) baseline. **111/111 tests passing across the whole
repo.**

**Not yet done:** replacing the deterministic `InvestigationAgent`/`InvestigationPlanner` with a
genuinely LLM-orchestrated one via `InvestigationTools` (the Semantic Kernel plugin seam already
built in Phase 1).

## EF Core migrations

Real migrations now exist, replacing `Database.EnsureCreated()` — the exact gap that caused the
bug described above (new tables never appearing in an already-created database) is what
migrations are for.

**Two migrations projects, not one** — `AgentTrust.Data.Migrations.SqlServer` and
`AgentTrust.Data.Migrations.Postgres`, each with their own `IDesignTimeDbContextFactory` and
their own `Migrations/` folder for the same `AgentTrustDbContext`. This is deliberate, not
incidental: a migration bakes in provider-specific SQL (column types, etc.) at generation time —
a SQL Server migration file literally contains `type: "nvarchar(max)"` in its `CreateTable` call,
which Postgres's SQL generator cannot execute. Mixing both providers' migrations into one
migrations assembly for the same context would apply the wrong dialect's DDL the moment you ran
against the other provider. `Program.cs` (and the Runner's `--research-eval --sql-server` path)
select the matching assembly via `x.MigrationsAssembly("AgentTrust.Data.Migrations.SqlServer")` /
`"...Postgres"` when configuring `UseSqlServer`/`UseNpgsql`.

```bash
# regenerate after changing AgentTrust.Data's entities/DbContext
cd src/AgentTrust.Data.Migrations.SqlServer && dotnet ef migrations add <Name> --context AgentTrustDbContext
cd src/AgentTrust.Data.Migrations.Postgres  && dotnet ef migrations add <Name> --context AgentTrustDbContext
```

`Program.cs` and the Runner both now call `Database.Migrate()` instead of `EnsureCreated()`.
Verified against a fresh SQL Server database in this session: `Migrate()` created every table,
the `__EFMigrationsHistory` tracking table, and recorded `InitialCreate` as applied — then a
normal register → record intelligence event → fetch profile flow worked end-to-end against that
migrated schema.

**If you're switching an existing `EnsureCreated()`-based database over to migrations** (e.g. the
`AgentTrust` database used earlier in this session, before migrations existed): `Migrate()` will
try to run `InitialCreate` from scratch, including `CREATE TABLE` for tables that already exist,
and fail. This is a one-time migration-adoption step, not a bug in the migrations themselves —
either drop and recreate that database (simplest, appropriate if it only ever held test/demo
data — this was **not** done automatically; ask if you want it done), or baseline it by manually
creating `__EFMigrationsHistory` and inserting a row marking `InitialCreate` as already applied
without running its SQL:
```sql
CREATE TABLE [__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL, [ProductVersion] nvarchar(32) NOT NULL, CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId]));
INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260902103106_InitialCreate', N'9.0.9');
```
After that, `Migrate()` sees `InitialCreate` as already applied and only runs future migrations.

## Swagger weekly grocery purchase

In Development, `POST /api/development/token` issues a one-hour, locally signed JWT for Swagger.
The endpoint returns 404 outside Development and startup rejects development-token mode in every
other environment. Submit a stable subject such as:

```json
{ "subject": "local-consumer@example.test", "displayName": "Local consumer" }
```

Copy `accessToken`, open `http://localhost:5104/swagger/index.html`, select **Authorize**, and paste
the token without a `Bearer` prefix. Then use this sequence:

1. `POST /api/consumer/agents`

   ```json
   { "agentId": "weekly-grocery-agent", "displayName": "Weekly grocery agent" }
   ```

2. `POST /api/consumer/payment-methods/setup` with a Stripe test PaymentMethod token created by
   Stripe's test tooling (never a PAN or CVV).

   ```json
   { "provider": "Stripe", "providerToken": "pm_card_visa", "cardBrand": "visa", "last4": "4242", "expiryMonth": 12, "expiryYear": 2035 }
   ```

3. `POST /api/consumer/mandates`, substituting the returned payment-method ID.

   ```json
   { "agentId": "weekly-grocery-agent", "merchantIds": ["demo-grocery"], "paymentMethodId": "pm_REPLACE", "currency": "GBP", "perTransactionLimit": 70.00, "weeklyLimit": 70.00, "humanApprovalThreshold": 70.00, "validFrom": "2026-09-03T00:00:00Z", "validUntil": "2027-09-03T00:00:00Z" }
   ```

4. `POST /api/consumer/tasks`, substituting mandate and payment-method IDs.

   ```json
   { "instruction": "Buy my weekly groceries every Sunday up to £70.", "merchantId": "demo-grocery", "mandateId": "mandate_REPLACE", "paymentMethodId": "pm_REPLACE", "currency": "GBP", "maximumAmount": 70.00, "timezone": "Europe/London", "schedule": { "frequency": "Weekly", "dayOfWeek": "Sunday", "localTime": "10:00" }, "shoppingList": [{ "query": "milk", "quantity": 2 }, { "query": "bread", "quantity": 1 }, { "query": "eggs", "quantity": 1 }, { "query": "bananas", "quantity": 1 }], "substitutionPolicy": { "allowed": true, "maximumAdditionalAmount": 5.00 }, "deliveryAddressReference": "local-test-address" }
   ```

5. `POST /api/consumer/tasks/{taskId}/run` with `{}`. Repeating the same task occurrence returns
   the existing execution and does not submit another payment.
6. Read the execution through `GET /api/consumer/purchases/{purchaseId}`. For an escalation, call
   the step-up protected approve/reject endpoint for that exact purchase.
7. Keep `stripe listen --forward-to http://localhost:5104/api/payments/stripe/webhook` running.
   A verified success webhook commits the reservation and creates at most one receipt.
8. Read `GET /api/consumer/purchases/{purchaseId}/receipt` and
   `GET /api/consumer/purchases/{purchaseId}/audit`.

All ownership comes from the authenticated `agenttrust_principal_id` claim. Consumer request DTOs
do not accept a principal/owner ID. Production must configure a real OIDC authority and audience
and leave `Authentication:Development:Enabled` disabled.

## Known limitations

- Store interfaces (`IAgentRegistry`, etc.) are synchronous; the EF-Core implementations call
  EF Core's sync APIs rather than being async end-to-end. Fine for this prototype's throughput,
  worth revisiting before any real load.
- Consumer endpoints use JWT bearer validation against a configured OIDC authority/audience.
  A validated `(iss, sub)` is linked to a durable ASP.NET Core Identity user and mapped to the
  platform's stable `PrincipalId`; unlinked identities fail the `Consumer` policy. Set
  `Authentication:AutoProvisionUsers=true` only when controlled just-in-time provisioning is
  intended. Production startup fails if the OIDC authority or audience is absent.
- High-risk consumer endpoints use the `StepUp` policy. It accepts only signed token `amr=mfa`
  or a configured `acr` value together with a recent `auth_time`; request-body booleans cannot
  satisfy step-up. Configure `Authentication:StepUp:MaxAgeMinutes` and
  `Authentication:StepUp:AllowedAcrValues` for the selected identity provider.
- Consumer durable-state, Identity, mandate and tokenised-payment-method schemas have
  provider-specific SQL Server/PostgreSQL migrations. When a connection string is configured,
  the consumer runtime uses EF stores, database-unique occurrence/payment keys, durable checkout
  records, signed Stripe webhooks, and an opt-in scheduler/recovery/reconciliation worker. Set
  `Stripe:WebhookSecret` from the Stripe CLI/Dashboard and explicitly enable
  `ConsumerPilot:Worker:Enabled` on only the intended worker deployment. The worker is off by
  default. Mandate creation, task authority binding, payment-method setup, live pilot execution,
  and approval require recent trusted step-up evidence; receipts and purchase audit are
  principal-owned reads.
- The cross-model harness's "degraded" profile is a hand-authored scripted variant, not a
  second real model — meaningful multi-model comparison needs a second live connector
  (Azure OpenAI, Anthropic via an OpenAI-compatible shim, etc.) configured through
  `AgentFactory`.
- No merchant-registry enforcement inside the policy engine yet — `IMerchantStore`/`Merchant`
  exist and are exposed for future use, but `PolicyEngine` still checks `DelegatedAuthority.
  ApprovedMerchants` (a name list on the authority itself), not the merchant registry.
