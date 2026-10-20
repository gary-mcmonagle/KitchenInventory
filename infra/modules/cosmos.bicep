@description('Azure region.')
param location string

@description('Resource tags.')
param tags object

@description('Cosmos DB account name (globally unique).')
param accountName string

@description('SQL (NoSQL) database name.')
param databaseName string

// Containers and partition keys from docs/data-model.md. Changing a partition key later means
// creating a new container and copying data, so treat this list as fixed.
var containers = [
  { name: 'scanEvents', partitionKey: '/deviceId' }
  { name: 'products', partitionKey: '/id' }
  { name: 'inventoryItems', partitionKey: '/productId' }
  { name: 'devices', partitionKey: '/deviceId' }
  { name: 'unmatchedCheckOuts', partitionKey: '/id' }
  { name: 'sentDigests', partitionKey: '/id' }
  { name: 'leases', partitionKey: '/id' }
  // Push notifications (#77): one document per device, by person; and short-lived "seen" receipts and job claims,
  // which Cosmos deletes itself (each document sets its own ttl; the default is a day).
  { name: 'pushSubscriptions', partitionKey: '/userId' }
  { name: 'pushSeen', partitionKey: '/eventKey', defaultTtl: 86400 }
  // The household's shopping list (#84): items, their tombstones and list memory, one partition (listId "household").
  // defaultTtl -1 turns on per-document ttl (tombstones expire) without expiring anything else.
  { name: 'shoppingList', partitionKey: '/listId', defaultTtl: -1 }
]

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: accountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [
      { name: 'EnableServerless' }
    ]
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    // Azure's default; stated so what-if matches the live account (single region, so it has no effect).
    enableAutomaticFailover: true
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    // Managed identity and data-plane RBAC only: account keys can't be used.
    disableLocalAuth: true
    disableKeyBasedMetadataWriteAccess: true
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: {
      id: databaseName
    }
  }
}

resource sqlContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [
  for c in containers: {
    parent: database
    name: c.name
    properties: {
      // defaultTtl only where a container sets one: Cosmos rejects an explicit null (the #77 deploy failed on it).
      resource: union(
        {
          id: c.name
          partitionKey: {
            paths: [c.partitionKey]
            kind: 'Hash'
            version: 2
          }
          // Azure's defaults, stated so what-if matches the live containers.
          indexingPolicy: {
            automatic: true
            indexingMode: 'consistent'
            includedPaths: [{ path: '/*' }]
            excludedPaths: [{ path: '/"_etag"/?' }]
          }
          conflictResolutionPolicy: {
            mode: 'LastWriterWins'
            conflictResolutionPath: '/_ts'
          }
        },
        contains(c, 'defaultTtl') ? { defaultTtl: c.defaultTtl } : {}
      )
    }
  }
]

output accountName string = account.name
output endpoint string = account.properties.documentEndpoint
output databaseName string = database.name
