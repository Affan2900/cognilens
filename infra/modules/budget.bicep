@description('Name prefix for the environment, e.g. cognilens-dev.')
param namePrefix string

@description('Monthly budget amount in the subscription\'s billing currency.')
param budgetAmount int = 10

@description('Email address notified at the 80%% and 100%% thresholds.')
param notificationEmail string

// utcNow() is only valid as a param default (evaluated once, at deployment start) — using it
// directly in a resource body is rejected by Bicep. First-of-month is required by the budgets API.
param budgetStartDate string = utcNow('yyyy-MM-01\'T\'00:00:00Z')
param budgetEndDate string = dateTimeAdd(utcNow('yyyy-MM-01\'T\'00:00:00Z'), 'P10Y', 'yyyy-MM-dd\'T\'00:00:00Z')

resource actionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${namePrefix}-budget-ag'
  location: 'global'
  properties: {
    groupShortName: 'cgbudget'
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: notificationEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: '${namePrefix}-budget'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
      endDate: budgetEndDate
    }
    notifications: {
      Actual_80Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: []
        contactRoles: []
        contactGroups: [
          actionGroup.id
        ]
      }
      Actual_100Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: []
        contactRoles: []
        contactGroups: [
          actionGroup.id
        ]
      }
      // Forecasted crossing 100% gets a heads-up before actual spend catches up, given the free-tier
      // resources here can flip to metered mid-month if a quota is exceeded.
      Forecasted_100Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: []
        contactRoles: []
        contactGroups: [
          actionGroup.id
        ]
      }
    }
  }
}
