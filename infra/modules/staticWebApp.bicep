// The React app (#37): Azure Static Web Apps, Free plan. Static files only: the app calls the API Function app
// directly (Free has no linked backends) and signs users in with MSAL, not SWA's built-in auth (which on Free would
// let any Microsoft account in and gives no API token). Deployed to by deploy/ui.sh.

@description('Region: the Free plan isn\'t offered in UK South, so the nearest one that has it.')
param location string = 'westeurope'

@description('Resource tags.')
param tags object

@description('Static Web App name.')
param name string

resource site 'Microsoft.Web/staticSites@2023-12-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    // Deployed by deploy/ui.sh with the SWA CLI, not from a linked repository.
    allowConfigFileUpdates: true
    stagingEnvironmentPolicy: 'Disabled'
  }
}

output name string = site.name
output defaultHostname string = site.properties.defaultHostname
