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
