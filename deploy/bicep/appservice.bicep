// Azure App Service + Azure Database for PostgreSQL Flexible Server + Azure Key Vault for the Access Review portal.
// Sample template: adapt it to your environment (names, network, policies) before production use.
// Step-by-step guide: docs/DEPLOY-AZURE-APP-SERVICE.md
//
// What it creates:
//   - Key Vault (Azure RBAC mode) with two secrets: the client secret of the app registration and the database connection string
//   - a user-assigned managed identity with "Key Vault Secrets User" on the vault. It gets the role before the web app
//     exists, so the Key Vault references in the app settings resolve at the first start.
//   - Linux App Service plan and a .NET 10 web app (code deployment) that uses this identity for Key Vault references
//   - PostgreSQL Flexible Server and the database (module postgres-flexible.bicep)
//
// The secrets are written through Azure Resource Manager, so the deploying user needs no Key Vault data-plane role.
// The deploying user needs Contributor and a right to create role assignments on the resource group (see docs/PERMISSIONS.md).

@description('Web app name. The portal address is https://<appName>.azurewebsites.net. Must be globally unique.')
@minLength(2)
@maxLength(60)
param appName string

param location string = resourceGroup().location

@description('App Service plan size. See docs/SIZING.md.')
@allowed(['B1', 'B2', 'B3', 'P0v3', 'P1v3', 'P2v3', 'P3v3', 'P1mv3', 'P2mv3'])
param appServicePlanSku string = 'P0v3'

@description('Microsoft Entra tenant id of the app registration.')
param tenantId string = subscription().tenantId

@description('Application (client) id of the app registration (setup-sp.ps1 prints it).')
param clientId string

@description('Client secret of the app registration. Stored in Key Vault only.')
@secure()
param clientSecret string

@description('Key Vault name (3-24 characters, globally unique).')
@minLength(3)
@maxLength(24)
param keyVaultName string

@description('Purge protection of the Key Vault. Many landing zone policies require it. With it, a deleted vault name stays reserved until the retention period ends.')
param enablePurgeProtection bool = true

@description('PostgreSQL server name (globally unique, lower case).')
param postgresServerName string

param postgresAdminLogin string = 'accessreviewadmin'

@description('Administrator password of the PostgreSQL server. Do not use ; or quotes.')
@secure()
param postgresAdminPassword string

@description('PostgreSQL compute size. See docs/SIZING.md.')
param postgresSkuName string = 'Standard_B2s'

@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param postgresSkuTier string = 'Burstable'

@description('PostgreSQL storage in GB (32, 64, 128, 256, 512, ...). Storage auto-grow is on.')
param postgresStorageSizeGB int = 32

@allowed(['16', '17', '18'])
param postgresVersion string = '18'

@description('Public access only: your client IP address, to connect with psql or pgAdmin for administration. Empty = no rule.')
param administratorClientIp string = ''

@description('Private network: subnet delegated to Microsoft.Web/serverFarms for the VNet integration of the web app. Empty = no VNet integration.')
param appSubnetResourceId string = ''

@description('Private network: subnet delegated to Microsoft.DBforPostgreSQL/flexibleServers. Empty = public access with firewall rules.')
param postgresDelegatedSubnetResourceId string = ''

@description('Private network: private DNS zone for the server, for example privatelink.postgres.database.azure.com, linked to the VNet.')
param postgresPrivateDnsZoneResourceId string = ''

param tags object = {
  workload: 'access-review'
}

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var portalUrl = 'https://${appName}.azurewebsites.net'
var databaseName = 'accessreview'
var privateDatabase = !empty(postgresDelegatedSubnetResourceId)

module postgres 'postgres-flexible.bicep' = {
  name: 'postgres-${uniqueString(postgresServerName)}'
  params: {
    serverName: postgresServerName
    location: location
    postgresVersion: postgresVersion
    administratorLogin: postgresAdminLogin
    administratorPassword: postgresAdminPassword
    skuName: postgresSkuName
    skuTier: postgresSkuTier
    storageSizeGB: postgresStorageSizeGB
    databaseName: databaseName
    allowAzureServices: !privateDatabase
    allowedIpRanges: empty(administratorClientIp) ? [] : [{ start: administratorClientIp, end: administratorClientIp }]
    delegatedSubnetResourceId: postgresDelegatedSubnetResourceId
    privateDnsZoneArmResourceId: postgresPrivateDnsZoneResourceId
    tags: tags
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
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
    publicNetworkAccess: 'Enabled'
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
    value: 'Host=${postgres.outputs.fqdn};Port=5432;Database=${databaseName};Username=${postgresAdminLogin};Password=${postgresAdminPassword};Ssl Mode=VerifyFull'
    contentType: 'Npgsql connection string'
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${appName}'
  location: location
  tags: tags
}

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-${appName}'
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: appServicePlanSku
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  // The identity must be able to read the secrets before the app settings are applied.
  dependsOn: [secretsUser]
  properties: {
    serverFarmId: plan.id
    keyVaultReferenceIdentity: identity.id
    httpsOnly: true
    // Blazor Server keeps the user session on one instance.
    clientAffinityEnabled: true
    virtualNetworkSubnetId: empty(appSubnetResourceId) ? null : appSubnetResourceId
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: 'dotnet AccessReviewPortal.dll'
      // The monthly and quarterly schedules run inside the app, so it must not be unloaded when idle.
      alwaysOn: true
      webSocketsEnabled: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/healthz'
      vnetRouteAllEnabled: false
    }
  }
}

resource appSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: webApp
  name: 'appsettings'
  properties: {
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
    // One instance: the schema is migrated when the app starts.
    Database__ApplyMigrationsOnStartup: 'true'
    Authentication__Mode: 'EntraId'
    Portal__PublicBaseUrl: portalUrl
    AzureAd__TenantId: tenantId
    AzureAd__ClientId: clientId
    AzureAd__ClientSecret: '@Microsoft.KeyVault(VaultName=${vault.name};SecretName=${clientSecretValue.name})'
    ConnectionStrings__DefaultConnection: '@Microsoft.KeyVault(VaultName=${vault.name};SecretName=${connectionStringValue.name})'
  }
}

resource logs 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: webApp
  name: 'logs'
  properties: {
    applicationLogs: {
      fileSystem: {
        level: 'Information'
      }
    }
    httpLogs: {
      fileSystem: {
        enabled: true
        retentionInDays: 7
        retentionInMb: 35
      }
    }
  }
}

output portalUrl string = portalUrl
output webAppName string = webApp.name
output identityPrincipalId string = identity.properties.principalId
output keyVaultName string = vault.name
output postgresFqdn string = postgres.outputs.fqdn
