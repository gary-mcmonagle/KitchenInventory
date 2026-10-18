// Lets the Function apps' managed identities send to the scan feed and negotiate client connections (#54):
// "SignalR Service Owner", the role the Functions docs name for identity-based SignalR connections.

@description('SignalR Service name (in this resource group).')
param signalRName string

@description('The Function apps that send scan feed events.')
param apps array

var signalRServiceOwner = '7e4f1700-ea5a-4f59-8f37-079cfe29dce3'

resource signalR 'Microsoft.SignalRService/signalR@2024-03-01' existing = {
  name: signalRName
}

resource owners 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for app in apps: {
    scope: signalR
    name: guid(signalR.id, app.principalId, signalRServiceOwner)
    properties: {
      principalId: app.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', signalRServiceOwner)
    }
  }
]
