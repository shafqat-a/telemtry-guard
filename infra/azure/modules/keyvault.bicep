// P2-04 / D16: Key Vault holds every connection string / secret the API and
// migration jobs consume via Container Apps secretRefs (versionless URLs, so a
// rotated secret is picked up by the next revision without a template change).
// Third-party secrets (Turnstile, Google Ads, Meta) are seeded out-of-band by the
// operator (runbook step 8) and are NOT created here.
param location string
param environmentName string
@description('Precomputed in main.bicep — 24-char Key Vault name limit already truncated there.')
param keyVaultName string
param uamiPrincipalId string

param sqlServerFqdn string
param sqlAdminLogin string
@secure()
param sqlAdminPassword string
param databaseName string

param redisHostName string
@secure()
param redisPrimaryKey string

param clickHousePrivateIp string
param clickHouseDb string
param clickHouseUser string
@secure()
param clickHousePassword string

@secure()
param beaconHmacSecret string

@secure()
param appInsightsConnectionString string

var softDeleteRetentionInDays = environmentName == 'prod' ? 90 : 7

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: softDeleteRetentionInDays
    publicNetworkAccess: 'Enabled'
  }
}

// Key Vault Secrets User built-in role: 4633458b-17de-408a-b874-0445c86b69e6
resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, uamiPrincipalId, 'KeyVaultSecretsUser')
  scope: vault
  properties: {
    principalId: uamiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

resource sqlConnectionString 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'sql-connection-string'
  properties: {
    value: 'Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${databaseName};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
  }
}

resource redisConnectionString 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'redis-connection-string'
  properties: {
    value: '${redisHostName}:6380,password=${redisPrimaryKey},ssl=True,abortConnect=False'
  }
}

resource clickHouseConnectionString 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'clickhouse-connection-string'
  properties: {
    value: 'Host=${clickHousePrivateIp};Port=8123;Database=${clickHouseDb};Username=${clickHouseUser};Password=${clickHousePassword}'
  }
}

resource beaconHmacSecretEntry 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'beacon-hmac-secret'
  properties: {
    value: beaconHmacSecret
  }
}

resource appInsightsConnectionStringEntry 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'appinsights-connection-string'
  properties: {
    value: appInsightsConnectionString
  }
}

output keyVaultName string = vault.name
output keyVaultUri string = vault.properties.vaultUri
