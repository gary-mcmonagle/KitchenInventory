// Read-only access for the person (and Claude Code, signed in as them) who verifies cards against the live system.
@description('Object ID of the verifier (a user).')
param principalId string

@description('Log Analytics workspace name (Application Insights data lives here).')
param workspaceName string

@description('Cosmos DB account name.')
param cosmosAccountName string

@description('Cosmos DB database name.')
param databaseName string

// Built-in role IDs.
var reader = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
var logAnalyticsReader = '73c42c96-874c-492b-b04d-ab87d138a893'
var cosmosDataReader = '00000000-0000-0000-0000-000000000001' // Cosmos DB Built-in Data Reader (data plane)

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: cosmosAccountName
}

resource rgReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, principalId, reader)
  properties: {
    principalId: principalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', reader)
  }
}

resource logsReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: workspace
  name: guid(workspace.id, principalId, logAnalyticsReader)
  properties: {
    principalId: principalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', logAnalyticsReader)
  }
}

resource cosmosReader 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, databaseName, principalId, cosmosDataReader)
  properties: {
    principalId: principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataReader}'
    scope: '${cosmos.id}/dbs/${databaseName}'
  }
}
