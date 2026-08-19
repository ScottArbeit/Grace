@description('Globally unique Cosmos DB account name.')
param name string

@description('Azure region for the Cosmos DB account.')
param location string

@description('Tags applied to Cosmos DB resources.')
param tags object

@description('Whether to use consumption-based serverless request units.')
param serverless bool

@description('Provisioned database throughput. Ignored by the serverless profile.')
@minValue(400)
param provisionedThroughput int = 400

@description('Microsoft Entra principal that runs Grace during infrastructure validation.')
param developerPrincipalId string

@description('Optional managed-identity principal that runs Grace Server.')
param graceServerPrincipalId string = ''

@description('Grace Cosmos DB database name.')
param databaseName string = 'grace-dev'

@description('Grace event container name.')
param containerName string = 'grace-events'

var cosmosDataContributorRoleDefinitionId = '${account.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: name
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    capabilities: serverless ? [
      {
        name: 'EnableServerless'
      }
    ] : []
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    databaseAccountOfferType: 'Standard'
    disableKeyBasedMetadataWriteAccess: true
    enableAutomaticFailover: false
    enableFreeTier: false
    locations: [
      {
        failoverPriority: 0
        isZoneRedundant: false
        locationName: location
      }
    ]
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: account
  name: databaseName
  properties: serverless ? {
    resource: {
      id: databaseName
    }
  } : {
    options: {
      throughput: provisionedThroughput
    }
    resource: {
      id: databaseName
    }
  }
}

resource container 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: containerName
  properties: {
    resource: {
      id: containerName
      indexingPolicy: {
        automatic: true
        indexingMode: 'consistent'
        includedPaths: [
          {
            path: '/*'
          }
        ]
      }
      partitionKey: {
        kind: 'Hash'
        paths: [
          '/PartitionKey'
        ]
        version: 2
      }
    }
  }
}

resource developerAccess 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: account
  name: guid(account.id, developerPrincipalId, cosmosDataContributorRoleDefinitionId)
  properties: {
    principalId: developerPrincipalId
    roleDefinitionId: cosmosDataContributorRoleDefinitionId
    scope: account.id
  }
}

resource graceServerAccess 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = if (!empty(graceServerPrincipalId)) {
  parent: account
  name: guid(account.id, graceServerPrincipalId, cosmosDataContributorRoleDefinitionId)
  properties: {
    principalId: graceServerPrincipalId
    roleDefinitionId: cosmosDataContributorRoleDefinitionId
    scope: account.id
  }
}

output accountName string = account.name
output endpoint string = account.properties.documentEndpoint
output databaseName string = database.name
output containerName string = container.name
