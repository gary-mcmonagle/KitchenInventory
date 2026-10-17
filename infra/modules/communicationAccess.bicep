// Lets the API Function app's managed identity send email through Communication Services.
// Microsoft documents no smaller built-in role for sending, and custom roles are reported not to work for ACS, so this
// is the built-in "Communication and Email Service Owner", scoped to the one Communication Services resource and
// assigned directly to the identity (assignments through groups are reported not to work for managed identities).

@description('Communication Services name (in this resource group).')
param communicationServiceName string

@description('Object ID of the API Function app\'s managed identity.')
param principalId string

var communicationAndEmailServiceOwner = '09976791-48a7-449e-bb21-39d1a415f350'

resource communicationService 'Microsoft.Communication/communicationServices@2023-04-01' existing = {
  name: communicationServiceName
}

resource sendEmail 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: communicationService
  name: guid(communicationService.id, principalId, communicationAndEmailServiceOwner)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', communicationAndEmailServiceOwner)
  }
}
