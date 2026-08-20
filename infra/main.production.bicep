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

@description('High-availability Azure Managed Redis SKU.')
param redisSkuName string

@description('Immutable Grace Server image in this deployment registry, including its sha256 digest.')
param graceServerImage string

@description('Common environment tags.')
param tags object = {
  environment: environmentName
  lifecycle: 'persistent'
  project: 'Grace'
}

var normalizedEnvironment = toLower(replace(environmentName, '-', ''))
var normalizedSuffix = toLower(replace(deploymentSuffix, '-', ''))
var storageName = take('grace${normalizedEnvironment}${normalizedSuffix}', 24)
var cosmosName = take('grace-cosmos-${environmentName}-${normalizedSuffix}', 44)
var serviceBusName = take('grace-sb-${environmentName}-${normalizedSuffix}', 50)
var sqlServerName = take('grace-sql-${environmentName}-${normalizedSuffix}', 63)
var redisName = take('grace-redis-${environmentName}-${normalizedSuffix}', 60)
var registryName = take('grace${normalizedEnvironment}${normalizedSuffix}acr', 50)
var identityName = take('grace-server-${environmentName}-${normalizedSuffix}', 128)
var containerEnvironmentName = take('grace-${environmentName}-${normalizedSuffix}', 60)
var containerAppName = take('grace-server-${environmentName}-${normalizedSuffix}', 32)
var validatedGraceServerImage = contains(graceServerImage, '@sha256:')
  ? graceServerImage
  : fail('graceServerImage must be an immutable sha256 digest reference.')

module identity 'modules/managed-identity.bicep' = {
  name: 'grace-server-identity'
  params: {
    location: location
    name: identityName
    tags: tags
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    developerPrincipalId: developerPrincipalId
    graceServerPrincipalId: identity.outputs.principalId
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
    graceServerPrincipalId: identity.outputs.principalId
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
    graceServerPrincipalId: identity.outputs.principalId
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
    autoPauseDelayMinutes: 15
    freeLimitExhaustionBehavior: 'BillOverUsage'
    maxSizeBytes: 34359738368
    minimumCapacity: '0.5'
    name: sqlServerName
    serverless: true
    skuCapacity: 1
    skuName: 'GP_S_Gen5_1'
    tags: tags
    tenantId: tenant().tenantId
    useFreeLimit: true
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

module registry 'modules/container-registry.bicep' = {
  name: 'container-registry'
  params: {
    imagePullPrincipalId: identity.outputs.principalId
    location: location
    name: registryName
    tags: tags
  }
}

module containerEnvironment 'modules/container-app-environment.bicep' = {
  name: 'container-app-environment'
  params: {
    location: location
    name: containerEnvironmentName
    tags: tags
  }
}

module graceServer 'modules/container-app.bicep' = {
  name: 'grace-server'
  params: {
    cosmosContainerName: cosmos.outputs.containerName
    cosmosDatabaseName: cosmos.outputs.databaseName
    cosmosEndpoint: cosmos.outputs.endpoint
    environmentId: containerEnvironment.outputs.id
    identityClientId: identity.outputs.clientId
    identityId: identity.outputs.id
    image: startsWith(validatedGraceServerImage, '${registry.outputs.loginServer}/')
      ? validatedGraceServerImage
      : fail('graceServerImage must be hosted by the registry created by this deployment.')
    location: location
    name: containerAppName
    registryServer: registry.outputs.loginServer
    serviceBusEventSubscription: serviceBus.outputs.eventSubscriptionName
    serviceBusEventTopic: serviceBus.outputs.eventTopicName
    serviceBusGraceUsageSubscription: serviceBus.outputs.graceUsageSubscriptionName
    serviceBusGraceUsageTopic: serviceBus.outputs.graceUsageTopicName
    serviceBusNamespace: '${serviceBus.outputs.namespaceName}.servicebus.windows.net'
    sqlConnectionString: sql.outputs.entraConnectionString
    storageAccountName: storage.outputs.accountName
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
output containerRegistryLoginServer string = registry.outputs.loginServer
output graceServerFqdn string = graceServer.outputs.fqdn
output graceServerRevisionName string = graceServer.outputs.latestRevisionName
output graceServerIdentityClientId string = identity.outputs.clientId
output graceServerIdentityPrincipalId string = identity.outputs.principalId
