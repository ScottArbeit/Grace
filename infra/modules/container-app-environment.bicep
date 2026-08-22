@description('Name of the Container Apps managed environment.')
param name string

@description('Azure region for the Container Apps managed environment.')
param location string

@description('Tags applied to the Container Apps managed environment.')
param tags object

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: name
  location: location
  tags: tags
  properties: {}
}

output id string = environment.id
