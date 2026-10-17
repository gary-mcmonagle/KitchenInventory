@description('Cosmos DB account name.')
param accountName string

@description('Database the principals get data access to.')
param databaseName string

@description('Function apps that get the built-in Cosmos DB Data Contributor role on the database: [{ name, principalId }]. The name keeps role-assignment names known before deployment, so what-if can show them.')
param apps array

// Built-in "Cosmos DB Built-in Data Contributor" data-plane role.
var dataContributorRoleId = '00000000-0000-0000-0000-000000000002'

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: accountName
}

resource dataContributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [
  for app in apps: {
    parent: account
    name: guid(account.id, databaseName, app.name, dataContributorRoleId)
    properties: {
      principalId: app.principalId
      roleDefinitionId: '${account.id}/sqlRoleDefinitions/${dataContributorRoleId}'
      scope: '${account.id}/dbs/${databaseName}'
    }
  }
]
