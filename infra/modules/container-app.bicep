@description('Name of the Grace Server Container App.')
param name string

@description('Azure region for Grace Server.')
param location string

@description('Tags applied to Grace Server.')
param tags object

@description('Resource ID of the Container Apps managed environment.')
param environmentId string

@description('Resource ID of the Grace Server user-assigned managed identity.')
param identityId string

@description('Client ID of the Grace Server user-assigned managed identity.')
param identityClientId string

@description('Azure Container Registry login server.')
param registryServer string

@description('Immutable Grace Server image reference, including its sha256 digest.')
param image string

@description('Azure Storage account name used by Orleans and Grace storage.')
param storageAccountName string

@description('Azure Cosmos DB endpoint.')
param cosmosEndpoint string

@description('Azure Cosmos DB database name.')
param cosmosDatabaseName string

@description('Azure Cosmos DB container name.')
param cosmosContainerName string

@description('Service Bus namespace hostname.')
param serviceBusNamespace string

@description('Grace event stream topic name.')
param serviceBusEventTopic string

@description('Grace Server event stream subscription name.')
param serviceBusEventSubscription string

@description('Grace usage topic name.')
param serviceBusGraceUsageTopic string

@description('Grace usage processor subscription name.')
param serviceBusGraceUsageSubscription string

@description('Microsoft Entra Azure SQL connection string.')
param sqlConnectionString string

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 5000
        transport: 'http'
      }
      registries: [
        {
          identity: identityId
          server: registryServer
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'grace-server'
          image: image
          env: [
            { name: 'ASPNETCORE_HTTP_PORTS', value: '5000' }
            { name: 'AZURE_CLIENT_ID', value: identityClientId }
            { name: 'grace__debug_environment', value: 'Azure' }
            { name: 'grace__log_directory', value: '/tmp/grace-logs' }
            { name: 'grace__pubsub__system', value: 'AzureServiceBus' }
            { name: 'grace__orleans__clusterid', value: 'production' }
            { name: 'grace__orleans__serviceid', value: 'grace-prod' }
            { name: 'grace__azure_storage__account_name', value: storageAccountName }
            { name: 'grace__azure_storage__directoryversion_container_name', value: 'directoryversions' }
            { name: 'grace__azure_storage__diff_container_name', value: 'diffs' }
            { name: 'grace__azure_storage__zipfile_container_name', value: 'zipfiles' }
            { name: 'grace__azurecosmosdb__endpoint', value: cosmosEndpoint }
            { name: 'grace__azurecosmosdb__database_name', value: cosmosDatabaseName }
            { name: 'grace__azurecosmosdb__container_name', value: cosmosContainerName }
            { name: 'grace__azure_service_bus__namespace', value: serviceBusNamespace }
            { name: 'grace__azure_service_bus__topic', value: serviceBusEventTopic }
            { name: 'grace__azure_service_bus__subscription', value: serviceBusEventSubscription }
            { name: 'grace__azure_service_bus__operational_facts_topic', value: serviceBusGraceUsageTopic }
            {
              name: 'grace__azure_service_bus__operational_facts_processor_subscription'
              value: serviceBusGraceUsageSubscription
            }
            { name: 'grace__operations__sql__connectionstring', value: sqlConnectionString }
          ]
          probes: [
            {
              type: 'Startup'
              tcpSocket: {
                port: 5000
              }
              initialDelaySeconds: 1
              periodSeconds: 5
              failureThreshold: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/healthz'
                port: 5000
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 5000
                scheme: 'HTTP'
              }
              initialDelaySeconds: 15
              periodSeconds: 30
              failureThreshold: 3
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output fqdn string = app.properties.configuration.ingress.fqdn
output latestRevisionName string = app.properties.latestRevisionName
output latestReadyRevisionName string = app.properties.latestReadyRevisionName
