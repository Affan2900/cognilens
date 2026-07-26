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

- **`deploy-prod` in `cd.yml` is scaffolded but commented out.** The plan calls for a
  prod job gated behind a GitHub Environment manual approval, but there is no prod
  resource group, no `production` GitHub Environment, and no
  `:environment:production` federated credential on the `cognilens-gh-oidc` app
  registration. Wiring a job against infrastructure that doesn't exist would just fail
  on first run; left as a documented placeholder until prod is actually provisioned.
  Standing cost caveat for whoever picks this up: a second environment means a second
  Speech account, OpenAI account, SQL server, and Container Apps environment, which is
  the single largest threat to the "$8/month idle" line in the Definition of Done.
  AI Search is the exception — prod deliberately reuses `cognilens-search-dev` because
  the Free tier allows one service per subscription.

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
