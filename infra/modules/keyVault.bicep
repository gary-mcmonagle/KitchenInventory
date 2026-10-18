// Key Vault for true third-party secrets (#42), such as the Jev API key. Azure services use managed identities instead.
// RBAC authorisation only (no access policies), soft delete and purge protection on. Secrets are set by
// Gary by hand (portal), never by a template, so no secret value ever appears here or in a deployment's history.

@description('Azure region.')
param location string

@description('Tags for the vault.')
param tags object

@description('Key Vault name (globally unique, 3-24 characters).')
param name string

@description('Managed identities that read secrets through Key Vault references (Key Vault Secrets User). Only apps that need a secret.')
param secretReaderPrincipalIds array

@description('Object ID of the person who sets and rotates secrets by hand (Key Vault Secrets Officer). Empty skips it.')
param vaultOfficerPrincipalId string = ''

// Built-in role IDs.
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
var keyVaultSecretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled' // Flex Consumption apps here have no virtual network; access is by Entra identity only
  }
}

resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in secretReaderPrincipalIds: {
    scope: vault
    name: guid(vault.id, principalId, keyVaultSecretsUser)
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    }
  }
]

resource officer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(vaultOfficerPrincipalId)) {
  scope: vault
  name: guid(vault.id, vaultOfficerPrincipalId, keyVaultSecretsOfficer)
  properties: {
    principalId: vaultOfficerPrincipalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficer)
  }
}

output name string = vault.name
output uri string = vault.properties.vaultUri
