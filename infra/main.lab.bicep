targetScope = 'resourceGroup'

@description('Short stable suffix used to make globally unique lab resource names.')
@minLength(3)
@maxLength(12)
param deploymentSuffix string

@description('Azure region for disposable lab resources.')
param location string = resourceGroup().location

@description('Object ID of the Microsoft Entra user who validates the lab.')
param developerPrincipalId string

@description('User principal name of the Microsoft Entra user who administers the lab SQL database.')
param developerPrincipalName string

@description('Public IPv4 address allowed to connect to Azure SQL during the lab run.')
param clientIpAddress string = ''

@description('Common tags added to disposable lab resources.')
param tags object = {
  environment: 'infrastructure-lab'
  lifecycle: 'disposable'
  project: 'Grace'
}

var normalizedSuffix = toLower(replace(deploymentSuffix, '-', ''))
var storageName = take('gracelab${normalizedSuffix}', 24)
var cosmosName = take('grace-cosmos-lab-${normalizedSuffix}', 44)
var serviceBusName = take('grace-sb-lab-${normalizedSuffix}', 50)
var sqlServerName = take('grace-sql-lab-${normalizedSuffix}', 63)
var redisName = take('grace-redis-lab-${normalizedSuffix}', 60)

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    developerPrincipalId: developerPrincipalId
    location: location
    name: storageName
    tags: tags
  }
}

module cosmos 'modules/cosmos.bicep' = {
  name: 'cosmos'
  params: {
    databaseName: 'grace-lab'
    containerName: 'grace-events'
    developerPrincipalId: developerPrincipalId
    location: location
    name: cosmosName
    serverless: true
    tags: tags
  }
}

module serviceBus 'modules/service-bus.bicep' = {
  name: 'service-bus'
  params: {
    developerPrincipalId: developerPrincipalId
    location: location
    name: serviceBusName
    tags: tags
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    administratorLogin: developerPrincipalName
    administratorObjectId: developerPrincipalId
    clientIpAddress: clientIpAddress
    location: location
    maxSizeBytes: 34359738368
    name: sqlServerName
    serverless: true
    skuCapacity: 1
    skuName: 'GP_S_Gen5_1'
    tags: tags
    tenantId: tenant().tenantId
  }
}

module redis 'modules/redis.bicep' = {
  name: 'redis'
  params: {
    highAvailability: false
    location: location
    name: redisName
    skuName: 'Balanced_B0'
    tags: tags
  }
}

output profile string = 'infrastructure-lab'
output storageAccountName string = storage.outputs.accountName
output cosmosEndpoint string = cosmos.outputs.endpoint
output cosmosDatabaseName string = cosmos.outputs.databaseName
output cosmosContainerName string = cosmos.outputs.containerName
output serviceBusNamespace string = serviceBus.outputs.namespaceName
output serviceBusEventTopic string = serviceBus.outputs.eventTopicName
output serviceBusEventSubscription string = serviceBus.outputs.eventSubscriptionName
output serviceBusOperationalFactsTopic string = serviceBus.outputs.operationalFactsTopicName
output serviceBusOperationalFactsSubscription string = serviceBus.outputs.operationalFactsSubscriptionName
output sqlConnectionString string = sql.outputs.entraConnectionString
output redisHostName string = redis.outputs.hostName
output redisPort int = redis.outputs.port
