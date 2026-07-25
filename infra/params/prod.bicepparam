using '../main.bicep'

param environmentName = 'prod'

param speechAccountName = 'cognilens-speech-prod'
param openAiAccountName = 'cognilens-openai-prod'

// AI Search Free tier allows only one instance per subscription (non-negotiable: Search must stay
// Free). Dev and prod share the one free-tier service rather than prod getting its own — a
// demo-scale limitation, not an oversight. See docs/decisions.md.
param searchServiceName = 'cognilens-search-dev'

// TODO: replace once the GitHub repo/owner is finalized.
param apiImage = 'ghcr.io/REPLACE_WITH_GH_OWNER/cognilens-api:latest'
param workerImage = 'ghcr.io/REPLACE_WITH_GH_OWNER/cognilens-worker:latest'

param notificationEmail = 'affanamir290@gmail.com'

// No secret literal in this file. readEnvironmentVariable() pulls the value from the deploying
// shell at build time — set COGNILENS_API_KEYS before running `az deployment group create`.
param apiKeys = readEnvironmentVariable('COGNILENS_API_KEYS')
