// Existing Azure Container Registry of the Access Review portal (used by appservice-existing-network.bicep with
// deploymentType = Container). Deployed to the resource group of the registry. Changes nothing in the registry: it gives the
// managed identity of the web app the pull role, AcrPull, or Container Registry Repository Reader when the registry uses
// ABAC repository permissions (az acr show --query roleAssignmentMode returns AbacRepositoryPermissions).

param registryName string

@description('Resource id of the user-assigned managed identity of the web app.')
param identityId string

@description('Principal id of the same identity.')
param identityPrincipalId string

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var repositoryReaderRoleId = 'b93aa761-3e63-49ed-ac28-beffa264f7ac'

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' existing = {
  name: registryName
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

output loginServer string = registry.properties.loginServer
