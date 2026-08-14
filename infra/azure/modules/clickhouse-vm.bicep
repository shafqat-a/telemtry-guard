// P2-04 / D6 decision 1: self-hosted single-node ClickHouse on a private-subnet VM,
// running the SAME pinned image (clickhouse/clickhouse-server:24.8) as
// docker-compose.yml — dev, CI (Testcontainers) and prod all run the identical
// engine version, which is what keeps the ANA-06 contract suite meaningful here.
// No public IP; the NSG on snet-data (network.bicep) allows 8123/9000 from
// snet-infra only. No SSH rule — administration is `az vm run-command invoke`.
param location string
param namePrefix string
param environmentName string
param dataSubnetId string
param clickHousePrivateIp string
param clickHouseDb string
@secure()
param clickHousePassword string
@description('OS admin username. No SSH inbound rule exists (network.bicep) and administration is az vm run-command, so this account is never reachable over the network — it exists only to satisfy the VM osProfile.')
param vmAdminUsername string = 'chadmin'

var vmName = '${namePrefix}-ch-${environmentName}'
var vmSize = environmentName == 'prod' ? 'Standard_D4as_v5' : 'Standard_D2as_v5'
// __CH_DB__ / __CH_PASSWORD__ placeholders substituted into the shared cloud-init
// template (same file used to seed the docker run env vars as docker-compose.yml).
var customData = base64(replace(replace(loadTextContent('../cloud-init/clickhouse.yaml'), '__CH_PASSWORD__', clickHousePassword), '__CH_DB__', clickHouseDb))

resource nic 'Microsoft.Network/networkInterfaces@2023-11-01' = {
  name: '${vmName}-nic'
  location: location
  properties: {
    ipConfigurations: [
      {
        name: 'ipconfig1'
        properties: {
          privateIPAllocationMethod: 'Static'
          privateIPAddress: clickHousePrivateIp
          subnet: {
            id: dataSubnetId
          }
        }
      }
    ]
  }
}

resource vm 'Microsoft.Compute/virtualMachines@2023-09-01' = {
  name: vmName
  location: location
  properties: {
    hardwareProfile: {
      vmSize: vmSize
    }
    storageProfile: {
      imageReference: {
        publisher: 'Canonical'
        offer: '0001-com-ubuntu-server-jammy'
        sku: '22_04-lts-gen2'
        version: 'latest'
      }
      osDisk: {
        createOption: 'FromImage'
        diskSizeGB: 64
        managedDisk: {
          storageAccountType: 'Premium_LRS'
        }
      }
      dataDisks: [
        {
          lun: 0
          createOption: 'Empty'
          diskSizeGB: 256
          caching: 'None'
          managedDisk: {
            storageAccountType: 'Premium_LRS'
          }
        }
      ]
    }
    osProfile: {
      computerName: vmName
      adminUsername: vmAdminUsername
      // No public IP, no SSH inbound NSG rule (network.bicep) — administration is
      // `az vm run-command invoke`, which uses the Azure VM Agent control-plane
      // channel and needs neither a password nor an SSH key. disablePasswordAuthentication
      // is set for defense-in-depth; if a real deployment's ARM validation demands an
      // explicit auth method, add an `sshPublicKey` parameter + Bastion (both
      // intentionally out of scope for this task — see the runbook).
      linuxConfiguration: {
        disablePasswordAuthentication: true
        patchSettings: {
          patchMode: 'ImageDefault'
        }
      }
      customData: customData
    }
    networkProfile: {
      networkInterfaces: [
        {
          id: nic.id
        }
      ]
    }
  }
}

output vmName string = vm.name
