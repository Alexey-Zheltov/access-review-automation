// Existing Key Vault of the Access Review portal (used by appservice-existing-network.bicep). Deployed to the resource group of the vault.
// Adds the client secret and the database connection string, and gives the managed identity of the web app the role
// "Key Vault Secrets User" (Azure RBAC): on these two secrets only, or on the whole vault. Changes nothing else in the vault.
// The vault must use Azure RBAC (permission model "Azure role-based access control"): with access policies the role has no effect.

param vaultName string

@secure()
param clientSecret string

@secure()
param connectionString string

param clientSecretName string
param connectionSecretName string

@description('Secrets = the role on the two secrets only (for a vault that other apps also use). Vault = the role on the whole vault.')
@allowed(['Secrets', 'Vault'])
param access string = 'Secrets'

@description('Resource id of the user-assigned managed identity of the web app.')
param identityId string

@description('Principal id of the same identity.')
param identityPrincipalId string

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource clientSecretValue 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: clientSecretName
  properties: {
    value: clientSecret
    contentType: 'Client secret of the app registration (Access Review portal)'
  }
}

resource connectionStringValue 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: connectionSecretName
  properties: {
    value: connectionString
    contentType: 'Npgsql connection string (Access Review portal)'
  }
}

resource vaultReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (access == 'Vault') {
  name: guid(vault.id, identityId, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource clientSecretReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (access == 'Secrets') {
  name: guid(clientSecretValue.id, identityId, keyVaultSecretsUserRoleId)
  scope: clientSecretValue
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource connectionStringReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (access == 'Secrets') {
  name: guid(connectionStringValue.id, identityId, keyVaultSecretsUserRoleId)
  scope: connectionStringValue
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output name string = vault.name
output id string = vault.id
