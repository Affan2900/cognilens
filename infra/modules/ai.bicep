@description('Location for the Speech and OpenAI accounts.')
param location string = 'eastus2'

@description('Exact Speech account name — must match the existing resource name so this deployment adopts it instead of creating a duplicate.')
param speechAccountName string

@description('Exact OpenAI account name — must match the existing resource name so this deployment adopts it instead of creating a duplicate.')
param openAiAccountName string

@description('Principal ID of the Worker managed identity — the only caller of Speech/OpenAI.')
param workerPrincipalId string

@description('Azure OpenAI chat deployment capacity, in thousands of tokens-per-minute units.')
param chatDeploymentCapacity int = 10

@description('Azure OpenAI embedding deployment capacity, in thousands of tokens-per-minute units.')
param embeddingDeploymentCapacity int = 10

resource speech 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: speechAccountName
  location: location
  kind: 'SpeechServices'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: speechAccountName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true // AAD/RBAC only — no api-key auth path, per the no-keys non-negotiable
  }
}

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: openAiAccountName
  location: location
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: openAiAccountName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: 'gpt-5-mini'
  sku: {
    name: 'GlobalStandard'
    capacity: chatDeploymentCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5-mini'
      version: '2025-08-07'
    }
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: 'text-embedding-3-small'
  sku: {
    name: 'GlobalStandard'
    capacity: embeddingDeploymentCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-small'
      version: '1'
    }
  }
  dependsOn: [
    chatDeployment // Cognitive Services deployments serialize; avoids a rare concurrent-write 429 on the account
  ]
}

var cognitiveServicesSpeechUserRoleId = 'f2dc8367-1007-4938-bd23-fe263f013447'
var cognitiveServicesOpenAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource speechRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(speech.id, workerPrincipalId, cognitiveServicesSpeechUserRoleId)
  scope: speech
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesSpeechUserRoleId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource openAiRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAi.id, workerPrincipalId, cognitiveServicesOpenAiUserRoleId)
  scope: openAi
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUserRoleId)
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output speechEndpoint string = speech.properties.endpoint
output openAiEndpoint string = openAi.properties.endpoint
output chatDeploymentName string = chatDeployment.name
output embeddingDeploymentName string = embeddingDeployment.name
