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

@description('Client (application) ID of the Api managed identity — converted to a binary SID so the contained DB user can be created without a Microsoft Graph lookup.')
param apiIdentityClientId string

@description('Client (application) ID of the Worker managed identity — see apiIdentityClientId.')
param workerIdentityClientId string

@description('Client (application) ID of the CI/CD principal that cd.yml authenticates as via OIDC. It gets a contained DB user with db_ddladmin so the pipeline can apply EF migrations. Azure SQL permits exactly one AAD admin (the deploy MI), so the pipeline cannot be made an admin as well — this is the least-privilege way to let it change the schema. Empty disables the user, for local/manual deploys that are not applying migrations.')
param cicdPrincipalClientId string = ''

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
      
      principalType: 'Application'
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
      { name: 'API_IDENTITY_CLIENT_ID', value: apiIdentityClientId }
      { name: 'WORKER_IDENTITY_CLIENT_ID', value: workerIdentityClientId }
      { name: 'DEPLOY_CLIENT_ID', value: deployIdentity.properties.clientId }
      { name: 'CICD_CLIENT_ID', value: cicdPrincipalClientId }
    ]
    scriptContent: '''
      set -e

      # mssql-tools is NOT installed from packages.microsoft.com here: the AzureCLI
      # deployment-script image is Azure Linux, which has no apt-get, so the Debian/Ubuntu repo
      # route fails outright. go-sqlcmd ships a single statically-linked binary instead, so this
      # depends on nothing in the base image beyond curl.
      curl -sSL -o sqlcmd.tar.bz2 https://github.com/microsoft/go-sqlcmd/releases/download/v1.10.0/sqlcmd-linux-amd64.tar.bz2
      # `tar -j` shells out to a bzip2 binary that isn't guaranteed to be present; Python's
      # tarfile decompresses bz2 in-process, so it works even on a stripped-down image.
      tar -xjf sqlcmd.tar.bz2 2>/dev/null || python3 -c "import tarfile; tarfile.open('sqlcmd.tar.bz2').extractall('.')"
      chmod +x ./sqlcmd

      # CREATE USER ... FROM EXTERNAL PROVIDER is deliberately NOT used here. That form makes the
      # SQL server call Microsoft Graph to resolve the identity name, which requires the server to
      # have a managed identity holding the Directory Readers role — granting that needs Privileged
      # Role Administrator in the tenant, which isn't available on this (university-managed) tenant.
      # Creating the user from the identity's client ID converted to a binary SID is the documented
      # equivalent and needs no directory permissions at all.
      cat <<SQL > bootstrap.sql
DECLARE @apiUser sysname = N'$API_IDENTITY_NAME';
DECLARE @workerUser sysname = N'$WORKER_IDENTITY_NAME';
DECLARE @apiSid varbinary(16) = CONVERT(varbinary(16), CAST(N'$API_IDENTITY_CLIENT_ID' AS uniqueidentifier));
DECLARE @workerSid varbinary(16) = CONVERT(varbinary(16), CAST(N'$WORKER_IDENTITY_CLIENT_ID' AS uniqueidentifier));
DECLARE @cmd nvarchar(max);

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @apiUser)
BEGIN
  SET @cmd = N'CREATE USER [' + @apiUser + N'] WITH SID = 0x' + CONVERT(varchar(100), @apiSid, 2) + N', TYPE = E;';
  EXEC(@cmd);
END
EXEC('ALTER ROLE db_datareader ADD MEMBER [' + @apiUser + ']');
EXEC('ALTER ROLE db_datawriter ADD MEMBER [' + @apiUser + ']');

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @workerUser)
BEGIN
  SET @cmd = N'CREATE USER [' + @workerUser + N'] WITH SID = 0x' + CONVERT(varchar(100), @workerSid, 2) + N', TYPE = E;';
  EXEC(@cmd);
END
EXEC('ALTER ROLE db_datareader ADD MEMBER [' + @workerUser + ']');
EXEC('ALTER ROLE db_datawriter ADD MEMBER [' + @workerUser + ']');
SQL

      # The CI/CD principal applies EF migrations from cd.yml, so it needs DDL rights that no
      # runtime identity should ever have. db_ddladmin covers CREATE/ALTER/DROP; the data roles
      # are for the SeedRubrics migration, which inserts rows. Deliberately not db_owner: the
      # pipeline has no business granting permissions or dropping users.
      if [ -n "$CICD_CLIENT_ID" ]; then
        cat <<SQL >> bootstrap.sql
DECLARE @cicdUser sysname = N'cognilens-cicd';
DECLARE @cicdSid varbinary(16) = CONVERT(varbinary(16), CAST(N'$CICD_CLIENT_ID' AS uniqueidentifier));
DECLARE @cicdCmd nvarchar(max);

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @cicdUser)
BEGIN
  SET @cicdCmd = N'CREATE USER [' + @cicdUser + N'] WITH SID = 0x' + CONVERT(varchar(100), @cicdSid, 2) + N', TYPE = E;';
  EXEC(@cicdCmd);
END
EXEC('ALTER ROLE db_ddladmin ADD MEMBER [' + @cicdUser + ']');
EXEC('ALTER ROLE db_datareader ADD MEMBER [' + @cicdUser + ']');
EXEC('ALTER ROLE db_datawriter ADD MEMBER [' + @cicdUser + ']');
SQL
      fi

      # -U carries the user-assigned identity's client ID (how go-sqlcmd disambiguates which MI to
      # use); -b makes a failed T-SQL batch exit non-zero, otherwise sqlcmd returns 0 on SQL errors
      # and a broken bootstrap would silently report success.
      ./sqlcmd -S "tcp:$SERVER,1433" -d "$DATABASE" -l 30 -b \
        --authentication-method=ActiveDirectoryManagedIdentity -U "$DEPLOY_CLIENT_ID" \
        -i bootstrap.sql
    '''
  }
  dependsOn: [
    allowAzureServices
  ]
}

output sqlServerFqdn string = '${sqlServer.name}.database.windows.net'
output databaseName string = database.name
