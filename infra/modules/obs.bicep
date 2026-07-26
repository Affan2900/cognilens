@description('Location for the observability resources.')
param location string

@description('Name prefix for the environment, e.g. cognilens-dev.')
param namePrefix string

@description('Log retention in days. Kept short to stay inside the free/low-cost tier.')
param retentionInDays int = 30

@description('Hard daily ingestion cap in GB. Ingestion stops for the rest of the UTC day once hit.')
param dailyQuotaGb string = '0.2'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-law'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: retentionInDays
    // PerGB2018 bills roughly $2.76/GB past Azure Monitor's 5 GB/month free allowance, and until
    // Phase 5 nothing actually exported to this workspace, so the bill was structurally zero
    // regardless of what the app did. Wiring up OpenTelemetry removes that accident, which makes
    // an explicit ceiling necessary rather than optional: a retry storm or a chatty log level
    // could otherwise run the meter unbounded between monthly budget-alert evaluations.
    //
    // 0.2 GB/day is ~6 GB/month at the worst case but realistically far under it, since ingestion
    // only happens while a replica is awake and both apps scale to zero. Hitting the cap drops
    // telemetry until 00:00 UTC — the deliberate trade: losing visibility is recoverable, an
    // unbounded bill on a personal subscription is not.
    workspaceCapping: {
      dailyQuotaGb: json(dailyQuotaGb)
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-appi'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
  }
}

output logAnalyticsWorkspaceId string = logAnalytics.id
output logAnalyticsCustomerId string = logAnalytics.properties.customerId
output appInsightsConnectionString string = appInsights.properties.ConnectionString
