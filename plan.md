# CogniLens — Implementation Plan

An ASP.NET Core + Azure AI system that ingests contact-centre call recordings and produces
structured QA reports: summary, per-speaker sentiment, compliance checklist with quoted
evidence, and a searchable transcript archive.

Built as a portfolio project. Two goals of equal weight: (1) a real async backend, not an
LLM chat wrapper; (2) a production-shaped DevOps layer — IaC, CI/CD with canary + rollback,
observability, and managed identity.

---

## Non-negotiables

Read these before choosing any implementation. They are cost and design guardrails, not
suggestions.

- **No AKS.** Azure Container Apps only. Nodes cost ~$60/mo for a workload ACA handles better.
- **Azure AI Search must stay on the Free tier** (3 indexes, 50 MB, ~10K documents). Basic
  is ~$74/mo — more than every other line item combined. This caps *chunk count*, not just
  storage: size the chunking strategy (chunk length, overlap) against the 10K-document
  ceiling before Phase 2, not after indexing fails. The Free tier's semantic ranker also
  has its own usage quota (~1,000 queries/month) — same usage-not-just-resource risk as the
  Speech/OpenAI guardrail above. Cap the demo corpus to fit.
- **Azure SQL must use the free offer** (100k vCore-seconds + 32 GB/month, serverless,
  auto-pause). Set `Behavior when free limit reached` = auto-pause.
- **min-replicas = 0** on both container apps unless explicitly changed for a demo.
- **No secrets in code, config, or pipeline variables.** Managed Identity for every Azure
  resource. Key Vault only for third-party secrets that have no MI path.
- **Container registry = GitHub Container Registry**, not ACR. Free, and ACA pulls from it fine.
- Budget alert at $10 must exist in the Bicep, not clicked in the portal.
- Everything runs locally first via `docker compose` (Azurite + mssql + the two services)
  before anything touches Azure.
- **Minimal auth + rate limiting must exist before the API is ever publicly reachable**,
  i.e. from the first Phase 3 deploy — not deferred to Phase 5. Applies to `/analyze` AND
  `/api/search` — a stranger hammering either endpoint is the realistic way this blows past
  $10/mo, since Speech, OpenAI, and Search's semantic ranker all meter usage, not just
  storage. A single API key check or IP allowlist is enough for a demo; full rate limiting
  can still land in Phase 5.

---

## Stack

| Layer | Choice |
|---|---|
| API | ASP.NET Core Web API, .NET 9, controllers, EF Core |
| Worker | .NET Worker Service (`BackgroundService`) |
| Queue | Azure Service Bus (Storage Queue acceptable if simpler locally) |
| DB | Azure SQL (free offer) / mssql container locally |
| Blob | Azure Blob Storage / Azurite locally |
| Transcription | Azure AI Speech — batch transcription + diarization |
| LLM | Azure OpenAI `gpt-5-mini` (`gpt-4o-mini` is deprecated for new deployments as of mid-2026; `gpt-5-mini` is its GA successor), structured outputs against a JSON schema |
| Embeddings | `text-embedding-3-small` |
| Retrieval | Azure AI Search — hybrid (BM25 + vector) + semantic ranker |
| Frontend | Blazor WASM or React on Static Web Apps (free tier) |
| IaC | Bicep |
| CI/CD | GitHub Actions |
| Telemetry | OpenTelemetry → Application Insights |

---

## Repo layout

```
/src
  CogniLens.Api/            # HTTP surface, auth, EF Core, SAS token issuing
  CogniLens.Worker/         # queue consumer, transcription + analysis pipeline
  CogniLens.Core/           # domain models, JSON schemas, rubric definitions
  CogniLens.Infrastructure/ # Azure clients, EF DbContext, repositories
  CogniLens.Web/            # SPA
/tests
  CogniLens.UnitTests/
  CogniLens.IntegrationTests/   # Testcontainers
/infra
  main.bicep
  modules/                 # aca.bicep, sql.bicep, search.bicep, ai.bicep, obs.bicep
  params/dev.bicepparam
  params/prod.bicepparam
/.github/workflows
  ci.yml
  cd.yml
docker-compose.yml
README.md
```

---

## Phases

### Phase 0 — Scaffold
- Solution + five projects + two test projects.
- `docker compose` bringing up Azurite, mssql, and both services.
- EF Core migrations for: `Call`, `TranscriptSegment`, `QaReport`, `Rubric`, `JobStatus`.
- Health endpoints (`/healthz/live`, `/healthz/ready`) on both services.

### Phase 1 — Vertical slice, local only
- `POST /api/calls` → issues a blob SAS token, creates a `Call` row in `Pending`.
- Client uploads audio directly to blob (never through the API).
- `POST /api/calls/{id}/analyze` → enqueues a job message.
- Worker consumes, marks `Processing`, does a **stubbed** analysis, writes `Completed`.
- `GET /api/calls/{id}` returns status + report.
- Consumers must be idempotent (dedupe on message id). Dead-letter after 3 attempts.
- Polly retry policies on all outbound calls.

### Phase 2 — Real AI pipeline
- Speech batch transcription with diarization; poll the job, store segments with speaker tags.
- Chunk the transcript; embed with `text-embedding-3-small`.
- Azure OpenAI call with a **strict JSON schema**: summary, sentiment per speaker,
  rubric results (`pass` / `fail` / `not_applicable` + verbatim evidence span), next-best-action.
- Reject and retry once on schema validation failure; log the raw output on second failure.
- Index transcript chunks into Azure AI Search (hybrid: keyword + vector + semantic ranker).
- `GET /api/search?q=` returning ranked segments with call context.

### Phase 3 — Infrastructure as code
- Bicep modules for: Container Apps environment + 2 apps, Service Bus, Storage, SQL (free
  offer), AI Search (free), Azure OpenAI deployments, Speech, Key Vault, Log Analytics,
  App Insights, budget alert.
- User-assigned managed identity with scoped RBAC role assignments — no keys anywhere.
- Two parameter files: `dev`, `prod`. Separate resource groups.

### Phase 4 — CI/CD
**ci.yml** (on PR): restore → build → unit tests → integration tests (Testcontainers) →
`dotnet format --verify-no-changes` → build image → Trivy scan (fail on HIGH/CRITICAL) →
`az deployment group what-if` posted as a PR comment.

**cd.yml** (on merge to main): push image tagged with git SHA to GHCR → deploy Bicep
(incremental) → create new ACA revision → smoke test the new revision's direct URL →
shift traffic 10% → wait → shift 100% → on any failure, revert traffic to the previous revision.

- Prod job gated behind a GitHub Environment with a manual approval.
- OIDC federated credentials for Azure auth. No `AZURE_CREDENTIALS` secret.

### Phase 5 — Observability & hardening
- OpenTelemetry tracing propagated across API → Service Bus → Worker (one trace per call).
- Custom metrics: `tokens_consumed_per_job`, `transcription_seconds`, `llm_schema_failures`,
  `job_duration`, `queue_depth`.
- KEDA scale rule on the worker: scale on Service Bus queue length, 0 → 5.
- Rate limiting on the API. Request size caps. Structured logging with correlation ids.
- Load-shed test: enqueue 50 jobs, confirm the worker scales and nothing is lost.

### Phase 6 — Docs
- `README.md`: problem statement, architecture diagram, **decision log** (why Speech instead
  of Whisper, why ACA instead of AKS, why hybrid search), cost breakdown table, teardown
  instructions, 3-minute demo video link.
- `docs/runbook.md`: how to roll back, how to drain the dead-letter queue, what each alert means.

---

## Definition of done

- [ ] `docker compose up` runs the whole system locally with no Azure dependency
- [ ] A push to main deploys to prod with zero manual steps beyond one approval click
- [ ] A deliberately broken health probe triggers automatic rollback
- [ ] `grep -ri "AccountKey\|api-key\|password=" src/ infra/` returns nothing
- [ ] Monthly idle cost verified under $8 in Azure Cost Analysis
- [ ] README explains what the system costs to run

---

## Notes for the agent

- Prefer boring, well-documented .NET patterns over clever ones. This code is read by
  interviewers, not just executed.
- Write the integration test before the Azure client wrapper it covers.
- When a step needs a design decision (queue vs topic, Blazor vs React), state the tradeoff
  in one line, pick one, and record it in the decision log — do not stop to ask.
- Never widen a cost guardrail to make something easier. Flag it instead.