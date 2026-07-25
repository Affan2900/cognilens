@description('Location for the managed identities.')
param location string

@description('Name prefix for the environment, e.g. cognilens-dev.')
param namePrefix string

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${namePrefix}-api-mi'
  location: location
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${namePrefix}-worker-mi'
  location: location
}

output apiIdentityId string = apiIdentity.id
output apiIdentityPrincipalId string = apiIdentity.properties.principalId
output apiIdentityClientId string = apiIdentity.properties.clientId
output workerIdentityId string = workerIdentity.id
output workerIdentityPrincipalId string = workerIdentity.properties.principalId
output workerIdentityClientId string = workerIdentity.properties.clientId
