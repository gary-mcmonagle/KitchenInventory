// Azure SignalR Service for the live scan feed (#54, spike #38): Free tier, Serverless mode, no access keys.
// The Function apps only send (REST, transient transport) and the React app connects through the API's negotiate.

@description('Azure region.')
param location string

@description('Tags for the resource.')
param tags object

@description('SignalR Service name (globally unique).')
param name string

@description('Browser origins allowed to connect: the Static Web App and the local dev server.')
param allowedOrigins array

resource signalR 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: name
  location: location
  tags: tags
  kind: 'SignalR'
  sku: {
    name: 'Free_F1'
    capacity: 1
  }
  properties: {
    features: [
      { flag: 'ServiceMode', value: 'Serverless' }
      { flag: 'EnableConnectivityLogs', value: 'true' }
    ]
    cors: {
      allowedOrigins: allowedOrigins
    }
    disableLocalAuth: true // managed identity only: no access keys or connection strings
    publicNetworkAccess: 'Enabled'
  }
}

output name string = signalR.name
output serviceUri string = 'https://${signalR.properties.hostName}'
