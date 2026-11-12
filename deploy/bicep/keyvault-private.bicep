// Key Vault of the Access Review portal with a private endpoint and no public access (used by appservice-existing-network.bicep).
// Stores the client secret and the database connection string, and gives the managed identity of the web app
// the role "Key Vault Secrets User" on this vault only. Deployed to the resource group that the caller chooses.

param vaultName string
param location string
param tags object
param enablePurgeProtection bool

@secure()
param clientSecret string

@secure()
param connectionString string

@description('Subnet of the private endpoint of the vault.')
param endpointSubnetId string

@description('Private DNS zone privatelink.vaultcore.azure.net.')
param privateDnsZoneId string

@description('Resource id of the user-assigned managed identity of the web app.')
param identityId string

@description('Principal id of the same identity.')
param identityPrincipalId string

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: enablePurgeProtection ? true : null
    // Only through the private endpoint, no exceptions for trusted services.
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
    }
  }
}

resource clientSecretValue 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'portal-client-secret'
  properties: {
    value: clientSecret
    contentType: 'Client secret of the app registration'
  }
}

resource connectionStringValue 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'portal-db-connection'
  properties: {
    value: connectionString
    contentType: 'Npgsql connection string'
  }
}

resource vaultEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${vaultName}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: endpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'vault'
        properties: {
          privateLinkServiceId: vault.id
          groupIds: ['vault']
        }
      }
    ]
  }
}

resource vaultEndpointDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: vaultEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'vault'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identityId, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output name string = vault.name
output id string = vault.id
