@description('Location for the AI Search service. Defaults to eastus — the free-tier SKU was only available there in this subscription at initial provisioning, so the search service lives in a different region than the rest of the resource group.')
param location string = 'eastus'

@description('Exact Search service name — must match the existing resource name so this deployment adopts it instead of creating a duplicate (and the Free tier only allows one service per subscription anyway).')
param searchServiceName string

@description('Principal ID of the Worker managed identity — indexes documents and manages the index schema.')
param workerPrincipalId string

@description('Principal ID of the Api managed identity — read-only queries via GET /api/search.')
param apiPrincipalId string

resource search 'Microsoft.Search/searchServices@2024-06-01-preview' = {
  name: searchServiceName
  location: location
  sku: {
    name: 'free'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    publicNetworkAccess: 'enabled'
    disableLocalAuth: true // AAD/RBAC only — no admin/query api-key auth path
  }
}

var searchServiceContributorRoleId = '7ca78c08-252a-4471-8644-bb5ff32d4ba0' // manage the index schema itself
var searchIndexDataContributorRoleId = '8ebe5a00-799e-43f5-93ac-243d3dce84a7' // read/write documents
var searchIndexDataReaderRoleId = '1407120a-92aa-4202-b7e9-c0e197c71c8f' // read-only document queries

resource workerServiceContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, workerPrincipalId, searchServiceContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributorRoleId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource workerIndexDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, workerPrincipalId, searchIndexDataContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataContributorRoleId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiIndexDataReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, apiPrincipalId, searchIndexDataReaderRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataReaderRoleId)
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output searchEndpoint string = 'https://${search.name}.search.windows.net'
