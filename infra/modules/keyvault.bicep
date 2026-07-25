@description('Location for the Key Vault.')
param location string

@description('Name prefix for the environment, e.g. cognilens-dev.')
param namePrefix string

@description('Tenant ID for RBAC authorization.')
param tenantId string

@description('Principal ID of the Api managed identity.')
param apiPrincipalId string

@description('Principal ID of the Worker managed identity.')
param workerPrincipalId string

// Nothing lives in here yet — every Azure resource is reached via Managed Identity RBAC.
// Scaffolded per the non-negotiable that Key Vault is reserved for third-party secrets
// with no MI path (e.g. a future webhook signing key).
//
// enablePurgeProtection is true
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${namePrefix}-kv'
  location: location
  properties: {
    tenantId: tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
  }
}

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

var principals = [
  apiPrincipalId
  workerPrincipalId
]

resource secretsUserAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in principals: {
  name: guid(keyVault.id, principalId, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

output keyVaultUri string = keyVault.properties.vaultUri
