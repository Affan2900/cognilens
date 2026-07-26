@description('Location for the Container Apps environment and apps.')
param location string

@description('Name prefix for the environment, e.g. cognilens-dev.')
param namePrefix string

@description('Log Analytics workspace resource ID (for the Container Apps environment log destination).')
param logAnalyticsWorkspaceId string

@description('Log Analytics workspace customer ID (workspace GUID).')
param logAnalyticsCustomerId string

@description('Application Insights connection string.')
@secure()
param appInsightsConnectionString string

@description('Resource ID of the Api managed identity.')
param apiIdentityId string
@description('Client ID of the Api managed identity — pinned explicitly in the SQL AAD connection string so DefaultAzureCredential is unambiguous.')
param apiIdentityClientId string

@description('Resource ID of the Worker managed identity.')
param workerIdentityId string
@description('Client ID of the Worker managed identity.')
param workerIdentityClientId string

@description('Full Api container image reference, e.g. ghcr.io/owner/cognilens-api:latest.')
param apiImage string

@description('Full Worker container image reference, e.g. ghcr.io/owner/cognilens-worker:latest.')
param workerImage string

@description('SQL server FQDN (from sql.bicep).')
param sqlServerFqdn string
@description('SQL database name (from sql.bicep).')
param sqlDatabaseName string

@description('Storage account name (from storage.bicep) — used by the Worker queue-length scale rule.')
param storageAccountName string
@description('Blob service endpoint (from storage.bicep).')
param storageBlobEndpoint string
@description('Queue service endpoint (from storage.bicep).')
param storageQueueEndpoint string
param containerName string = 'call-audio'
param queueName string = 'analyze-jobs'
param poisonQueueName string = 'analyze-jobs-poison'

@description('Speech endpoint (from ai.bicep).')
param speechEndpoint string
@description('OpenAI endpoint (from ai.bicep).')
param openAiEndpoint string
@description('OpenAI chat deployment name (from ai.bicep).')
param chatDeploymentName string
@description('OpenAI embedding deployment name (from ai.bicep).')
param embeddingDeploymentName string

@description('AI Search endpoint (from search.bicep).')
param searchEndpoint string
@description('AI Search index name — created at runtime by the Worker, not provisioned in Bicep.')
param searchIndexName string = 'transcript-chunks'

@description('Public origin of the Blazor frontend (from web.bicep), e.g. https://witty-sand-0abc.azurestaticapps.net. Becomes the Api\'s single allowed CORS origin. Until this existed, Cors:AllowedOrigins was set only in appsettings.Development.json, so the deployed Api fell through to the empty-array default in Program.cs and rejected every browser origin.')
param webOrigin string

@description('API keys accepted by the Api auth middleware, comma-separated. Generated outside Bicep (e.g. `openssl rand`) and passed in as a secure param — never committed.')
@secure()
param apiKeys string

@description('Max replicas for the Api app.')
param apiMaxReplicas int = 2
@description('Max replicas for the Worker app.')
param workerMaxReplicas int = 5

@description('Revision suffix for this deploy of the Api app, e.g. the short git SHA — lets cd.yml address the exact new revision by name for smoke testing and canary traffic shifting. Empty on first deploy.')
param apiRevisionSuffix string = ''

@description('Name of the Api revision that should keep 100% traffic at the moment this deploy runs (the previous "stable" revision, queried by cd.yml before deploying). Empty means no prior revision exists yet (bootstrap) or the caller wants the newest revision to take traffic immediately (local/manual deploys) — both fall back to latestRevision: true.')
param apiStableRevisionName string = ''

// listKeys() is a deploy-time ARM function call, not a stored secret — the shared key never lands
// in source control, app config, or an env var. It is required because Microsoft.App/managedEnvironments'
// classic Log Analytics destination has no Managed Identity-based auth option in the current API version.
var logAnalyticsSharedKey = listKeys(logAnalyticsWorkspaceId, '2023-09-01').primarySharedKey

resource env 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${namePrefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsSharedKey
      }
    }
  }
}

// AAD-only connection string — Authentication=Active Directory Default resolves to the container's
// attached user-assigned identity via DefaultAzureCredential, pinned by client ID since a UAI-only
// (no system-assigned) identity needs disambiguation. No password anywhere.
var sqlConnectionStringTemplate = 'Server=tcp:{0},1433;Initial Catalog={1};Authentication=Active Directory Default;User Id={2};Encrypt=True;TrustServerCertificate=False;'

resource apiApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-api'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      // Multiple (not Single) so a deploy creates a new revision alongside the running one
      // instead of replacing it outright — required for cd.yml's canary traffic shift and
      // instant rollback (shift weight back to the old revision) instead of a hard cutover.
      activeRevisionsMode: 'Multiple'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        // If cd.yml supplied the previous stable revision's name, pin 100% traffic there so
        // this deploy's new revision starts at 0% and only moves on the pipeline's explicit
        // `az containerapp ingress traffic set` canary steps. Otherwise (bootstrap, or a
        // manual/local deploy) fall back to routing to whatever revision is newest.
        traffic: !empty(apiStableRevisionName)
          ? [
              { revisionName: apiStableRevisionName, weight: 100 }
            ]
          : [
              { latestRevision: true, weight: 100 }
            ]
      }
      secrets: [
        { name: 'api-keys', value: apiKeys }
      ]
    }
    template: {
      revisionSuffix: apiRevisionSuffix
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'AZURE_CLIENT_ID', value: apiIdentityClientId }
            { name: 'ConnectionStrings__CogniLensDb', value: format(sqlConnectionStringTemplate, sqlServerFqdn, sqlDatabaseName, apiIdentityClientId) }
            { name: 'Storage__BlobServiceUri', value: storageBlobEndpoint }
            { name: 'Storage__QueueServiceUri', value: storageQueueEndpoint }
            { name: 'Storage__ContainerName', value: containerName }
            { name: 'Storage__QueueName', value: queueName }
            { name: 'Storage__PoisonQueueName', value: poisonQueueName }
            { name: 'AzureAi__Speech__Endpoint', value: speechEndpoint }
            { name: 'AzureAi__OpenAi__Endpoint', value: openAiEndpoint }
            { name: 'AzureAi__OpenAi__ChatDeploymentName', value: chatDeploymentName }
            { name: 'AzureAi__OpenAi__EmbeddingDeploymentName', value: embeddingDeploymentName }
            { name: 'AzureAi__Search__Endpoint', value: searchEndpoint }
            { name: 'AzureAi__Search__IndexName', value: searchIndexName }
            // Bound to Cors:AllowedOrigins[0] in Program.cs. The __0 suffix is how ASP.NET Core's
            // environment-variable provider expresses an array element, which is what
            // .Get<string[]>() on that section needs to see.
            { name: 'Cors__AllowedOrigins__0', value: webOrigin }
            { name: 'ApiKeys', secretRef: 'api-keys' }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: apiMaxReplicas
        rules: [
          {
            name: 'http-scale'
            http: {
              metadata: {
                concurrentRequests: '20'
              }
            }
          }
        ]
      }
    }
  }
}

resource workerApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${namePrefix}-worker'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workerIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      // Internal-only ingress: gives ACA an HTTP target for revision health probes without exposing
      // the Worker publicly — it has no endpoints that need the auth/rate-limit treatment (task #26
      // covers the Api's public /analyze and /search endpoints only).
      ingress: {
        external: false
        targetPort: 8081
        transport: 'auto'
      }
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: workerImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_URLS', value: 'http://+:8081' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'AZURE_CLIENT_ID', value: workerIdentityClientId }
            { name: 'ConnectionStrings__CogniLensDb', value: format(sqlConnectionStringTemplate, sqlServerFqdn, sqlDatabaseName, workerIdentityClientId) }
            { name: 'Storage__BlobServiceUri', value: storageBlobEndpoint }
            { name: 'Storage__QueueServiceUri', value: storageQueueEndpoint }
            { name: 'Storage__ContainerName', value: containerName }
            { name: 'Storage__QueueName', value: queueName }
            { name: 'Storage__PoisonQueueName', value: poisonQueueName }
            { name: 'AzureAi__Speech__Endpoint', value: speechEndpoint }
            { name: 'AzureAi__OpenAi__Endpoint', value: openAiEndpoint }
            { name: 'AzureAi__OpenAi__ChatDeploymentName', value: chatDeploymentName }
            { name: 'AzureAi__OpenAi__EmbeddingDeploymentName', value: embeddingDeploymentName }
            { name: 'AzureAi__Search__Endpoint', value: searchEndpoint }
            { name: 'AzureAi__Search__IndexName', value: searchIndexName }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: workerMaxReplicas
        rules: [
          {
            // KEDA azure-queue scaler, authenticated via the Worker's own managed identity (the
            // `identity` property below) instead of a storage connection-string secret — the Worker
            // MI already holds Storage Queue Data Contributor from storage.bicep, which is sufficient
            // for KEDA to poll queue length. This is what actually wakes the Worker from
            // minReplicas=0 when a job is enqueued, since it has no inbound HTTP traffic of its own.
            name: 'queue-scale'
            custom: {
              type: 'azure-queue'
              metadata: {
                accountName: storageAccountName
                queueName: queueName
                queueLength: '1'
              }
              identity: workerIdentityId
            }
          }
        ]
      }
    }
  }
}

output apiFqdn string = apiApp.properties.configuration.ingress.fqdn
output apiUrl string = 'https://${apiApp.properties.configuration.ingress.fqdn}'
