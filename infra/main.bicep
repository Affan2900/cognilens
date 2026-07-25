targetScope = 'resourceGroup'

@description('Environment name — dev or prod. Drives the namePrefix and is not otherwise branched on; dev/prod differ via which resource group and param file this is deployed with, not via conditionals in this template.')
@allowed([
  'dev'
  'prod'
])
param environmentName string

@description('Name prefix for the environment, e.g. cognilens-dev.')
param namePrefix string = 'cognilens-${environmentName}'

@description('Primary location for most resources.')
param location string = 'eastus2'

@description('Exact Speech account name — must match the existing resource so this deployment adopts it instead of creating a duplicate.')
param speechAccountName string

@description('Exact OpenAI account name — must match the existing resource so this deployment adopts it instead of creating a duplicate.')
param openAiAccountName string

@description('Exact AI Search service name — must match the existing resource (Free tier allows only one per subscription).')
param searchServiceName string

@description('Azure OpenAI chat deployment capacity.')
param chatDeploymentCapacity int = 10
@description('Azure OpenAI embedding deployment capacity.')
param embeddingDeploymentCapacity int = 10

@description('Full Api container image reference, e.g. ghcr.io/owner/cognilens-api:tag.')
param apiImage string
@description('Full Worker container image reference, e.g. ghcr.io/owner/cognilens-worker:tag.')
param workerImage string

@description('API keys accepted by the Api auth middleware, comma-separated. Pass via --parameters apiKeys=... at deploy time — never committed to a param file.')
@secure()
param apiKeys string

@description('Email address for budget alerts.')
param notificationEmail string
@description('Monthly budget amount.')
param budgetAmount int = 10

@description('Max replicas for the Api app.')
param apiMaxReplicas int = 2
@description('Max replicas for the Worker app.')
param workerMaxReplicas int = 2

@description('Revision suffix for this deploy of the Api app (e.g. short git SHA) — lets cd.yml address the new revision by name for canary smoke testing and traffic shifting. Leave empty for local/manual deploys.')
param apiRevisionSuffix string = ''

@description('Name of the Api revision holding 100% traffic before this deploy runs, queried by cd.yml so the new revision starts at 0% instead of cutting over immediately. Leave empty to let the newest revision take traffic right away (bootstrap / manual deploys).')
param apiStableRevisionName string = ''

// SQL server names are globally unique across all of Azure. Deriving from the resource group
// gives a stable name across redeploys without needing a manually-picked, possibly-taken literal.
var sqlServerName = '${namePrefix}-sql-${uniqueString(resourceGroup().id)}'

module identity 'modules/identity.bicep' = {
  name: 'identity'
  params: {
    location: location
    namePrefix: namePrefix
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    namePrefix: namePrefix
    apiPrincipalId: identity.outputs.apiIdentityPrincipalId
    workerPrincipalId: identity.outputs.workerIdentityPrincipalId
  }
}

module obs 'modules/obs.bicep' = {
  name: 'obs'
  params: {
    location: location
    namePrefix: namePrefix
  }
}

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    namePrefix: namePrefix
    tenantId: subscription().tenantId
    apiPrincipalId: identity.outputs.apiIdentityPrincipalId
    workerPrincipalId: identity.outputs.workerIdentityPrincipalId
  }
}

module ai 'modules/ai.bicep' = {
  name: 'ai'
  params: {
    speechAccountName: speechAccountName
    openAiAccountName: openAiAccountName
    workerPrincipalId: identity.outputs.workerIdentityPrincipalId
    chatDeploymentCapacity: chatDeploymentCapacity
    embeddingDeploymentCapacity: embeddingDeploymentCapacity
  }
}

module search 'modules/search.bicep' = {
  name: 'search'
  params: {
    searchServiceName: searchServiceName
    workerPrincipalId: identity.outputs.workerIdentityPrincipalId
    apiPrincipalId: identity.outputs.apiIdentityPrincipalId
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    location: location
    sqlServerName: sqlServerName
    apiIdentityName: '${namePrefix}-api-mi'
    workerIdentityName: '${namePrefix}-worker-mi'
  }
  dependsOn: [
    identity
  ]
}

module aca 'modules/aca.bicep' = {
  name: 'aca'
  params: {
    location: location
    namePrefix: namePrefix
    logAnalyticsWorkspaceId: obs.outputs.logAnalyticsWorkspaceId
    logAnalyticsCustomerId: obs.outputs.logAnalyticsCustomerId
    appInsightsConnectionString: obs.outputs.appInsightsConnectionString
    apiIdentityId: identity.outputs.apiIdentityId
    apiIdentityClientId: identity.outputs.apiIdentityClientId
    workerIdentityId: identity.outputs.workerIdentityId
    workerIdentityClientId: identity.outputs.workerIdentityClientId
    apiImage: apiImage
    workerImage: workerImage
    sqlServerFqdn: sql.outputs.sqlServerFqdn
    sqlDatabaseName: sql.outputs.databaseName
    storageAccountName: storage.outputs.storageAccountName
    storageBlobEndpoint: storage.outputs.blobEndpoint
    storageQueueEndpoint: storage.outputs.queueEndpoint
    speechEndpoint: ai.outputs.speechEndpoint
    openAiEndpoint: ai.outputs.openAiEndpoint
    chatDeploymentName: ai.outputs.chatDeploymentName
    embeddingDeploymentName: ai.outputs.embeddingDeploymentName
    searchEndpoint: search.outputs.searchEndpoint
    apiKeys: apiKeys
    apiMaxReplicas: apiMaxReplicas
    workerMaxReplicas: workerMaxReplicas
    apiRevisionSuffix: apiRevisionSuffix
    apiStableRevisionName: apiStableRevisionName
  }
}

module budget 'modules/budget.bicep' = {
  name: 'budget'
  params: {
    namePrefix: namePrefix
    budgetAmount: budgetAmount
    notificationEmail: notificationEmail
  }
}

output apiUrl string = aca.outputs.apiUrl
