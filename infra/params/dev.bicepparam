using '../main.bicep'

param environmentName = 'dev'

param speechAccountName = 'cognilens-speech-dev'
param openAiAccountName = 'cognilens-openai-dev'
param searchServiceName = 'cognilens-search-dev'

// TODO: replace once the GitHub repo/owner is finalized.
param apiImage = 'ghcr.io/REPLACE_WITH_GH_OWNER/cognilens-api:latest'
param workerImage = 'ghcr.io/REPLACE_WITH_GH_OWNER/cognilens-worker:latest'

param notificationEmail = 'affanamir290@gmail.com'

// No secret literal in this file. readEnvironmentVariable() pulls the value from the deploying
// shell at build time — set COGNILENS_API_KEYS before running `az deployment group create`.
param apiKeys = readEnvironmentVariable('COGNILENS_API_KEYS')
