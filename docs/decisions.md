# Decision log

Running log of non-obvious calls made during implementation. Folded into the final
`README.md` decision log in Phase 6.

## Phase 2

- **LLM: `gpt-5-mini` instead of the plan's `gpt-4o-mini`.** `gpt-4o-mini` is deprecated
  for new deployments as of mid-2026; `gpt-5-mini` is its GA successor with the same
  cost tier and structured-outputs support. Recorded inline in `plan.md`'s stack table.

- **Local dev auth: host `dotnet run` processes, not `docker compose`, for Phase 2
  AI testing.** `DefaultAzureCredential`/`AzureCliCredential` read the token cache
  `az login` writes to the host user profile; the Linux containers in
  `docker-compose.yml` can't see that cache, so `AzureCliCredential` fails inside them.
  Running Api/Worker as host processes against the same Azurite/mssql containers lets
  local dev authenticate to real Azure AI services without pushing secrets into the
  compose file. This is a stopgap — Phase 3's Managed Identity removes the need for
  `az login` entirely once the services run in ACA, and at that point
  `docker compose` should work again for anyone who wants a fully local loop.

- **Azurite blobs are not reachable by Azure AI Speech's batch-transcription
  service, and this is not being worked around.** Speech batch transcription fetches
  the audio itself via an HTTPS `contentUrl` — it cannot reach
  `http://127.0.0.1:10000/...` or `http://azurite:10000/...`. Confirmed live: a
  real job submitted against an Azurite blob URI came back with
  `InvalidUri — Error when downloading recordings`, and the Worker's existing
  retry/dead-letter logic (3 attempts, then `DeadLettered`) handled it correctly —
  which is itself useful signal that the retry path works end-to-end against a real
  failure, not a mocked one.
  **Not fixing this by provisioning an internet-reachable Storage account for local
  dev** — that would mean paying for/managing a second Storage account (or exposing
  the dev one) purely to make local smoke tests exercise Speech, which widens scope
  beyond what Phase 2 needs. Speech, OpenAI, and Search are instead being validated
  independently: Speech against a real call recording once the system is actually
  deployed to Azure (Phase 3), and OpenAI/Search directly with synthetic transcript
  data that skips the blob-fetch step entirely (see below).

- **OpenAI and AI Search validated directly, bypassing blob/Speech.** A throwaway
  console harness (not checked in) referenced `CogniLens.Core`/`CogniLens.Infrastructure`,
  resolved `IEmbeddingService`, `IQaAnalysisService`, and `ISearchIndexService` from the
  real DI container, and called them with a hand-written synthetic transcript. Results
  against the real Azure resources: `text-embedding-3-small` returned proper 1536-dim
  vectors; `gpt-5-mini` returned schema-valid structured output including a correctly
  *failed* rubric (the synthetic agent script never verified customer identity, and the
  model caught it) — good evidence the rubric grading is doing real reasoning, not
  rubber-stamping; and AI Search's hybrid+semantic query correctly ranked the
  outage/credit chunks above small talk for the query "outage credit compensation".
  This closes out Phase 2 validation: every AI integration has now been exercised
  against real Azure resources, and the Worker's retry → dead-letter path has been
  proven against a real, non-transient failure (see above), not just a mocked one.

## Phase 3

- **Fixed a real non-negotiable violation found in Phase 2 code: Storage was
  shared-key-only.** `BlobServiceClient`/`QueueServiceClient` were built from a
  connection string everywhere, including the path that would run in ACA. Fixed by
  branching construction on whether `Storage:ConnectionString` is set (dev/Azurite —
  keys are fine against an emulator) vs. `new Uri(...)` + `TokenCredential` (prod/ACA).
  SAS issuance (`CreateUploadTicketAsync`, `GenerateReadSasUriAsync`) now branches the
  same way: shared-key `GenerateSasUri` in dev, user-delegation SAS
  (`GetUserDelegationKeyAsync` + `BlobSasBuilder.ToSasQueryParameters`) in prod — the
  AAD-auth equivalent, since delegation keys can't be minted without a live token
  credential. `allowSharedKeyAccess: false` added to the storage account in Bicep so
  the account itself refuses key auth going forward, not just the app code.

- **ACA API version bumped to `2025-01-01` (from `2024-03-01`) for both
  `managedEnvironments` and `containerApps`.** Needed for the `identity` property on
  a KEDA `CustomScaleRule` — without it, the only way to auth a queue-based scale rule
  is a storage connection-string secret, which violates "no secrets, MI everywhere."
  This is also the mechanism that lets the Worker app wake from `minReplicas: 0` at
  all (it has no inbound HTTP of its own to trigger HTTP-based scaling), so it's load
  bearing for the min-replicas=0 non-negotiable, not just a auth nicety.

- **Dev and prod share one AI Search instance (`cognilens-search-dev`).** Free tier
  is capped at one instance per subscription, and the non-negotiable pins AI Search to
  Free. `prod.bicepparam` points `searchServiceName` at the dev instance rather than
  provisioning a second (impossible) or paying for Basic+ (against the non-negotiable).
  Documented as a deliberate demo-scale tradeoff — a real prod environment would need
  its own paid instance.

- **Resource adoption pattern for Speech/OpenAI/AI Search.** These three already
  existed in the subscription from earlier manual setup. Rather than importing them
  or renaming, the Bicep resource blocks use the exact existing names
  (`cognilens-speech-dev`, `cognilens-openai-dev`, `cognilens-search-dev`), so
  deployment converges/updates them in place instead of erroring or duplicating.
  Confirmed via `what-if`: these three show as `Modify`, everything genuinely new
  shows as `Create` — the adoption is working as designed.

- **Auth scope: shared-secret API key + rate limiting applied to all of `/api/*`,
  not just `/analyze` and `/api/search`.** The non-negotiable named those two
  endpoints specifically (the AI-cost-bearing ones), but gating only those two would
  leave `/api/calls` (POST/GET) reachable anonymously once deployed, which is still a
  publicly-reachable-with-no-auth surface. `ApiKeyMiddleware` (shared secret via
  `X-Api-Key`) is applied via `UseWhen` to the whole `/api/*` prefix; the
  `ai-cost-guardrail` rate-limit policy (30 req/min, fixed window, partitioned by API
  key) stays scoped to just `AnalyzeCall` and `Search` per the non-negotiable's intent,
  since those are the calls that actually cost money per-request.

- **`listKeys()` used inline (not stored) to wire the Log Analytics workspace key
  into `Microsoft.App/managedEnvironments`' classic log destination.** This API
  surface has no Managed Identity-based alternative for connecting an ACA environment
  to Log Analytics — the workspace key is the only option. It's read at deploy time
  via `listKeys()` and passed directly into the resource property, never persisted to
  a parameter file, output, or app setting, so it doesn't violate "no secrets in
  code/config" in spirit even though it's technically a key-based connection.

- **SQL is AAD-only (`azureADOnlyAuthentication: true`); contained database users
  for the Api/Worker managed identities are bootstrapped via a
  `Microsoft.Resources/deploymentScripts` (AzureCLI kind) running `sqlcmd` under a
  dedicated `deployIdentity`** that's set as the SQL Server's AAD admin. This avoids
  ever having a SQL admin password, while still letting the MIs get `db_datareader`/
  `db_datawriter` grants without a human running a manual script post-deploy.

- **Full Bicep composition validated against the real `rg-cognilens-dev` resource
  group** via `az deployment group validate` (clean pass) and `what-if`
  (`status: Succeeded`, 21 `Create`, 5 `Modify` for the three adopted resources, 13
  `Unsupported` for role assignments). The `Unsupported` entries are a known what-if
  limitation — role assignment names embed `reference(...).principalId` of
  identities that don't exist yet at diff time, so what-if can't resolve them, but
  they deploy fine for real since `validate` (which fully evaluates deployability)
  passed clean. Not a blocker.

## Phase 4

- **Api Container App switched from `activeRevisionsMode: Single` to `Multiple`.**
  The plan's canary requirement (create new revision, smoke test its direct URL,
  shift traffic 10%, wait, shift 100%, roll back on failure) has no meaning under
  `Single` mode, which just replaces the running revision outright on every deploy.
  Worker stays `Single` — it has no ingress traffic to shift (internal-only, scaled by
  queue depth), so canary semantics don't apply to it.

- **Canary traffic shifting is imperative (`az containerapp ingress traffic set`),
  not declarative in Bicep — ARM has no primitive for "gradually move weight from
  revision A to B."** `aca.bicep`'s `traffic` block only expresses a static end-state:
  if `cd.yml` passes the previous stable revision's name in as `apiStableRevisionName`,
  the new revision Bicep just created starts at 0% weight (100% stays pinned to the old
  one); if left empty (bootstrap, or a manual/local deploy), it falls back to
  `latestRevision: true` so the newest revision just takes over immediately. `cd.yml`
  queries the current 100%-weight revision before the Bicep deploy, passes it in, then
  does the actual 10% to 100% shift afterward with explicit CLI calls against the new
  revision's own name (`${apiName}--${shortSha}`, via the new `apiRevisionSuffix` param).
  On any failure past that point, a rollback step restores 100% traffic to the old
  revision and deactivates the broken one.

- **Federated credential subjects use GitHub's immutable-ID form, not the
  human-readable one.** The documented subject format
  (`repo:OWNER/REPO:ref:refs/heads/main`) is not what this repo's runner actually
  asserts — the token carries numeric account and repository IDs instead:
  `repo:Affan2900@123811141/cognilens@1312175714:ref:refs/heads/main`. A credential
  registered with the readable form fails every login with `AADSTS700213: No matching
  federated identity record found for presented assertion subject ...`, and the error
  text is the only place the expected value appears. Both credentials
  (`:ref:refs/heads/main` for `cd.yml`, `:pull_request` for `ci.yml`) are registered in
  the ID form; anything added later (notably `:environment:production`) must match it.
  The IDs are immutable, so renaming the account or repo does not invalidate them.

- **`deploy-prod` is a real approval gate over shared infrastructure, not an isolated
  prod environment.** The job is live and blocked on a required reviewer via the
  `production` GitHub Environment (deployments restricted to `main`), which is what
  `plan.md`'s "prod job gated behind a GitHub Environment with a manual approval"
  asks for. What it deliberately does *not* do is deploy to a separate resource group.

  The reason is per-subscription free-tier exhaustion, which is a hard limit rather
  than a preference: Azure SQL's free offer is one database per subscription, AI Search's
  Free tier is one service per subscription, and Speech's F0 tier is one per subscription
  — all three already consumed by dev. A genuinely separate prod would therefore run a
  *paid* SQL server, Speech account, OpenAI account and Container Apps environment.

  Honest accounting of what that would actually cost: it was never priced against live
  Azure rates, so no claim is made here that it would or wouldn't breach the "$8/month
  idle" line. The plausible idle delta is dominated by SQL storage (serverless compute
  auto-pauses to ~zero, and OpenAI/Speech bill per use, so an idle prod is mostly storage
  plus any Log Analytics ingestion above the 5 GB free grant). The decision to share
  infrastructure was taken to keep added cost at exactly zero, not because a priced
  alternative was rejected.

  So the gate, the required-reviewer flow, and the `:environment:production` federated
  credential are all genuinely exercised; the environment *isolation* is the documented
  scope cut. Two things keep the gate from being theatre: promotion is refused unless the
  approved SHA is still the revision serving 100% traffic (an approval can sit unactioned
  while a later push or a rollback moves traffic), and promotion is a server-side
  `docker buildx imagetools` retag, so `:prod` points at the byte-identical manifest that
  passed the canary rather than a fresh rebuild.

- **The canary and rollback paths are verified by execution, not by inspection.** Both
  were dead code for the first eight CD runs: every deploy until 2026-07-26 had an empty
  `STABLE_REVISION` (no previous 100%-traffic revision), so `if: env.STABLE_REVISION != ''`
  skipped the 10% shift, the 60-second re-check and the deactivate-previous step, and
  `if: failure()` had never fired at all. Both have since been exercised for real:

  - *Canary:* traffic moved `bfde59e=90 / 21d3718=10`, held 60s, re-checked
    `/healthz/ready`, shifted to 100%, and deactivated the predecessor.
  - *Rollback:* a deliberately `Unhealthy` readiness check was merged (commit `8e9cec1`,
    reverted by `fd1bab5`) to satisfy the Definition of Done item "a deliberately broken
    health probe triggers automatic rollback". The new revision was created at 0% traffic,
    failed all five smoke-test attempts, the four canary steps were skipped, and the
    rollback step restored `21d3718=100` and deactivated the broken revision. The live
    endpoint returned HTTP 200 throughout — the broken revision never served a request,
    because it sits at 0% until the smoke test says otherwise.

  Worth noting what is still untested: rollback from a *Bicep* failure, where the deploy
  dies before a new revision exists. Lower risk (nothing was ever exposed to traffic) but
  it is a different code path.

- **SQL lives in its own `sqlLocation` (`centralus` for dev), separate from
  `location`.** Azure gates SQL logical-server creation per region *per subscription*,
  independently of every other resource type: `eastus2` and `eastus` both returned
  `RegionDoesNotAllowProvisioning` for this subscription while happily accepting
  Container Apps, Storage, and Key Vault in the same resource group. The authoritative
  answer is `az sql list-usages`/the `Microsoft.Sql/locations/{loc}/capabilities` API,
  not the region picker in the portal — querying it showed only `westus`, `westus3`,
  `centralus`, and `southeastasia` as `Available` here. Two consequences baked into
  `main.bicep`: the region is a separate parameter so it can move without dragging the
  rest of the stack along, and `sqlServerName` folds `sqlLocation` into its
  `uniqueString()` salt because a *failed* create still tombstones the name-to-region
  pairing server-side — the same name then gets rejected as "already exists in location
  eastus2" with no such resource visible anywhere in the subscription.

- **Contained DB users are created `WITH SID = 0x..., TYPE = E`, not
  `FROM EXTERNAL PROVIDER`.** The `FROM EXTERNAL PROVIDER` form makes the SQL server
  call Microsoft Graph to resolve the identity name, which requires the server's
  identity to hold the Directory Readers role — granting that needs Privileged Role
  Administrator, which isn't available on this (university-managed) tenant. Converting
  each managed identity's client ID to a binary SID is the documented equivalent and
  needs no directory permissions at all. The AAD admin assignment on the server sets
  `principalType: 'Application'` for the same reason: it defaults to `Group` and fails
  the same way.

- **The bootstrap deployment script uses `go-sqlcmd`, not `mssql-tools18`.** The
  `AzureCLI` deployment-script image is Azure Linux, which has no `apt-get`, so the
  usual `packages.microsoft.com` install route fails outright. `go-sqlcmd` ships one
  statically-linked binary, so it depends on nothing in the base image beyond `curl`.
  Decompression falls back to Python's `tarfile` because a `bzip2` binary isn't
  guaranteed to be present either.

- **`cd.yml` removes the `containerapp` CLI extension and prefers the implementation
  in core azure-cli.** The extension shadows core and its `ingress traffic set` rejects
  revision names that `revision show` resolves fine on the same runner
  (`Revision 'cognilens-dev-api--<sha>' is not a valid revision name`), which broke the
  canary traffic shift against a revision that was demonstrably Running at 100%. Core
  handles the identical call correctly. The step still falls back to installing the
  extension if a future runner image predates `containerapp` landing in core.

- **Key Vault sets `enablePurgeProtection: true`.** Azure rejects `false` outright on
  this resource — the property is one-way and cannot be set back to disabled once a
  vault has ever had it on, so the API returns `BadRequest` rather than treating it as
  a no-op. The tradeoff is that a deleted vault is unrecoverable-by-deletion for the
  full retention window, which slightly complicates teardown; documented here so the
  Phase 6 teardown instructions account for it.

## Phase 5

- **Trace context crosses the queue in the message body, not in a header.** Azure Storage
  Queues have no message-metadata channel — `SendMessageAsync` takes a single opaque string
  and nothing else — so a `traceparent` has nowhere to live except inside the payload. That
  makes it a schema change on `AnalyzeJobMessage`, which is why both new fields are nullable
  with defaults rather than required positional parameters. Two directions have to keep
  working, and both are exercised by tests in `AnalyzeJobTraceContextTests`:
  - Messages enqueued *before* the deploy are drained by the new Worker. Without the default,
    every in-flight job would throw on deserialisation and dead-letter after three redeliveries.
  - Messages enqueued *after* the deploy are received by the *old* Worker, because during the
    90/10 canary window both revisions are live. This one relies on `System.Text.Json` ignoring
    unknown members, which is the default but is a default worth pinning down in a test rather
    than assuming.

  Service Bus would have given a real header channel (and `ServiceBusMessage.ApplicationProperties`
  is where the OpenTelemetry messaging convention expects trace context to go), but Storage Queues
  is what the KEDA `azure-queue` scaler authenticates against with a managed identity and no
  connection string, which was the stronger constraint.

- **The producer writes `Activity.Id` directly instead of using an OpenTelemetry propagator.**
  .NET defaults to `ActivityIdFormat.W3C`, so `Activity.Id` *is* the `traceparent` header value
  verbatim — running it through `Propagators.DefaultTextMapPropagator.Inject` into a one-entry
  dictionary would produce the identical string via a carrier abstraction that buys nothing here.
  The consumer symmetrically uses `ActivitySource.StartActivity(name, kind, parentId)`, which
  parses W3C ids natively. The cost of this choice is that `Baggage` is not propagated; nothing
  in CogniLens uses baggage, and adding it later is a one-line change on both sides.

  The producer falls back to `Activity.Current` when `StartActivity` returns `null`. That is not
  defensive padding: `StartActivity` returns `null` whenever no listener has sampled the source,
  and dropping the hop *because tracing is only partially configured* would be the worst possible
  failure mode — a split trace with no error anywhere.

- **`APPLICATIONINSIGHTS_CONNECTION_STRING` was set on both container apps from Phase 2 but
  nothing read it until now.** `aca.bicep` has been injecting it since the observability module
  landed, and `obs.bicep` has been provisioning a workspace-based Application Insights the whole
  time — the app simply had no OpenTelemetry packages, so every deployment up to this point ran
  with a fully provisioned, entirely unused telemetry pipeline. Worth recording because "the
  infrastructure exists" and "the signal exists" looked identical from the portal.

- **A daily ingestion cap was added to the Log Analytics workspace in the same change that
  enabled export.** `PerGB2018` bills past Azure Monitor's 5 GB/month free allowance, and until
  Phase 5 the bill was structurally zero no matter what the app did, because nothing exported.
  Turning telemetry on removes that accident, so `workspaceCapping.dailyQuotaGb` is set to 0.2 —
  comfortably inside the free allowance even at a sustained worst case, and a hard stop rather
  than an alert. The monthly budget alert cannot serve this purpose: it evaluates on a schedule,
  so a retry storm could run the meter for hours before it fires. Hitting the cap drops telemetry
  until 00:00 UTC, which is the deliberate trade — lost visibility is recoverable, an unbounded
  bill on a personal subscription is not.

- **Health probes are filtered out of tracing.** Container Apps probes `/healthz/live` and
  `/healthz/ready` continuously on every replica, and `cd.yml`'s smoke test adds five more hits
  per deploy. Unfiltered they are the overwhelming majority of spans — noise that also consumes
  the ingestion budget above for no diagnostic value.

- **Resource attributes carry the Container Apps revision and replica name.** `CONTAINER_APP_REVISION`
  ends in the 7-character git SHA (`cognilens-dev-api--86fbe15`), which is what makes a span
  attributable to an exact commit and lets the canary window be sliced old-revision vs new.
  `CONTAINER_APP_REPLICA_NAME` becomes `service.instance.id`, without which every replica behind
  a revision collapses into one indistinguishable stream and "one bad replica or the whole
  revision?" is unanswerable.

- **The message visibility timeout was 30 seconds and had to be longer than the job.** Storage
  Queues leases a received message for a fixed window; if the lease expires before the message is
  deleted, the queue redelivers it and increments `DequeueCount`. The Worker's window was 30
  seconds while a single job is dominated by a batch transcription that
  `AzureSpeechTranscriptionService` allows up to *fifteen minutes* to complete. The lease could
  therefore expire mid-job on every successful run, and after three such redeliveries the job
  dead-letters on `DequeueCount` alone with nothing actually wrong. Raised to 20 minutes, which
  covers the worst case the transcription timeout permits plus the embedding, analysis and
  indexing that follow.

  The more precise fix is to renew the lease periodically with `UpdateMessageAsync` while work is
  in flight; it also returns a message faster when a replica dies. It was not chosen because it
  means threading a mutating pop receipt through every delete and dead-letter path, and the cost
  of the simpler option is bounded and knowable: a crash mid-job leaves that one message invisible
  for 20 minutes. That is a worse failure mode than renewal and a much better one than guaranteed
  spurious redelivery.

- **`ReceiveMessagesAsync` takes one message at a time, not five.** Compounding the above: a batch
  receive makes *every* message in the batch invisible for the same window, starting when the
  batch was received, but the loop processes them sequentially. Messages 2–5 spent their entire
  lease waiting their turn. Batching only pays off when per-message processing is fast, which is
  the opposite of the case here. Concurrency now comes from replicas, where KEDA can actually
  reason about it, rather than from a batch size the queue's leasing model does not account for.

- **`workerMaxReplicas` raised from 2 to 5.** The KEDA rule targets `queueLength: '1'`, so KEDA
  wants one replica per queued message and the replica ceiling is the only thing bounding
  concurrency. At 2 the scale rule could barely demonstrate scaling at all. Combined with
  one-message-per-receive this caps in-flight jobs at exactly 5 — which is also a useful ceiling
  on concurrent calls into the free-tier Speech and OpenAI quotas.

- **Metric tags are deliberately low-cardinality; per-job identifiers live on spans instead.**
  Application Insights bills custom metrics per time series, so a tag carrying a call id or a
  message id turns one metric into one series per job. `cognilens.job.duration` is tagged by
  outcome, `cognilens.llm.tokens` by direction and deployment — all bounded sets. The call id is
  on the span, where high cardinality is the norm and is what makes a single trace findable.

- **`cognilens.transcription.duration` and `cognilens.transcription.audio_duration` are separate
  metrics.** Speech bills per hour of audio, but most of the wall-clock time is the batch job
  waiting in Azure's own queue. Reporting a single "transcription seconds" figure would conflate
  cost with latency in both directions: a slow queue would look expensive, and a long call
  processed quickly would look cheap.

- **`cognilens.queue.depth` is an observable gauge that emits nothing until it has a sample.**
  The authoritative count lives in Azure Storage — several replicas consume the same queue, so a
  locally incremented counter would be wrong on every one of them. The callback reads a value
  cached by the poll loop rather than making a storage call, because OpenTelemetry invokes
  observable callbacks on its own collection thread with no cancellation token, and blocking that
  thread would stall the export of every other metric. When no sample exists yet the callback
  yields no measurement at all: reporting 0 would read as "the backlog is drained", which is the
  one wrong conclusion that would stop someone investigating. Note that with `minReplicas: 0` an
  idle system reports nothing rather than zero, since there is no replica awake to observe it.

- **No second correlation id was invented.** The W3C trace id already ties the Api request, the
  queue hop and every Worker span together; a parallel id scheme would just create two ids that
  can disagree. What was genuinely missing was a way for a user to *quote* it, so
  `TraceCorrelationMiddleware` returns the trace id as `X-Correlation-Id` and the CORS policy
  exposes that header — without `WithExposedHeaders` the browser can see the response but not read
  the header off it, which would have made the whole thing invisible to the Blazor frontend. An
  inbound `X-Correlation-Id` from a caller's own system is recorded as a span tag, length-capped,
  and never used in place of the trace id: an id chosen by the caller is not trustworthy as unique.

- **Kestrel's request body limit is 128 KB, down from the 30 MB default.** Audio never transits
  this API — clients PUT it straight to a blob SAS URL — so every endpoint takes a query string or
  a small JSON body. The default was roughly 240x larger than anything legitimate, and an oversized
  body is memory an unauthenticated caller can make the process allocate before `ApiKeyMiddleware`
  runs. The search `q` parameter needed its own separate cap: it arrives in the query string, which
  the body limit does not cover, and it is billed work once it reaches embedding and Azure AI Search.

  Not covered by any of this: the blob SAS URL itself grants an unbounded PUT. Azure SAS has no
  content-length constraint to attach, so the upload size ceiling would have to come from a
  different mechanism entirely. Recorded as a known gap rather than left implied.

## Phase 6 — frontend hosting

- **The Blazor frontend is hosted on Azure Static Web Apps (Free tier), not served from the Api
  container app.** Serving the published `wwwroot` from the Api via `UseStaticFiles` was the
  tempting option: it makes the SPA same-origin and deletes the CORS problem outright. It was
  rejected because it couples every frontend change to an Api revision, a canary traffic shift and
  an EF migration run, and because it puts a static bundle behind a `minReplicas: 0` container that
  cold-starts. Static Web Apps is a CDN, costs $0, and allows up to 10 free apps per subscription —
  so unlike Azure SQL and AI Search it does not consume a one-per-subscription free allocation that
  a future prod environment would need. Published bundle is ~26 MB against a 250 MB Free-tier
  ceiling.

  The cost of that choice is that the frontend is a separate origin, so CORS has to be configured
  rather than avoided. Which surfaced the real defect below.

- **The deployed Api was allowing zero browser origins.** `Cors:AllowedOrigins` was set only in
  `appsettings.Development.json`; no environment variable ever supplied it in Azure, so
  `Program.cs` fell through to its `?? []` default. The CORS policy, the explicit `X-Api-Key`
  grant and the `X-Correlation-Id` exposure written in Phase 4 were all correct and all inert —
  any browser call from a deployed frontend would have been blocked. `aca.bicep` now sets
  `Cors__AllowedOrigins__0` from the Static Web App's hostname. Worth noting how this stayed
  invisible: there was no frontend deployed to fail against, and the smoke test in `cd.yml` calls
  `/healthz` with curl, which is not a browser and therefore never sends an `Origin` header.

- **The Static Web App's hostname flows through Bicep, so there is no two-pass bootstrap.**
  `defaultHostname` is only knowable after the resource exists, and two things need it: the Api's
  CORS allow-list and the storage account's own CORS rule (the browser PUTs audio straight to a
  blob SAS URL, which the Api's policy has no say over). Both take it as a module input, and ARM
  sequences the deployment from the output reference. `storage.bicep`'s `webOriginUrl` lost its
  `'*'` default in the process — that default was a placeholder for a hosting URL that did not
  exist yet, and leaving it defaultable would let a missed wiring silently reopen the account to
  every origin instead of failing the deploy.

- **The API key is entered at runtime and kept in `localStorage`; it is never built into the
  bundle.** A WASM app's `wwwroot/appsettings.json` is a public download, so a key placed there
  would be readable by every visitor — and that key is what partitions the `ai-cost-guardrail`
  rate limiter protecting paid Azure AI spend against the $10 budget. `ApiKeyStore` and the connect
  banner in `MainLayout.razor` already did this correctly; recorded here because "the config file
  is the obvious place for it" is exactly the change a future edit would make.

- **`ApiBaseUrl` is injected before `dotnet publish`, not patched into the published output.**
  A Blazor WASM app has no server to read environment variables from; its configuration is a static
  file the browser downloads. The build hashes the config files it emits, so editing them after
  publishing desyncs those hashes from the content the runtime fetches. `cd.yml` rewrites the
  source file with `jq`, then publishes, then greps the output for the placeholder and fails the
  job if it survives — with `Program.cs` refusing to start on the placeholder as a backstop for
  manual publishes. A silently skipped injection would otherwise produce a bundle that builds,
  deploys and only fails when a user loads it.

- **The Static Web App is not linked to the GitHub repository, and its deployment token is not a
  GitHub secret.** Linking the app to a repo makes Azure generate and commit its own workflow file,
  which would then race `cd.yml` for the same deployment. Instead `cd.yml` fetches the upload token
  at deploy time via `az staticwebapp secrets list` on the OIDC session it already holds. Same
  posture as the rest of the pipeline: the only long-lived credential is the federated identity,
  and there is no deployment token in repo settings to leak or rotate.

- **The frontend deploys in its own job, gated on `deploy-dev` completing.** The SPA is uploaded
  only once the canary has shifted 100% of traffic to the new Api revision — publishing earlier
  would leave a frontend built against a revision that a rollback then retires. Keeping it out of
  `deploy-dev` also means a frontend-only failure cannot trip that job's `failure()` rollback and
  revert a healthy Api revision.

- **`staticwebapp.config.json` sets a navigation fallback to `/index.html`, excluding
  `/_framework/*`.** Without the fallback, a deep link to `/calls/{id}` is a 404 from the CDN that
  never reaches the Blazor router. Without the exclusion, a missing framework asset would be
  answered with `index.html` at HTTP 200, and the runtime would fail trying to parse HTML as
  WebAssembly — a far worse failure to diagnose than a 404.
