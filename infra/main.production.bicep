targetScope = 'resourceGroup'

@description('Stable environment name used in production-shaped resource names and tags.')
@allowed([
  'development'
  'production'
])
param environmentName string

@description('Short stable suffix used to make globally unique resource names.')
@minLength(3)
@maxLength(12)
param deploymentSuffix string

@description('Azure region for the environment.')
param location string = resourceGroup().location

@description('Object ID of the Microsoft Entra principal used during infrastructure validation.')
param developerPrincipalId string

@description('User principal name of the Microsoft Entra SQL administrator.')
param developerPrincipalName string

@description('Provisioned Cosmos DB database throughput in RU/s.')
@minValue(400)
param cosmosProvisionedThroughput int

@description('Provisioned Azure SQL SKU name. Template evaluation rejects serverless SKU names.')
param sqlSkuName string

@description('Provisioned Azure SQL vCore capacity.')
@minValue(1)
param sqlVCoreCapacity int

@description('Maximum Azure SQL database size in bytes.')
@minValue(1073741824)
param sqlMaxSizeBytes int

@description('High-availability Azure Managed Redis SKU.')
param redisSkuName string

@description('Common environment tags.')
param tags object = {
  environment: environmentName
  lifecycle: 'persistent'
  project: 'Grace'
}

var normalizedEnvironment = toLower(replace(environmentName, '-', ''))
var normalizedSuffix = toLower(replace(deploymentSuffix, '-', ''))
var validatedSqlSkuName = contains(toLower(sqlSkuName), '_s_')
  ? fail('The production-shaped profile does not accept Azure SQL serverless SKU names.')
  : sqlSkuName
var storageName = take('grace${normalizedEnvironment}${normalizedSuffix}', 24)
var cosmosName = take('grace-cosmos-${environmentName}-${normalizedSuffix}', 44)
var serviceBusName = take('grace-sb-${environmentName}-${normalizedSuffix}', 50)
var sqlServerName = take('grace-sql-${environmentName}-${normalizedSuffix}', 63)
var redisName = take('grace-redis-${environmentName}-${normalizedSuffix}', 60)

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
    containerName: 'grace-events'
    databaseName: 'grace-${environmentName}'
    developerPrincipalId: developerPrincipalId
    location: location
    name: cosmosName
    provisionedThroughput: cosmosProvisionedThroughput
    serverless: false
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
    location: location
    maxSizeBytes: sqlMaxSizeBytes
    name: sqlServerName
    serverless: false
    skuCapacity: sqlVCoreCapacity
    skuName: validatedSqlSkuName
    tags: tags
    tenantId: tenant().tenantId
  }
}

module redis 'modules/redis.bicep' = {
  name: 'redis'
  params: {
    highAvailability: true
    location: location
    name: redisName
    skuName: redisSkuName
    tags: tags
  }
}

output profile string = 'production-shaped-${environmentName}'
output storageAccountName string = storage.outputs.accountName
output cosmosEndpoint string = cosmos.outputs.endpoint
output cosmosDatabaseName string = cosmos.outputs.databaseName
output cosmosContainerName string = cosmos.outputs.containerName
output serviceBusNamespace string = serviceBus.outputs.namespaceName
output serviceBusEventTopic string = serviceBus.outputs.eventTopicName
output serviceBusEventSubscription string = serviceBus.outputs.eventSubscriptionName
output serviceBusGraceUsageTopic string = serviceBus.outputs.graceUsageTopicName
output serviceBusGraceUsageSubscription string = serviceBus.outputs.graceUsageSubscriptionName
output sqlConnectionString string = sql.outputs.entraConnectionString
output redisHostName string = redis.outputs.hostName
output redisPort int = redis.outputs.port
