@description('Globally unique Azure Managed Redis name.')
param name string

@description('Azure region for Azure Managed Redis.')
param location string

@description('Tags applied to Azure Managed Redis resources.')
param tags object

@description('Azure Managed Redis SKU, such as Balanced_B0.')
param skuName string

@description('Whether Redis uses a replicated high-availability pair.')
param highAvailability bool

resource redis 'Microsoft.Cache/redisEnterprise@2025-04-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: skuName
  }
  properties: {
    encryption: {}
    highAvailability: highAvailability ? 'Enabled' : 'Disabled'
    minimumTlsVersion: '1.2'
  }
}

resource database 'Microsoft.Cache/redisEnterprise/databases@2025-04-01' = {
  parent: redis
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    clusteringPolicy: 'OSSCluster'
    evictionPolicy: 'VolatileLRU'
    modules: []
    port: 10000
  }
}

output name string = redis.name
output hostName string = redis.properties.hostName
output port int = database.properties.port
