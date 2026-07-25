using '../main.bicep'

param environmentName = 'dev'

// eastus2 (the default `location`) rejects new SQL logical server creation on this subscription
// (RegionDoesNotAllowProvisioning); eastus already hosts cognilens-search-dev successfully.
param sqlLocation = 'eastus'

param speechAccountName = 'cognilens-speech-dev'
param openAiAccountName = 'cognilens-openai-dev'
param searchServiceName = 'cognilens-search-dev'

param apiImage = 'ghcr.io/affan2900/cognilens-api:latest'
param workerImage = 'ghcr.io/affan2900/cognilens-worker:latest'

param notificationEmail = 'affanamir290@gmail.com'

// No secret literal in this file. readEnvironmentVariable() pulls the value from the deploying
// shell at build time — set COGNILENS_API_KEYS before running `az deployment group create`.
param apiKeys = readEnvironmentVariable('COGNILENS_API_KEYS')
