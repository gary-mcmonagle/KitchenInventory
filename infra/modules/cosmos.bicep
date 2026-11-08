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
  // Saved recipes and Ready now ideas (#105): one household collection, one partition (householdId "household").
  { name: 'savedRecipes', partitionKey: '/householdId' }
  // Settings people change in the app (#120): one document, aiModels (the registered Foundry models and each job's active one).
  { name: 'settings', partitionKey: '/id' }
  // Ratings (#128, spike #127): one rating per person per dish, one household partition (householdId "household"). Each rating
  // can carry a 256-dimension embedding (written by #129; this card writes none) searched by a flat vector index. A vector
  // policy can only be set when a container is created, so it is here from the start. Needs the account capability above.
  { name: 'ratings', partitionKey: '/householdId', vectorDimensions: 256 }
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
      // Vector search (spike #127): lets a container hold embeddings and be searched by similarity. Free in itself (only
      // containers that use it cost anything); registration is automatic and takes about 15 minutes.
      { name: 'EnableNoSQLVectorSearch' }
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
      // defaultTtl and the vector policy only where a container sets one: Cosmos rejects an explicit null (the #77 deploy failed on it).
      resource: union(
        {
          id: c.name
          partitionKey: {
            paths: [c.partitionKey]
            kind: 'Hash'
            version: 2
          }
          // Azure's defaults, stated so what-if matches the live containers. A vector container excludes its vector from the
          // ordinary index and adds the vector index (spike #127: flat, 256 dimensions, cosine).
          indexingPolicy: union(
            {
              automatic: true
              indexingMode: 'consistent'
              includedPaths: [{ path: '/*' }]
              excludedPaths: contains(c, 'vectorDimensions')
                ? [{ path: '/embedding/*' }, { path: '/"_etag"/?' }]
                : [{ path: '/"_etag"/?' }]
            },
            contains(c, 'vectorDimensions') ? { vectorIndexes: [{ path: '/embedding', type: 'flat' }] } : {}
          )
          conflictResolutionPolicy: {
            mode: 'LastWriterWins'
            conflictResolutionPath: '/_ts'
          }
        },
        contains(c, 'defaultTtl') ? { defaultTtl: c.defaultTtl } : {},
        contains(c, 'vectorDimensions')
          ? {
              vectorEmbeddingPolicy: {
                vectorEmbeddings: [
                  { path: '/embedding', dataType: 'float32', distanceFunction: 'cosine', dimensions: c.vectorDimensions }
                ]
              }
            }
          : {}
      )
    }
  }
]

output accountName string = account.name
output endpoint string = account.properties.documentEndpoint
output databaseName string = database.name
