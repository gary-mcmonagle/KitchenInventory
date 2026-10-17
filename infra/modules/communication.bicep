// Azure Communication Services Email on an Azure-managed domain (spike #39): sends the offline alert (#44) and,
// later, the daily digest. No keys are used: the API Function app sends with its managed identity
// (communicationAccess.bicep). The sender address is fixed by Azure (DoNotReply@<random>.azurecomm.net).

@description('Resource tags.')
param tags object

@description('Email Communication Service name.')
param emailServiceName string

@description('Communication Services name (globally unique: it is the endpoint host name).')
param communicationServiceName string

@description('Where email data is stored at rest.')
param dataLocation string = 'UK'

resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: emailServiceName
  location: 'global'
  tags: tags
  properties: {
    dataLocation: dataLocation
  }
}

resource managedDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  tags: tags
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}

resource communicationService 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: communicationServiceName
  location: 'global'
  tags: tags
  properties: {
    dataLocation: dataLocation
    linkedDomains: [
      managedDomain.id
    ]
  }
}

output communicationServiceName string = communicationService.name
output endpoint string = 'https://${communicationService.properties.hostName}'
output senderAddress string = 'DoNotReply@${managedDomain.properties.mailFromSenderDomain}'
