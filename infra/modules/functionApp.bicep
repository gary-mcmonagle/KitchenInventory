@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Function app name (globally unique).')
param functionAppName string

@description('Flex Consumption plan name (one app per plan).')
param planName string

@description('Storage account for the Functions host and the deployment package (3-24 lowercase letters and digits).')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Application Insights name (in this resource group).')
param appInsightsName string

@description('Maximum number of instances.')
param maximumInstanceCount int

@description('Instance memory in MB.')
param instanceMemoryMB int

@description('Extra app settings (non-secret).')
param appSettings object = {}

@description('Browser origins allowed to call this app (CORS). Empty: no CORS rules.')
param corsAllowedOrigins array = []

@description('Give the app Storage Queue Data Contributor on its own storage account (queue triggers and sends).')
param queueDataContributor bool = false

var deploymentContainerName = 'app-package'

// Built-in Azure role IDs.
var storageBlobDataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var monitoringMetricsPublisher = '3913510d-42f4-4e42-8a64-420c390055eb'
var storageQueueDataContributor = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    // Managed identity only: no account keys, no anonymous blob access.
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    defaultToOAuthAuthentication: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentContainerName
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: union({
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          // Built from names (not a runtime reference) so what-if can compare it.
          value: 'https://${storageAccountName}.blob.${environment().suffixes.storage}/${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
  }, empty(corsAllowedOrigins) ? {} : {
    siteConfig: {
      cors: {
        allowedOrigins: corsAllowedOrigins
        supportCredentials: false
      }
    }
  })
}

resource settings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: functionApp
  name: 'appsettings'
  properties: union(
    {
      // Host storage over managed identity (no connection string).
      AzureWebJobsStorage__accountName: storage.name
      APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
      // Telemetry is sent with the managed identity (App Insights local auth is off).
      APPLICATIONINSIGHTS_AUTHENTICATION_STRING: 'Authorization=AAD'
    },
    appSettings
  )
}

resource blobOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionApp.id, storageBlobDataOwner)
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwner)
  }
}

resource queueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (queueDataContributor) {
  scope: storage
  name: guid(storage.id, functionApp.id, storageQueueDataContributor)
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributor)
  }
}

resource metricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: appInsights
  name: guid(appInsights.id, functionApp.id, monitoringMetricsPublisher)
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringMetricsPublisher)
  }
}

output name string = functionApp.name
output defaultHostname string = functionApp.properties.defaultHostName
output principalId string = functionApp.identity.principalId
