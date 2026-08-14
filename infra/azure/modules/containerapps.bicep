// P2-04: the Container Apps environment, the API app (one replica, OTel Collector
// sidecar, Cloudflare-only ingress lockdown), and the two migration jobs (always
// deployed, same image as the app — one tag can never deploy app vN against schema
// vN-1). See decision 5 in the task doc for why minReplicas/maxReplicas are pinned
// at 1: VerdictFinalizerService claims grace-expired sessions with ZREM and
// documents itself as single-instance MVP; a second replica would double-run the
// 15-minute RollupService and race the 1-second grace sweep.
param location string
param namePrefix string
param environmentName string
@description('Precomputed in main.bicep.')
param containerAppEnvName string
param infraSubnetId string
param workspaceCustomerId string
@secure()
param workspaceSharedKey string
param acrLoginServer string
param uamiId string
param keyVaultUri string
@description('Full image reference incl. tag — same image deploys the app AND both migration jobs.')
param containerImage string
param otelCollectorImage string
param cloudflareIpv4Ranges array
param deployApiApp bool
param integrationSecretsEnabled bool
param customDomainName string
param customDomainCertificateId string

var appName = '${namePrefix}-api-${environmentName}'
var migrateSqlJobName = '${namePrefix}-migrate-sql-${environmentName}'
var migrateClickHouseJobName = '${namePrefix}-migrate-clickhouse-${environmentName}'

var identityRef = {
  type: 'UserAssigned'
  userAssignedIdentities: {
    '${uamiId}': {}
  }
}
var acrRegistry = {
  server: acrLoginServer
  identity: uamiId
}

// Base Key-Vault-backed secrets: all five are created unconditionally by keyvault.bicep.
var baseSecrets = [
  { name: 'sql-connection-string', keyVaultUrl: '${keyVaultUri}secrets/sql-connection-string', identity: uamiId }
  { name: 'redis-connection-string', keyVaultUrl: '${keyVaultUri}secrets/redis-connection-string', identity: uamiId }
  { name: 'clickhouse-connection-string', keyVaultUrl: '${keyVaultUri}secrets/clickhouse-connection-string', identity: uamiId }
  { name: 'beacon-hmac-secret', keyVaultUrl: '${keyVaultUri}secrets/beacon-hmac-secret', identity: uamiId }
  { name: 'appinsights-connection-string', keyVaultUrl: '${keyVaultUri}secrets/appinsights-connection-string', identity: uamiId }
]
// Third-party secrets are seeded by the operator (runbook step 8) and referenced
// only once integrationSecretsEnabled=true — a fresh environment never references
// a Key Vault secret that does not exist yet.
var integrationSecrets = [
  { name: 'turnstile-secret-key', keyVaultUrl: '${keyVaultUri}secrets/turnstile-secret-key', identity: uamiId }
  { name: 'turnstile-site-key', keyVaultUrl: '${keyVaultUri}secrets/turnstile-site-key', identity: uamiId }
  { name: 'googleads-developer-token', keyVaultUrl: '${keyVaultUri}secrets/googleads-developer-token', identity: uamiId }
  { name: 'googleads-oauth-client-id', keyVaultUrl: '${keyVaultUri}secrets/googleads-oauth-client-id', identity: uamiId }
  { name: 'googleads-oauth-client-secret', keyVaultUrl: '${keyVaultUri}secrets/googleads-oauth-client-secret', identity: uamiId }
  { name: 'googleads-oauth-refresh-token', keyVaultUrl: '${keyVaultUri}secrets/googleads-oauth-refresh-token', identity: uamiId }
  { name: 'meta-system-user-token', keyVaultUrl: '${keyVaultUri}secrets/meta-system-user-token', identity: uamiId }
]
var appSecrets = concat(baseSecrets, integrationSecretsEnabled ? integrationSecrets : [])

var baseEnv = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
  { name: 'OTEL_SERVICE_NAME', value: 'telemetry-guard-api' }
  { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: 'http://localhost:4317' }
  { name: 'OTEL_EXPORTER_OTLP_PROTOCOL', value: 'grpc' }
  { name: 'ConnectionStrings__Main', secretRef: 'sql-connection-string' }
  { name: 'ConnectionStrings__Redis', secretRef: 'redis-connection-string' }
  { name: 'Analytics__Provider', value: 'ClickHouse' }
  { name: 'Analytics__ClickHouse__ConnectionString', secretRef: 'clickhouse-connection-string' }
  { name: 'Beacon__HmacSecret', secretRef: 'beacon-hmac-secret' }
  // D13/INT-05: this environment is Cloudflare-fronted.
  { name: 'Edge__Provider', value: 'Cloudflare' }
  // INT-05 §7: flips to true only once JA3/JA4 actually flow (Enterprise Bot Management).
  { name: 'FeatureExtraction__TlsUaMismatchEnabled', value: 'false' }
  // Explicit: the synthetic-traffic header is never trusted outside dev.
  { name: 'Synthetic__Enabled', value: 'false' }
]
var integrationEnv = [
  { name: 'Turnstile__SiteKey', secretRef: 'turnstile-site-key' }
  { name: 'Turnstile__SecretKey', secretRef: 'turnstile-secret-key' }
  { name: 'GoogleAds__DeveloperToken', secretRef: 'googleads-developer-token' }
  { name: 'GoogleAds__OAuthClientId', secretRef: 'googleads-oauth-client-id' }
  { name: 'GoogleAds__OAuthClientSecret', secretRef: 'googleads-oauth-client-secret' }
  { name: 'GoogleAds__OAuthRefreshToken', secretRef: 'googleads-oauth-refresh-token' }
  { name: 'Meta__SystemUserToken', secretRef: 'meta-system-user-token' }
]
var apiEnv = concat(baseEnv, integrationSecretsEnabled ? integrationEnv : [])

// OTel Collector config (D16 decision 6): stable OTLP receiver -> azuremonitor
// exporter. Verbatim string (Bicep '''...''' does not interpolate ${...}), so
// ${env:APPLICATIONINSIGHTS_CONNECTION_STRING} is resolved by the collector at
// its own startup, never by Bicep.
var otelCollectorConfig = '''
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
processors:
  memory_limiter:
    check_interval: 5s
    limit_percentage: 75
    spike_limit_percentage: 20
  batch: {}
exporters:
  azuremonitor:
    connection_string: ${env:APPLICATIONINSIGHTS_CONNECTION_STRING}
service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [memory_limiter, batch]
      exporters: [azuremonitor]
    metrics:
      receivers: [otlp]
      processors: [memory_limiter, batch]
      exporters: [azuremonitor]
'''

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerAppEnvName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspaceCustomerId
        sharedKey: workspaceSharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: infraSubnetId
      internal: false
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

resource apiApp 'Microsoft.App/containerApps@2024-03-01' = if (deployApiApp) {
  name: appName
  location: location
  identity: identityRef
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      registries: [
        acrRegistry
      ]
      secrets: appSecrets
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        ipSecurityRestrictions: [for (cidr, i) in cloudflareIpv4Ranges: {
          name: 'cloudflare-${i}'
          description: 'D13 origin lockdown — Cloudflare published IPv4 ranges'
          ipAddressRange: cidr
          action: 'Allow'
        }]
        customDomains: empty(customDomainName) ? [] : [
          {
            name: customDomainName
            certificateId: customDomainCertificateId
            bindingType: 'SniEnabled'
          }
        ]
      }
    }
    template: {
      // DO NOT raise maxReplicas above 1 without reading decision 5 in the task
      // doc / doc/runbooks/azure-deployment.md — VerdictFinalizerService is a
      // documented single-instance MVP.
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: {
            cpu: json('0.75')
            memory: '1.5Gi'
          }
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              // /ready includes ClickHouseHealthCheck, so a revision will not go
              // healthy while the ClickHouse VM is down — intended ordering guard
              // (runbook first-deploy order puts the VM before the app).
              type: 'Readiness'
              httpGet: {
                path: '/ready'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 15
              failureThreshold: 6
            }
            {
              type: 'Startup'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              periodSeconds: 5
              failureThreshold: 30
            }
          ]
          env: apiEnv
        }
        {
          name: 'otel'
          image: otelCollectorImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          args: [
            '--config=env:OTELCOL_CONFIG'
          ]
          env: [
            { name: 'OTELCOL_CONFIG', value: otelCollectorConfig }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-connection-string' }
          ]
        }
      ]
    }
  }
}

resource migrateSqlJob 'Microsoft.App/jobs@2024-03-01' = {
  name: migrateSqlJobName
  location: location
  identity: identityRef
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        acrRegistry
      ]
      secrets: [
        { name: 'sql-connection-string', keyVaultUrl: '${keyVaultUri}secrets/sql-connection-string', identity: uamiId }
      ]
    }
    template: {
      containers: [
        {
          name: 'migrator'
          image: containerImage
          command: [ 'dotnet' ]
          args: [ '/app/migrator/TelemetryGuard.MigrationRunner.dll' ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'MIGRATIONS_CONNECTIONSTRING', secretRef: 'sql-connection-string' }
          ]
        }
      ]
    }
  }
}

resource migrateClickHouseJob 'Microsoft.App/jobs@2024-03-01' = {
  name: migrateClickHouseJobName
  location: location
  identity: identityRef
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        acrRegistry
      ]
      secrets: [
        { name: 'clickhouse-connection-string', keyVaultUrl: '${keyVaultUri}secrets/clickhouse-connection-string', identity: uamiId }
      ]
    }
    template: {
      containers: [
        {
          name: 'migrator'
          image: containerImage
          command: [ 'dotnet' ]
          args: [ '/app/migrator/TelemetryGuard.MigrationRunner.dll', '--clickhouse' ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'CLICKHOUSE_CONNECTIONSTRING', secretRef: 'clickhouse-connection-string' }
          ]
        }
      ]
    }
  }
}

output apiFqdn string = apiApp.?properties.configuration.ingress.fqdn ?? ''
output containerAppName string = appName
output migrateSqlJobName string = migrateSqlJob.name
output migrateClickHouseJobName string = migrateClickHouseJob.name
