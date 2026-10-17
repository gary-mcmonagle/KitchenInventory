@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Log Analytics workspace name.')
param workspaceName string

@description('Application Insights (workspace-based) name.')
param appInsightsName string

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    // Entra ID (managed identity) ingestion only: no instrumentation-key-only telemetry.
    DisableLocalAuth: true
  }
}

output appInsightsName string = appInsights.name
output workspaceId string = workspace.id
