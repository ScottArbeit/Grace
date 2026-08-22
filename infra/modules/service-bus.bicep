@description('Globally unique Service Bus namespace name.')
param name string

@description('Azure region for the Service Bus namespace.')
param location string

@description('Tags applied to Service Bus resources.')
param tags object

@description('Microsoft Entra principal that runs Grace during infrastructure validation.')
param developerPrincipalId string

@description('Optional managed-identity principal that runs Grace Server.')
param graceServerPrincipalId string = ''

@description('Grace event stream topic name.')
param eventTopicName string = 'graceeventstream'

@description('Grace Server subscription name.')
param eventSubscriptionName string = 'grace-server'

@description('Grace usage topic name.')
param graceUsageTopicName string = 'grace-usage'

@description('Grace usage collector subscription name.')
param graceUsageSubscriptionName string = 'grace-usage-collector'

var serviceBusDataOwnerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '090c5cfd-751d-490a-894a-3ce6f1109419'
)

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    zoneRedundant: false
  }
}

resource eventTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBus
  name: eventTopicName
  properties: {
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    enableExpress: false
    enablePartitioning: true
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: false
    status: 'Active'
    supportOrdering: false
  }
}

resource eventSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: eventTopic
  name: eventSubscriptionName
  properties: {
    deadLetteringOnFilterEvaluationExceptions: true
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    status: 'Active'
  }
}

resource graceUsageTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBus
  name: graceUsageTopicName
  properties: {
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    enableExpress: false
    enablePartitioning: true
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: false
    status: 'Active'
    supportOrdering: false
  }
}

resource graceUsageSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: graceUsageTopic
  name: graceUsageSubscriptionName
  properties: {
    deadLetteringOnFilterEvaluationExceptions: true
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    status: 'Active'
  }
}

resource developerAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, developerPrincipalId, serviceBusDataOwnerRoleId)
  scope: serviceBus
  properties: {
    principalId: developerPrincipalId
    principalType: 'User'
    roleDefinitionId: serviceBusDataOwnerRoleId
  }
}

resource graceServerAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(graceServerPrincipalId)) {
  name: guid(serviceBus.id, graceServerPrincipalId, serviceBusDataOwnerRoleId)
  scope: serviceBus
  properties: {
    principalId: graceServerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: serviceBusDataOwnerRoleId
  }
}

output namespaceName string = serviceBus.name
output eventTopicName string = eventTopic.name
output eventSubscriptionName string = eventSubscription.name
output graceUsageTopicName string = graceUsageTopic.name
output graceUsageSubscriptionName string = graceUsageSubscription.name
