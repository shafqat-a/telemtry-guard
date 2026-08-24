// TelemetryGuard — Azure deployment (P2-04 / D16). Resource-group-scoped
// orchestrator: Container Apps first (D16), Cloudflare stays the outermost edge
// (D13 — Front Door is deliberately NOT deployed here), self-hosted ClickHouse VM
// (D6 decision 1), Azure SQL serverless (D8), Azure Cache for Redis (D5).
//
// Deployment order note: modules are declared below in network -> observability ->
// registry -> (sql, redis, clickhouseVm) -> keyvault -> containerapps -> (optional)
// grafana order because keyvault's secrets (sql-connection-string,
// redis-connection-string) are computed FROM sql's and redis's outputs — Bicep's
// dependency graph (driven by the module input wiring below, not by declaration
// order) enforces this regardless, but the textual order here matches it for
// readability.
targetScope = 'resourceGroup'

@description('dev | prod — suffixes every resource name and selects sizing defaults.')
@allowed(['dev', 'prod'])
param environmentName string

param location string = resourceGroup().location

@description('Lowercase alphanumeric prefix, 3-8 chars, used to build globally unique names.')
@minLength(3)
@maxLength(8)
param namePrefix string = 'tguard'

@description('Full image reference incl. tag, e.g. acr.azurecr.io/telemetryguard-api:<sha>. Supplied per deploy — never a moving tag.')
param containerImage string

@description('OpenTelemetry Collector image, imported into ACR by the deploy workflow.')
param otelCollectorImage string

@description('Cloudflare IPv4 CIDRs allowed to reach ingress (D13 origin lockdown). Generated from TelemetryGuard.Api/Edge/cloudflare-ips.txt by the deploy workflow.')
param cloudflareIpv4Ranges array

@description('Set false for phase A of a deploy (infra + migration jobs only), true for phase B (roll the app).')
param deployApiApp bool = true

@description('Turnstile / Google Ads / Meta secrets have been seeded in Key Vault (runbook step 8) — until then the app runs with the inert appsettings defaults.')
param integrationSecretsEnabled bool = false

param deployGrafana bool = false

@description('Operator workstation IPv4 allowed through the SQL firewall; empty = none.')
param opsClientIp string = ''

param sqlAdminLogin string = 'tgadmin'
@description('0013: least-privilege SQL user for the API request path (tg_app role). Created by the SQL migration job via `provision create-db-user`.')
param sqlAppLogin string = 'tg_app'
@secure()
param sqlAppPassword string
@description('0013: least-privilege SQL user for background jobs (tg_system role) — the only application principal the SYSTEM sentinel bypass honours.')
param sqlSystemLogin string = 'tg_system'
@secure()
param sqlSystemPassword string
@secure()
param sqlAdminPassword string
@secure()
param clickHousePassword string
@secure()
param beaconHmacSecret string

@description('Custom domain bound to ingress (the Cloudflare-proxied hostname). Empty on the first deploy — see runbook step 6.')
param customDomainName string = ''
@description('Resource id of a managedEnvironments/certificates resource. Required when customDomainName is set.')
param customDomainCertificateId string = ''

var containerAppEnvName = '${namePrefix}-cae-${environmentName}'
var sqlServerName = '${namePrefix}-sql-${environmentName}'
var databaseName = 'TelemetryGuard'
var redisName = '${namePrefix}-redis-${environmentName}'
var keyVaultNameRaw = '${namePrefix}kv${environmentName}${uniqueString(resourceGroup().id)}'
var keyVaultName = length(keyVaultNameRaw) > 24 ? substring(keyVaultNameRaw, 0, 24) : keyVaultNameRaw
// acrName is NOT computed here — registry.bicep derives it internally so it can be
// deployed standalone as deploy.yml's bootstrap step (see the comment in that module).
var clickHousePrivateIp = '10.20.2.4'
var clickHouseDb = 'telemetry_guard'
var clickHouseUser = 'tg'

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    location: location
    namePrefix: namePrefix
    environmentName: environmentName
  }
}

module observability 'modules/observability.bicep' = {
  name: 'observability'
  params: {
    location: location
    namePrefix: namePrefix
    environmentName: environmentName
  }
}

module registry 'modules/registry.bicep' = {
  name: 'registry'
  params: {
    location: location
    namePrefix: namePrefix
    environmentName: environmentName
  }
}

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    location: location
    environmentName: environmentName
    sqlServerName: sqlServerName
    databaseName: databaseName
    sqlAdminLogin: sqlAdminLogin
    sqlAdminPassword: sqlAdminPassword
    opsClientIp: opsClientIp
  }
}

module redis 'modules/redis.bicep' = {
  name: 'redis'
  params: {
    location: location
    environmentName: environmentName
    redisName: redisName
  }
}

module clickhouseVm 'modules/clickhouse-vm.bicep' = {
  name: 'clickhouseVm'
  params: {
    location: location
    namePrefix: namePrefix
    environmentName: environmentName
    dataSubnetId: network.outputs.dataSubnetId
    clickHousePrivateIp: clickHousePrivateIp
    clickHouseDb: clickHouseDb
    clickHousePassword: clickHousePassword
  }
}

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    environmentName: environmentName
    keyVaultName: keyVaultName
    uamiPrincipalId: registry.outputs.uamiPrincipalId
    sqlServerFqdn: sql.outputs.sqlServerFqdn
    sqlAdminLogin: sqlAdminLogin
    sqlAdminPassword: sqlAdminPassword
    sqlAppLogin: sqlAppLogin
    sqlAppPassword: sqlAppPassword
    sqlSystemLogin: sqlSystemLogin
    sqlSystemPassword: sqlSystemPassword
    databaseName: databaseName
    redisHostName: redis.outputs.redisHostName
    redisPrimaryKey: redis.outputs.redisPrimaryKey
    clickHousePrivateIp: clickHousePrivateIp
    clickHouseDb: clickHouseDb
    clickHouseUser: clickHouseUser
    clickHousePassword: clickHousePassword
    beaconHmacSecret: beaconHmacSecret
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
  }
}

module containerapps 'modules/containerapps.bicep' = {
  name: 'containerapps'
  params: {
    location: location
    namePrefix: namePrefix
    environmentName: environmentName
    containerAppEnvName: containerAppEnvName
    infraSubnetId: network.outputs.infraSubnetId
    workspaceCustomerId: observability.outputs.workspaceCustomerId
    workspaceSharedKey: observability.outputs.workspaceSharedKey
    acrLoginServer: registry.outputs.acrLoginServer
    uamiId: registry.outputs.uamiId
    keyVaultUri: keyvault.outputs.keyVaultUri
    containerImage: containerImage
    otelCollectorImage: otelCollectorImage
    cloudflareIpv4Ranges: cloudflareIpv4Ranges
    deployApiApp: deployApiApp
    integrationSecretsEnabled: integrationSecretsEnabled
    customDomainName: customDomainName
    customDomainCertificateId: customDomainCertificateId
  }
}

module grafana 'modules/grafana.bicep' = if (deployGrafana) {
  name: 'grafana'
  params: {
    location: location
    namePrefix: namePrefix
    environmentName: environmentName
  }
}

output apiFqdn string = containerapps.outputs.apiFqdn
output acrLoginServer string = registry.outputs.acrLoginServer
output sqlServerFqdn string = sql.outputs.sqlServerFqdn
output keyVaultName string = keyvault.outputs.keyVaultName
output containerAppName string = containerapps.outputs.containerAppName
output migrateSqlJobName string = containerapps.outputs.migrateSqlJobName
output migrateClickHouseJobName string = containerapps.outputs.migrateClickHouseJobName
