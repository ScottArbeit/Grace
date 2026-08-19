@description('Globally unique Azure SQL logical server name.')
param name string

@description('Azure region for the SQL logical server and database.')
param location string

@description('Tags applied to Azure SQL resources.')
param tags object

@description('Microsoft Entra tenant containing the SQL administrator.')
param tenantId string

@description('Object ID of the Microsoft Entra SQL administrator.')
param administratorObjectId string

@description('Display name or user principal name of the Microsoft Entra SQL administrator.')
param administratorLogin string

@description('Whether the database uses General Purpose serverless compute.')
param serverless bool

@description('Azure SQL database SKU name.')
param skuName string

@description('Azure SQL database SKU tier.')
param skuTier string = 'GeneralPurpose'

@description('Azure SQL database hardware family.')
param skuFamily string = 'Gen5'

@description('Maximum vCore capacity exposed to the database.')
@minValue(1)
param skuCapacity int

@description('Maximum database size in bytes.')
@minValue(1073741824)
param maxSizeBytes int

@description('Optional public client IPv4 address allowed to connect to the lab database.')
param clientIpAddress string = ''

@description('Grace operations database name.')
param databaseName string = 'GraceOperations'

resource sqlServer 'Microsoft.Sql/servers@2025-01-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: administratorLogin
      principalType: 'User'
      sid: administratorObjectId
      tenantId: tenantId
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'
    version: '12.0'
  }
}

resource clientFirewallRule 'Microsoft.Sql/servers/firewallRules@2025-01-01' = if (!empty(clientIpAddress)) {
  parent: sqlServer
  name: 'GraceInfrastructureLabClient'
  properties: {
    endIpAddress: clientIpAddress
    startIpAddress: clientIpAddress
  }
}

resource database 'Microsoft.Sql/servers/databases@2025-01-01' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: {
    capacity: skuCapacity
    family: skuFamily
    name: skuName
    tier: skuTier
  }
  properties: union(
    {
      maxSizeBytes: maxSizeBytes
      readScale: 'Disabled'
      zoneRedundant: false
    },
    serverless ? {
      autoPauseDelay: 60
      minCapacity: json('0.5')
    } : {}
  )
}

output databaseName string = database.name
output fullyQualifiedDomainName string = sqlServer.properties.fullyQualifiedDomainName
output entraConnectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${database.name};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;Authentication=Active Directory Default;'
