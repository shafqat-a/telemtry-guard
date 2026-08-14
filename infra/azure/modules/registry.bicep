// P2-04: ACR (image + OTel Collector import target) plus the single user-assigned
// managed identity shared by the API Container App and both migration jobs — pull
// is via this identity (adminUserEnabled: false), never admin credentials.
//
// Deliberately SELF-NAMING (computes acrName from its own params rather than
// taking a precomputed name from main.bicep): deploy.yml's `build-push` job needs
// a real ACR to `az acr login`/push to BEFORE the rest of main.bicep ever runs
// (the migration jobs it also creates need containerImage, which needs the image
// already pushed) — a chicken-and-egg gap in a naive "one Bicep template creates
// everything" design. Because this module derives the same name deterministically
// whether deployed standalone (deploy.yml's bootstrap step, idempotent — a rerun
// via the full main.bicep just reconciles the existing resource) or as part of the
// full orchestration, both paths agree without cross-wiring a name.
param location string
param namePrefix string
param environmentName string

var acrName = toLower('${namePrefix}acr${environmentName}${uniqueString(resourceGroup().id)}')
var uamiName = '${namePrefix}-id-${environmentName}'

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: uamiName
  location: location
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

// AcrPull built-in role: 7f951dda-4ed3-4680-a7ca-43fe172d538d
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, uami.id, 'AcrPull')
  scope: acr
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

output acrLoginServer string = acr.properties.loginServer
output acrId string = acr.id
output uamiId string = uami.id
output uamiPrincipalId string = uami.properties.principalId
