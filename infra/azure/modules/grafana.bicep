// P2-04 / D16: optional Azure Managed Grafana. Only deployed when main.bicep's
// deployGrafana=true (default false, to keep the dev bill honest). Dashboards are
// NOT provisioned here — the runbook documents `az grafana dashboard create` against
// ops/grafana/dashboards/*.json plus the ClickHouse datasource, and notes that
// reaching the VNet-private ClickHouse VM needs either Managed Grafana VNet
// integration + an NSG allow rule, or an operator-side tunnel.
param location string
param namePrefix string
param environmentName string

resource grafana 'Microsoft.Dashboard/grafana@2023-09-01' = {
  name: '${namePrefix}-grafana-${environmentName}'
  location: location
  sku: {
    name: 'Standard'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    zoneRedundancy: 'Disabled'
  }
}

output grafanaEndpoint string = grafana.properties.endpoint
