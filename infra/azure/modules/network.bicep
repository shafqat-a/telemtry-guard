// P2-04: VNet with two subnets — snet-infra (delegated to the Container Apps
// managed environment) and snet-data (the ClickHouse VM, NSG-locked to snet-infra
// only). No public ingress to snet-data; the VM has no public IP (see clickhouse-vm.bicep).
param location string
param namePrefix string
param environmentName string

var vnetName = '${namePrefix}-vnet-${environmentName}'
var infraSubnetPrefix = '10.20.0.0/23'
var dataSubnetPrefix = '10.20.2.0/24'

resource nsgData 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: '${namePrefix}-nsg-data-${environmentName}'
  location: location
  properties: {
    securityRules: [
      {
        name: 'AllowClickHouseFromInfra'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: infraSubnetPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRanges: [
            '8123'
            '9000'
          ]
        }
      }
      {
        // Redundant with the platform default deny at priority 4096, added explicitly
        // so the intent is legible in the portal/reviews (D13/D6 origin-lockdown spirit
        // applied to the analytics tier too).
        name: 'DenyInternetInbound'
        properties: {
          priority: 200
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: 'Internet'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: vnetName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.20.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'snet-infra'
        properties: {
          addressPrefix: infraSubnetPrefix
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'snet-data'
        properties: {
          addressPrefix: dataSubnetPrefix
          networkSecurityGroup: {
            id: nsgData.id
          }
        }
      }
    ]
  }
}

output infraSubnetId string = vnet.properties.subnets[0].id
output dataSubnetId string = vnet.properties.subnets[1].id
