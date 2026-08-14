// P2-04 / D8/D23: Azure SQL Database, serverless. Same T-SQL surface as the local
// mssql:2022-latest container; DbUp migrations and RLS (SESSION_CONTEXT) behave
// identically — no code or migration change. Auto-pause is disabled (decision 7):
// SqlHealthCheck polls every /ready cycle and RollupService hits SQL every 15
// minutes, so a 60-minute pause window can never elapse; the saving comes from
// the low minCapacity floor, not from pausing.
param location string
param environmentName string
@description('Precomputed in main.bicep.')
param sqlServerName string
param databaseName string
param sqlAdminLogin string
@secure()
param sqlAdminPassword string
@description('Operator workstation IPv4 allowed through the firewall for sqlcmd/verify-rls.sql; empty = no rule added.')
param opsClientIp string = ''

var skuCapacity = environmentName == 'prod' ? 4 : 2

resource sqlServer 'Microsoft.Sql/servers@2021-11-01' = {
  name: sqlServerName
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource db 'Microsoft.Sql/servers/databases@2021-11-01' = {
  parent: sqlServer
  name: databaseName
  location: location
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: skuCapacity
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    minCapacity: json('0.5')
    autoPauseDelay: -1
    maxSizeBytes: 34359738368
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

// Container Apps outbound has no static IP; it reaches SQL over the Azure backbone.
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2021-11-01' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource allowOpsClient 'Microsoft.Sql/servers/firewallRules@2021-11-01' = if (!empty(opsClientIp)) {
  parent: sqlServer
  name: 'AllowOpsClient'
  properties: {
    startIpAddress: opsClientIp
    endIpAddress: opsClientIp
  }
}

output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
