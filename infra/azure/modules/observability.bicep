// P2-04 / D16: Log Analytics + workspace-based Application Insights. The Container
// Apps environment ships console logs here (app's AddJsonConsole -> ContainerAppConsoleLogs_CL,
// no agent needed); the OTel Collector sidecar (containerapps.bicep) exports traces/metrics
// to this App Insights resource via the azuremonitor exporter — zero app-code change.
param location string
param namePrefix string
param environmentName string

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-law-${environmentName}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-appi-${environmentName}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
  }
}

output workspaceId string = workspace.id
output workspaceCustomerId string = workspace.properties.customerId
@secure()
output workspaceSharedKey string = workspace.listKeys().primarySharedKey
@secure()
output appInsightsConnectionString string = appInsights.properties.ConnectionString
