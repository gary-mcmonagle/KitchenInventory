// Entra ID app registrations for the React app (SPA) and the API Function app (#36, spike #35).
// Deployed on its own by deploy/entra.sh, as cupboard-deployer (Application Developer only), at resource group scope
// (it contains no Azure resources). Gary's personal admin account can't deploy Graph resources.
// Re-running it updates the registrations in place (uniqueName), never duplicates them.
extension microsoftGraphV1

@description('SPA redirect URIs: Vite\'s dev server, plus the Static Web App once it exists (deploy/entra.sh passes it).')
param spaRedirectUris array = [
  'http://localhost:5173/'
]

var tenantId = tenant().tenantId
// api://<tenantId>/... is accepted without a verified domain, and avoids needing the API's own app ID up front.
var apiIdentifierUri = 'api://${tenantId}/cupboard-tracker-api'
var scopeName = 'access_as_user'
var scopeId = guid(tenantId, 'cupboard-tracker-api', scopeName)
// Microsoft's well-known Azure CLI client: lets `az account get-access-token --scope <api>/access_as_user` work with
// Gary's own az login, so verification never needs a credential.
var azureCliClientId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'

resource spa 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'cupboard-tracker-spa'
  displayName: 'Cupboard Tracker'
  signInAudience: 'AzureADMyOrg'
  spa: {
    redirectUris: spaRedirectUris
  }
  web: {
    implicitGrantSettings: {
      enableAccessTokenIssuance: false
      enableIdTokenIssuance: false
    }
  }
  // No requiredResourceAccess for the API: MSAL asks for the scope at runtime and the API pre-authorises the SPA
  // (spike #35), which also avoids a circular reference.
}

resource spaServicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: spa.appId
  // Only users assigned to the app (in the Entra admin center) can sign in.
  appRoleAssignmentRequired: true
}

resource api 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'cupboard-tracker-api'
  displayName: 'Cupboard Tracker API'
  signInAudience: 'AzureADMyOrg'
  identifierUris: [
    apiIdentifierUri
  ]
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: scopeId
        value: scopeName
        type: 'User'
        isEnabled: true
        adminConsentDisplayName: 'Access Cupboard Tracker'
        adminConsentDescription: 'Lets the Cupboard Tracker app read and change household stock as the signed-in user.'
        userConsentDisplayName: 'Access Cupboard Tracker as you'
        userConsentDescription: 'Lets the Cupboard Tracker app read and change household stock as you.'
      }
    ]
    preAuthorizedApplications: [
      {
        appId: spa.appId
        delegatedPermissionIds: [
          scopeId
        ]
      }
      {
        appId: azureCliClientId
        delegatedPermissionIds: [
          scopeId
        ]
      }
    ]
  }
}

resource apiServicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: api.appId
}

// Not secrets: they go to the API app settings and the UI build as plain values.
output tenantId string = tenantId
output spaClientId string = spa.appId
output apiClientId string = api.appId
output apiScope string = '${apiIdentifierUri}/${scopeName}'
output azureCliClientId string = azureCliClientId
