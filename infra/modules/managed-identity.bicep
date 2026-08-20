@description('Name of the user-assigned managed identity.')
param name string

@description('Azure region for the managed identity.')
param location string

@description('Tags applied to the managed identity.')
param tags object

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags
}

output id string = identity.id
output clientId string = identity.properties.clientId
output principalId string = identity.properties.principalId
