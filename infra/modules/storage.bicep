@description('Location for the storage account.')
param location string

@description('Name prefix for the environment, e.g. cognilens-dev.')
param namePrefix string

@description('Principal ID of the Api managed identity.')
param apiPrincipalId string

@description('Principal ID of the Worker managed identity.')
param workerPrincipalId string

@description('Blob container that holds uploaded call recordings.')
param containerName string = 'call-audio'

@description('Queue that carries analyze job messages.')
param queueName string = 'analyze-jobs'

@description('Poison queue for messages that exceed max delivery attempts.')
param poisonQueueName string = 'analyze-jobs-poison'

// Storage account name must be <=24 chars, lowercase alphanumeric only.
var storageAccountName = replace('${namePrefix}st', '-', '')

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // AAD/RBAC only — no account-key auth path, per the no-keys non-negotiable
    supportsHttpsTrafficOnly: true
  }
}

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource callAudioContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: containerName
  properties: {
    publicAccess: 'None'
  }
}

resource queueServices 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource analyzeQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueServices
  name: queueName
}

resource poisonQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueServices
  name: poisonQueueName
}

// Built-in role definition IDs (same across all Azure tenants).
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageBlobDelegatorRoleId = 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a' // required to mint user-delegation SAS tokens
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'

var principals = [
  { id: apiPrincipalId, tag: 'api' }
  { id: workerPrincipalId, tag: 'worker' }
]

resource blobDataContributorAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for p in principals: {
  name: guid(storageAccount.id, p.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: p.id
    principalType: 'ServicePrincipal'
  }
}]

resource blobDelegatorAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for p in principals: {
  name: guid(storageAccount.id, p.id, storageBlobDelegatorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDelegatorRoleId)
    principalId: p.id
    principalType: 'ServicePrincipal'
  }
}]

resource queueDataContributorAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for p in principals: {
  name: guid(storageAccount.id, p.id, storageQueueDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: p.id
    principalType: 'ServicePrincipal'
  }
}]

output storageAccountName string = storageAccount.name
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
output queueEndpoint string = storageAccount.properties.primaryEndpoints.queue
