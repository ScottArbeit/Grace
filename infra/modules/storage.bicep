@description('Globally unique Storage account name.')
param name string

@description('Azure region for the Storage account.')
param location string

@description('Tags applied to the Storage account.')
param tags object

@description('Microsoft Entra principal that runs Grace during infrastructure validation.')
param developerPrincipalId string

@description('Optional managed-identity principal that runs Grace Server.')
param graceServerPrincipalId string = ''

var storageBlobDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)
var storageTableDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
)

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: account
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: false
    }
  }
}

resource directoryVersions 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'directoryversions'
  properties: {
    publicAccess: 'None'
  }
}

resource diffs 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'diffs'
  properties: {
    publicAccess: 'None'
  }
}

resource zipFiles 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'zipfiles'
  properties: {
    publicAccess: 'None'
  }
}

resource blobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, developerPrincipalId, storageBlobDataContributorRoleId)
  scope: account
  properties: {
    principalId: developerPrincipalId
    principalType: 'User'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

resource graceServerBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(graceServerPrincipalId)) {
  name: guid(account.id, graceServerPrincipalId, storageBlobDataContributorRoleId)
  scope: account
  properties: {
    principalId: graceServerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

resource graceServerTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(graceServerPrincipalId)) {
  name: guid(account.id, graceServerPrincipalId, storageTableDataContributorRoleId)
  scope: account
  properties: {
    principalId: graceServerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageTableDataContributorRoleId
  }
}

resource tableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, developerPrincipalId, storageTableDataContributorRoleId)
  scope: account
  properties: {
    principalId: developerPrincipalId
    principalType: 'User'
    roleDefinitionId: storageTableDataContributorRoleId
  }
}

output accountName string = account.name
output blobEndpoint string = account.properties.primaryEndpoints.blob
output tableEndpoint string = account.properties.primaryEndpoints.table
