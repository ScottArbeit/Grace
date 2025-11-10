param location string = resourceGroup().location
param appName string = 'grace-server'
param environment string = 'production'
param skuName string = 'B2'
param skuCapacity int = 1

var appServicePlanName = '${appName}-plan-${uniqueString(resourceGroup().id)}'
var webAppName = '${appName}-${uniqueString(resourceGroup().id)}'
var appInsightsName = '${appName}-ai-${uniqueString(resourceGroup().id)}'
var storageAccountName = toLower('sa${appName}${uniqueString(resourceGroup().id)}')
var cosmosAccountName = toLower('${appName}-cosmos-${uniqueString(resourceGroup().id)}')
var cosmosDatabaseName = 'grace'
var cosmosContainerName = 'grace-events'
var serviceBusNamespaceName = '${appName}-sb-${uniqueString(resourceGroup().id)}'
var keyVaultName = toLower('${appName}-kv-${uniqueString(resourceGroup().id)}')

resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: appServicePlanName
  location: location
  sku: {
    name: skuName
    tier: 'Basic'
    capacity: skuCapacity
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    RetentionInDays: 30
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2023-11-15' = {
  name: cosmosAccountName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    enableAutomaticFailover: false
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    enableFreeTier: false
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2023-11-15' = {
  name: '${cosmosAccount.name}/${cosmosDatabaseName}'
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
    options: {
      throughput: 400
    }
  }
  dependsOn: [
    cosmosAccount
  ]
}

resource cosmosContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-11-15' = {
  name: '${cosmosDatabase.name}/${cosmosContainerName}'
  properties: {
    resource: {
      id: cosmosContainerName
      partitionKey: {
        paths: [
          '/partitionKey'
        ]
        kind: 'Hash'
      }
    }
    options: {
      throughput: 400
    }
  }
  dependsOn: [
    cosmosDatabase
  ]
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2021-11-01' = {
  name: serviceBusNamespaceName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
    capacity: 1
  }
  properties: {
    publicNetworkAccess: 'Enabled'
  }
}

resource serviceBusRootAuth 'Microsoft.ServiceBus/namespaces/authorizationRules@2021-11-01' = {
  parent: serviceBusNamespace
  name: 'RootManageSharedAccessKey'
  properties: {
    rights: [
      'Listen'
      'Manage'
      'Send'
    ]
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: tenant().tenantId
    enableRbacAuthorization: true
    sku: {
      family: 'A'
      name: 'standard'
    }
    publicNetworkAccess: 'Enabled'
  }
}

var storageKeys = listKeys(storageAccount.id, '2023-01-01')
var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageKeys.keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

var cosmosKeys = listKeys(cosmosAccount.id, '2023-11-15')
var cosmosConnectionString = 'AccountEndpoint=${cosmosAccount.properties.documentEndpoint};AccountKey=${cosmosKeys.primaryMasterKey};'

var serviceBusKeys = listKeys(serviceBusRootAuth.id, '2017-04-01')
var serviceBusConnectionString = serviceBusKeys.primaryConnectionString

resource webApp 'Microsoft.Web/sites@2023-01-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'DOTNET_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'azurestorageconnectionstring'
          value: storageConnectionString
        }
        {
          name: 'azurecosmosdbconnectionstring'
          value: cosmosConnectionString
        }
        {
          name: 'cosmosdatabasename'
          value: cosmosDatabaseName
        }
        {
          name: 'cosmoscontainername'
          value: cosmosContainerName
        }
        {
          name: 'orleans_cluster_id'
          value: '${appName}-${environment}'
        }
        {
          name: 'orleans_service_id'
          value: appName
        }
        {
          name: 'redis_host'
          value: ''
        }
        {
          name: 'redis_port'
          value: ''
        }
      ]
      connectionStrings: [
        {
          name: 'AzureStorage'
          type: 'Custom'
          connectionString: storageConnectionString
        }
        {
          name: 'CosmosDb'
          type: 'Custom'
          connectionString: cosmosConnectionString
        }
        {
          name: 'ServiceBus'
          type: 'Custom'
          connectionString: serviceBusConnectionString
        }
      ]
    }
  }
  dependsOn: [
    appServicePlan
    appInsights
    storageAccount
    cosmosAccount
    serviceBusNamespace
  ]
}

output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output storageConnection string = storageConnectionString
output cosmosDbConnection string = cosmosConnectionString
output serviceBusConnection string = serviceBusConnectionString
