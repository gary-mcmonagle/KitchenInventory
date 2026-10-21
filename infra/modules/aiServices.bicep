// Azure AI Services (Foundry) for photo recognition (spike #41; the API uses it from #58): a vision model that names what's in a photo, plus
// Document Intelligence (Read) for the OCR comparison, in one multi-service account. No keys: local auth disabled.
// Models are Standard deployments, so images are processed in this region (UK South), not globally.

@description('Azure region.')
param location string

@description('Tags.')
param tags object

@description('Account name (globally unique; also its subdomain).')
param name string

@description('Object ID of a person allowed to call the models (Cognitive Services User), e.g. for the spike scripts. Empty skips it.')
param userPrincipalId string = ''

@description('Managed identities that call the models (Cognitive Services User): the API app, for photo recognition (#58).')
param appPrincipalIds array = []

@description('Thousands of tokens per minute for gpt-4.1-mini (left from spike #41; unused).')
@minValue(1)
param capacity int = 10

@description('Thousands of tokens per minute for gpt-4o, shared by recipes, adjustments, photo identification and generic names. 50 is the subscription\'s Standard quota in UK South (Gary, 2026-10-21); billing is per token, so this is only a speed limit.')
@minValue(1)
param gpt4oCapacity int = 50

var cognitiveServicesUser = 'a97b65f3-24c7-4388-baec-2e87135dc908'

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: name
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: name
    disableLocalAuth: true // Entra ID only
    publicNetworkAccess: 'Enabled'
  }
}

resource gpt4o 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: 'gpt-4o'
  sku: {
    name: 'Standard' // regional: processed in this region
    capacity: gpt4oCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-11-20'
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

resource gpt41mini 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: 'gpt-4.1-mini'
  sku: {
    name: 'Standard'
    capacity: capacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4.1-mini'
      version: '2025-04-14'
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  dependsOn: [gpt4o] // deployments on one account are created one at a time
}

resource apps 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in appPrincipalIds: {
    scope: account
    name: guid(account.id, principalId, cognitiveServicesUser)
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUser)
    }
  }
]

resource user 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(userPrincipalId)) {
  scope: account
  name: guid(account.id, userPrincipalId, cognitiveServicesUser)
  properties: {
    principalId: userPrincipalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUser)
  }
}

output name string = account.name
output endpoint string = account.properties.endpoint
