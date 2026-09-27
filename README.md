# AgentTrust

AgentTrust is a goal-driven autonomous commerce platform for retail and restaurant purchases. A customer describes an outcome in ordinary language; specialised agents discover options, obtain authoritative provider quotes, adapt when conditions change, and execute only after deterministic financial controls approve the exact action.

> **The agent can think, adapt, and act—but it cannot exceed the authority the customer granted.**

Examples:

> “I have £10. Buy two loaves of wholemeal bread.”

> “Buy the best-value ingredients for chicken wraps under £18.”

> “Place my usual grocery order every Friday. Use a safe substitute if something is unavailable.”

## What AgentTrust does

- Accepts natural-language purchase goals through a chat-first API and responsive PWA.
- Searches registered retailers or restaurants through provider capability contracts.
- Builds and compares proposals using authoritative prices, availability, fees, and quote expiry.
- Supports principal-scoped auto-proceed for safe, fully verified purchases.
- Runs recurring orders with stable occurrence identities and duplicate-execution protection.
- Replans after stock or price changes and applies permitted substitutions.
- Persists payment attempts, fulfilment evidence, receipts, audit events, and goal outcomes.
- Replays existing transactions without creating another payment or receipt.
- Learns principal-scoped preferences and corrections with provenance, confidence, and expiry.

## How it works

```text
Customer goal
    ↓
Analyst
    ↓
Planner ←────────────── revision ──────────────┐
    ↓                                          │
Auditor ───────────────────────────────────────┘
    ↓ accepted and hash-bound
Durable OODA cycle
    ├─ Observe provider state
    ├─ Orient using goal, constraints, and memory
    ├─ Decide between verified alternatives
    ├─ Act through a registered capability
    ├─ Prove the goal remains achievable
    └─ Check the authoritative outcome
    ↓
Deterministic trust boundary
    ↓
Operator
    ↓
Payment → Fulfilment → Receipt → Goal proof
```

The reasoning layer never receives card credentials and cannot authorise itself. It receives safe capability summaries and evidence. Application code owns identity, authority, budgets, payment, idempotency, recovery, and final execution.

## Four specialised workers

- **Analyst:** interprets the objective, budget, household needs, inventory, allergies, dietary requirements, preferences, and execution intent. It cannot build a basket or pay.
- **Planner:** uses discovery, details, availability, comparison, request-building, and quotation capabilities. It can revise a proposal but cannot execute it.
- **Auditor:** independently accepts, rejects, or requests revision. It cannot modify or execute a proposal. The accepted proposal is cryptographically bound to the final proposal and authoritative quote.
- **Operator:** executes only an Auditor-accepted proposal that passes deterministic controls. It cannot change the basket, expand authority, or bypass a failed check.

## Automatic safe execution

Customers can store standing conversational consent:

```http
PUT /api/consumer/conversation-policy
Authorization: Bearer <token>
Content-Type: application/json

{
  "autoProceedWhenSafe": true,
  "askBeforeSubstitutions": false,
  "showBasketBeforePayment": false
}
```

Read it with `GET /api/consumer/conversation-policy`.

With auto-proceed enabled, a ready proposal can execute without another “Shall I go ahead?”. Conversational consent is not financial authority: clarification, audit, budget, mandate, payment ownership, authentication, allergy, idempotency, and live-execution controls still apply. Set `autoProceedWhenSafe` to `false` or `showBasketBeforePayment` to `true` to require review.

## Recurring autonomous orders

A scheduled order such as “buy my usual groceries every Friday” is not a blind replay. Each occurrence observes current availability and prices, evaluates allowed alternatives, refreshes the quote, and proves that the resulting order still satisfies the goal.

The occurrence identity is `(taskId, scheduledFor)`. Competing workers and repeated calls converge on one execution and payment key. Optional missing products may be omitted when the outcome remains viable. Required items may be substituted only within policy. If no safe, goal-preserving alternative exists, the system records the unmet goal and requests intervention instead of making a materially incorrect purchase.

## Deterministic financial controls

Before execution, application code verifies:

- authenticated identity and recent step-up evidence where required;
- active agent-to-principal binding;
- active, unexpired, versioned mandate;
- merchant, category, currency, time-window, and task scope;
- per-transaction and rolling spending limits;
- customer budget and authoritative quote validity;
- owned, active payment method;
- product availability and constraint compliance;
- independent audit acceptance and proposal-hash integrity;
- reservation, occurrence, payment, and receipt idempotency.

These controls remain authoritative even when a model requests execution.

## Durable OODA and transaction lifecycle

`CommerceOodaCycles` stores each durable cycle. Append-only `CommerceOodaSteps` record observations, orientation, decisions, actions, proof, and checks. A completed goal is linked to the authorised proposal, payment outcome, provider fulfilment evidence, and receipt.

Immediate payment success, asynchronous success, timeout, retry, webhook processing, and reconciliation converge on the same purchase and idempotency key. Replaying a completed occurrence returns its existing execution, intent, authorisation, and receipt.

## Memory and personalisation

SQL is the source of truth for customer memory, provenance, consent, confidence, expiry, deletion, and retrieval audits. Optional Qdrant retrieval filters by `PrincipalId`, with returned ownership checked against SQL. Redis provides a short-TTL cache; an outbox makes SQL and the vector index converge safely.

Memories can include preferred products, accepted or rejected substitutions, dietary and accessibility preferences, household information, and corrections. Customers can export or delete them. Qdrant and Redis are optional; the core API and deterministic memory record work without them.

## Provider integration

AgentTrust separates reusable domain reasoning from provider execution. Retailers and restaurants implement only the capabilities they support:

```text
Discovery      search, get_details, availability
Configuration  select_options, build_request, update_request
Quote          get_quote, refresh_quote
Reservation    reserve, hold, release
Execution      purchase, place_order, confirm
Lifecycle      status, amend, cancel, refund
Evidence       receipt, confirmation, provider_reference
```

Connectors supply authoritative inventory, menu, pricing, delivery, reservation, and execution behaviour. Grocery-domain meal expansion, nutrition, allergy, and substitution semantics are reusable across compatible retailers.

Provider-specific identifiers remain inside the connector boundary; shared agent reasoning operates on normalized customer goals and provider capabilities rather than hard-coded merchant logic.

## Primary API

```text
POST /api/development/users                         Development/E2E only
POST /api/development/token                         Development/E2E only

POST /api/consumer/agents
GET  /api/consumer/setup/status
POST /api/consumer/payment-methods/setup
POST /api/consumer/mandates

GET  /api/consumer/conversation-policy
PUT  /api/consumer/conversation-policy
POST /api/consumer/purchases/request                text/plain

POST /api/consumer/tasks
POST /api/consumer/tasks/{taskId}/run
GET  /api/consumer/tasks/{taskId}/ooda-cycles

GET  /api/consumer/purchases/{purchaseId}
GET  /api/consumer/purchases/{purchaseId}/receipt
GET  /api/consumer/purchases/{purchaseId}/audit
```

Ownership always comes from the authenticated principal claim; request bodies cannot choose another owner. Development users and local tokens return 404 outside Development/E2E and must be disabled in production. Swagger is available in Development at `http://localhost:5104/swagger/index.html`.

## Verified evidence

The implementation has been exercised through a real `AgentTrust.Api` process—not only an in-process test host—using JWT/MFA, a freshly migrated SQL Server database, real HTTP requests, and mock payments. The latest black-box journey proved:

- customer agent, payment-method, and mandate setup;
- persistent auto-proceed configuration;
- execution without the customer saying “proceed” and with zero confirmation questions;
- deterministic trust invocation and a final `Purchased` state;
- durable OODA goal, observations, decision, and proof;
- `GOAL_COMPLETED_PAYMENT_FULFILMENT_RECEIPT_PROVED` completion;
- replay of the same execution and non-null receipt;
- a matching receipt endpoint and valid 17-event audit chain.

The isolated database was removed afterward and Stripe was not contacted. The automated repository suite currently reports **258 passed, 0 failed, 0 skipped**.

## Current autonomy status

AgentTrust demonstrates advanced bounded goal-driven autonomy approaching Level 5 in the reference environment. Governance, durable execution, OODA state, idempotency, and evidence boundaries are implemented and tested. Production maturity is approximately **4.6/5**, pending certification against several live retail and restaurant providers, operational load testing, and measured outcome-learning quality.

“Autonomous” never means bypassing budget, authority, authentication, allergy, or payment safety.

## Running locally

Requirements:

- .NET 9 SDK;
- SQL Server or PostgreSQL;
- optional Docker Desktop for PostgreSQL, Redis, and Qdrant;
- optional Stripe test account;
- optional OpenAI API key for live Semantic Kernel reasoning.

Apply SQL Server migrations:

```powershell
dotnet ef database update `
  -p src\AgentTrust.Data.Migrations.SqlServer `
  -s src\AgentTrust.Data.Migrations.SqlServer
```

Run with mock payments:

```powershell
$env:Payments__Provider = "Mock"
dotnet run --project src\AgentTrust.Api --launch-profile http
```

Start optional infrastructure:

```powershell
docker compose up -d postgres redis qdrant
docker compose ps
```

Run the suite:

```powershell
dotnet test tests\AgentTrust.Tests\AgentTrust.Tests.csproj
```

Do not commit production secrets. Supply database credentials, OIDC, Data Protection keys, OpenAI, Stripe, and webhook secrets through environment variables or a protected secret store.

## Repository structure

```text
src/AgentTrust.Api                     HTTP API, authentication, agent coordination
src/AgentTrust.Agents                  agent and Semantic Kernel infrastructure
src/AgentTrust.Commerce                purchase, OODA, payment, and goal execution
src/AgentTrust.Connectors              retail/restaurant capability connectors
src/AgentTrust.Consumer                tasks, policy, conversation, and memory models
src/AgentTrust.Mandates                delegated financial authority
src/AgentTrust.PaymentMethods          tokenised payment-method ownership
src/AgentTrust.Payments                payment adapters and durable lifecycle
src/AgentTrust.Policy                  deterministic policy decisions
src/AgentTrust.Data                    EF Core persistence
src/AgentTrust.Data.Migrations.*       SQL Server and PostgreSQL migrations
src/AgentTrust.Intelligence            advisory financial-investigation research
tests/AgentTrust.Tests                 architecture, safety, persistence, and API tests
docs/production-operations.md          deployment and operational guidance
docs/development-history.md            historical research and implementation record
```

## Research history

AgentTrust began as a trustworthy agentic-payments research system with deterministic risk analysis, graph intelligence, bounded Semantic Kernel investigation, adversarial trust-boundary testing, and B0–B4 comparative evaluation. Those capabilities remain as an advisory intelligence layer and cannot authorise payment.

The original phase-by-phase research notes, experiment design, migration history, and historical test counts are preserved in [docs/development-history.md](docs/development-history.md). Operational guidance is in [docs/production-operations.md](docs/production-operations.md).

## Safety boundary

AgentTrust is a research and reference implementation. Use mock or Stripe test payments locally. Live providers require provider certification, protected credentials, production OIDC and Data Protection configuration, webhook verification, monitoring, recovery operations, and an explicit deployment risk assessment.
