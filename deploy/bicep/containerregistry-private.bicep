// Azure Container Registry of the Access Review portal (used by appservice-existing-network.bicep with deploymentType = Container).
// Premium (needed for private endpoints), with a private endpoint, no public access, no admin user and no anonymous pull.
// Trusted Microsoft services keep access, so Microsoft Defender for Cloud can scan the images.
// The managed identity of the web app gets the pull role: AcrPull, or Container Registry Repository Reader when the registry
// uses ABAC repository permissions.

param registryName string
param location string
param tags object

@description('Subnet of the private endpoint of the registry.')
param endpointSubnetId string

@description('Private DNS zone privatelink.azurecr.io.')
param privateDnsZoneId string

@description('Resource id of the user-assigned managed identity of the web app.')
param identityId string

@description('Principal id of the same identity.')
param identityPrincipalId string

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var repositoryReaderRoleId = 'b93aa761-3e63-49ed-ac28-beffa264f7ac'

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' = {
  name: registryName
  location: location
  tags: tags
  sku: {
    name: 'Premium'
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Disabled'
    // Trusted Microsoft services, for example the image scan of Microsoft Defender for Cloud, reach the registry despite the rules.
    networkRuleBypassOptions: 'AzureServices'
  }
}

// Records for the registry and its data endpoint in privatelink.azurecr.io.
resource registryEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${registryName}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: endpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'registry'
        properties: {
          privateLinkServiceId: registry.id
          groupIds: ['registry']
        }
      }
    ]
  }
}

resource registryEndpointDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: registryEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'registry'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identityId, 'pull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      registry.properties.?roleAssignmentMode == 'AbacRepositoryPermissions' ? repositoryReaderRoleId : acrPullRoleId
    )
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output name string = registry.name
output id string = registry.id
output loginServer string = registry.properties.loginServer
