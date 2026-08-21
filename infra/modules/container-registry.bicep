@description('Globally unique Azure Container Registry name.')
param name string

@description('Azure region for the registry.')
param location string

@description('Tags applied to the registry.')
param tags object

@description('Object ID of the managed identity that pulls Grace Server images.')
param imagePullPrincipalId string

var acrPullRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    dataEndpointEnabled: false
    publicNetworkAccess: 'Enabled'
    zoneRedundancy: 'Disabled'
  }
}

resource imagePullAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, imagePullPrincipalId, acrPullRoleDefinitionId)
  scope: registry
  properties: {
    principalId: imagePullPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleDefinitionId
  }
}

output id string = registry.id
output loginServer string = registry.properties.loginServer
