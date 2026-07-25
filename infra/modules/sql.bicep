@description('Location for the SQL server.')
param location string

@description('Exact SQL logical server name (must be globally unique).')
param sqlServerName string

@description('Database name.')
param databaseName string = 'CogniLens'

@description('Name of the Api managed identity (used as the contained DB user login name).')
param apiIdentityName string

@description('Name of the Worker managed identity (used as the contained DB user login name).')
param workerIdentityName string

// Dedicated identity for the SQL AAD admin + the user-bootstrap script below. Kept separate from
// the Api/Worker identities so neither app identity is ever a SQL admin — least privilege even
// for the bootstrap path.
resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: '${sqlServerName}-deploy-mi'
  location: location
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  properties: {
    // No SQL-auth login/password anywhere — AAD-only, per the no-secrets non-negotiable.
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: '${sqlServerName}-deploy-mi'
      sid: deployIdentity.properties.principalId
      tenantId: subscription().tenantId
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// No VNet integration for this project (cost/complexity not justified at demo scale) — Container
// Apps have no static egress IP, so the standard non-VNet workaround is trusting all Azure-origin
// traffic. This is broader than a real production network boundary would allow; flagged as a
// deliberate scope cut, not an oversight.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    // Azure SQL free offer: 100K vCore-seconds + 32GB/month, serverless, auto-pause.
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368 // 32 GB
  }
}

// Bootstraps the AAD contained database users for the Api/Worker managed identities. There is no
// ARM/Bicep-native resource for "CREATE USER ... FROM EXTERNAL PROVIDER" — this is the standard
// workaround (see Microsoft's own create-sql-user-and-schema quickstart). Tradeoff: it spins up a
// short-lived Container Instance during deployment (a few minutes, negligible cost) instead of
// requiring a manual one-time SQL step; picked automation because the Definition of Done requires
// zero manual steps beyond the deploy approval.
resource bootstrapUsers 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: '${sqlServerName}-bootstrap-users'
  location: location
  kind: 'AzureCLI'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${deployIdentity.id}': {}
    }
  }
  properties: {
    azCliVersion: '2.65.0'
    retentionInterval: 'PT1H'
    timeout: 'PT15M'
    cleanupPreference: 'OnSuccess'
    environmentVariables: [
      { name: 'SERVER', value: '${sqlServer.name}.database.windows.net' }
      { name: 'DATABASE', value: database.name }
      { name: 'API_IDENTITY_NAME', value: apiIdentityName }
      { name: 'WORKER_IDENTITY_NAME', value: workerIdentityName }
    ]
    scriptContent: '''
      set -e
      apt-get update -qq && apt-get install -y -qq curl gnupg >/dev/null
      curl -sSL https://packages.microsoft.com/keys/microsoft.asc | tee /etc/apt/trusted.gpg.d/microsoft.asc >/dev/null
      curl -sSL https://packages.microsoft.com/config/ubuntu/22.04/prod.list | tee /etc/apt/sources.list.d/mssql-release.list >/dev/null
      apt-get update -qq
      ACCEPT_EULA=Y apt-get install -y -qq mssql-tools18 unixodbc-dev >/dev/null
      export PATH="$PATH:/opt/mssql-tools18/bin"

      TOKEN=$(az account get-access-token --resource https://database.windows.net --query accessToken -o tsv)

      cat <<SQL > bootstrap.sql
DECLARE @apiUser sysname = N'$API_IDENTITY_NAME';
DECLARE @workerUser sysname = N'$WORKER_IDENTITY_NAME';

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @apiUser)
BEGIN
  EXEC('CREATE USER [' + @apiUser + '] FROM EXTERNAL PROVIDER');
END
EXEC('ALTER ROLE db_datareader ADD MEMBER [' + @apiUser + ']');
EXEC('ALTER ROLE db_datawriter ADD MEMBER [' + @apiUser + ']');

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @workerUser)
BEGIN
  EXEC('CREATE USER [' + @workerUser + '] FROM EXTERNAL PROVIDER');
END
EXEC('ALTER ROLE db_datareader ADD MEMBER [' + @workerUser + ']');
EXEC('ALTER ROLE db_datawriter ADD MEMBER [' + @workerUser + ']');
SQL

      sqlcmd -S "tcp:$SERVER,1433" -d "$DATABASE" -G -l 30 --authentication-method=ActiveDirectoryAccessToken --access-token "$TOKEN" -i bootstrap.sql
    '''
  }
  dependsOn: [
    allowAzureServices
  ]
}

output sqlServerFqdn string = '${sqlServer.name}.database.windows.net'
output databaseName string = database.name
