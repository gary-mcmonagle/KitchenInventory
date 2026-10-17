// Cupboard Tracker: baseline infrastructure. Single environment (the live household system).
// Deployed at subscription scope so the resource group is part of the template.
targetScope = 'subscription'

@description('Azure region for all resources.')
param location string = 'uksouth'

@description('Name of the resource group that holds everything for Cupboard Tracker.')
param resourceGroupName string = 'rg-cupboard-tracker'

@description('Short name used in resource names (lowercase letters only).')
@minLength(3)
@maxLength(10)
param appName string = 'cupboard'

@description('Commit SHA currently deployed to the ingest Function app. deploy/infra.sh passes the live value so a re-run never resets it.')
param ingestCommitSha string = 'not-deployed'

@description('Commit SHA currently deployed to the API Function app. deploy/infra.sh passes the live value so a re-run never resets it.')
param apiCommitSha string = 'not-deployed'

@description('Maximum Flex Consumption instances per Function app (a cost guard; household load is tiny).')
@minValue(1)
@maxValue(1000)
param functionMaximumInstanceCount int = 10

@description('Flex Consumption instance memory in MB.')
@allowed([512, 2048, 4096])
param functionInstanceMemoryMB int = 512

var tags = {
  app: 'cupboard-tracker'
  environment: 'live'
  managedBy: 'bicep'
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// Short, stable suffix for globally unique names (storage, Cosmos, Function apps).
var suffix = take(uniqueString(subscription().id, resourceGroupName), 6)

module monitoring 'modules/monitoring.bicep' = {
  scope: rg
  name: 'monitoring'
  params: {
    location: location
    tags: tags
    workspaceName: 'log-${appName}-tracker'
    appInsightsName: 'appi-${appName}-tracker'
  }
}

module cosmos 'modules/cosmos.bicep' = {
  scope: rg
  name: 'cosmos'
  params: {
    location: location
    tags: tags
    accountName: 'cosmos-${appName}-${suffix}'
    databaseName: 'cupboard'
  }
}

module ingest 'modules/functionApp.bicep' = {
  scope: rg
  name: 'func-ingest'
  params: {
    location: location
    tags: union(tags, { component: 'ingest' })
    functionAppName: 'func-${appName}-ingest-${suffix}'
    planName: 'plan-${appName}-ingest'
    storageAccountName: 'st${appName}ing${suffix}'
    appInsightsName: monitoring.outputs.appInsightsName
    maximumInstanceCount: functionMaximumInstanceCount
    instanceMemoryMB: functionInstanceMemoryMB
    appSettings: {
      COMMIT_SHA: ingestCommitSha
      COSMOS_ENDPOINT: cosmos.outputs.endpoint
      COSMOS_DATABASE: cosmos.outputs.databaseName
    }
  }
}

module api 'modules/functionApp.bicep' = {
  scope: rg
  name: 'func-api'
  params: {
    location: location
    tags: union(tags, { component: 'api' })
    functionAppName: 'func-${appName}-api-${suffix}'
    planName: 'plan-${appName}-api'
    storageAccountName: 'st${appName}api${suffix}'
    appInsightsName: monitoring.outputs.appInsightsName
    maximumInstanceCount: functionMaximumInstanceCount
    instanceMemoryMB: functionInstanceMemoryMB
    appSettings: {
      COMMIT_SHA: apiCommitSha
      COSMOS_ENDPOINT: cosmos.outputs.endpoint
      COSMOS_DATABASE: cosmos.outputs.databaseName
    }
  }
}

module cosmosDataAccess 'modules/cosmosDataAccess.bicep' = {
  scope: rg
  name: 'cosmos-data-access'
  params: {
    accountName: cosmos.outputs.accountName
    databaseName: cosmos.outputs.databaseName
    apps: [
      { name: ingest.outputs.name, principalId: ingest.outputs.principalId }
      { name: api.outputs.name, principalId: api.outputs.principalId }
    ]
  }
}

output resourceGroupName string = rg.name
output ingestFunctionAppName string = ingest.outputs.name
output ingestFunctionAppHostname string = ingest.outputs.defaultHostname
output apiFunctionAppName string = api.outputs.name
output apiFunctionAppHostname string = api.outputs.defaultHostname
output cosmosEndpoint string = cosmos.outputs.endpoint
output cosmosAccountName string = cosmos.outputs.accountName
output cosmosDatabaseName string = cosmos.outputs.databaseName
output appInsightsName string = monitoring.outputs.appInsightsName
