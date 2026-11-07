// Azure App Service + Azure Database for PostgreSQL Flexible Server for the Access Review portal.
// Sample template: adapt it to your environment (names, network, policies) before production use.
// Step-by-step guide: docs/DEPLOY-AZURE-APP-SERVICE.md
//
// Two modes:
//   privateNetwork = false (quick start)
//     - Linux App Service plan and a .NET 10 web app (code deployment)
//     - PostgreSQL Flexible Server with public access, firewall rule "Allow Azure services"
//     - the client secret and the connection string are app settings of the web app (encrypted at rest)
//   privateNetwork = true (recommended for production)
//     - VNet integration of the web app (all outbound traffic through the VNet)
//     - PostgreSQL Flexible Server in a delegated subnet (no public access)
//     - Key Vault with a private endpoint, no public access; the web app reads the secrets through Key Vault references
//       with a user-assigned managed identity that gets "Key Vault Secrets User" before the web app exists
//     - a new VNet and new private DNS zones, or your existing subnets and DNS zones (for example in a hub subscription)
//
// Database (databaseServer):
//   AzurePostgreSQL (default) - the template creates Azure Database for PostgreSQL Flexible Server and the database accessreview.
//     databaseAuthentication = ManagedIdentity (default): the user-assigned identity id-<appName> is the Microsoft Entra
//       administrator of this server and the portal signs in with it; the server accepts no passwords.
//     databaseAuthentication = Password: the portal signs in with the administrator login and password.
//   ExistingServer - your own PostgreSQL server, for example on a VM on-premises or in Azure. Give existingServerConnectionString
//     (login and password). Use privateNetwork = true with an existing subnet that has a network path to the server.
//
// Rights of the deploying user: docs/PERMISSIONS.md.

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

@description('Client secret of the app registration.')
@secure()
param clientSecret string

@description('AzurePostgreSQL: the template creates Azure Database for PostgreSQL Flexible Server. ExistingServer: your own PostgreSQL server, for example on a VM; the template creates no database server.')
@allowed(['AzurePostgreSQL', 'ExistingServer'])
param databaseServer string = 'AzurePostgreSQL'

@description('AzurePostgreSQL only. ManagedIdentity: the portal signs in with its managed identity (Microsoft Entra), no password. Password: administrator login and password.')
@allowed(['ManagedIdentity', 'Password'])
param databaseAuthentication string = 'ManagedIdentity'

@description('ExistingServer only: connection string with login and password, for example Host=pg01.contoso.com;Port=5432;Database=accessreview;Username=accessreview;Password=<password>;Ssl Mode=VerifyFull')
@secure()
param existingServerConnectionString string = ''

@description('ExistingServer only, optional: CA certificate of the server (base64 of the .cer file, or PEM) when the server certificate comes from your own CA. Needed for Ssl Mode=VerifyFull.')
param existingServerCaCertificate string = ''

@description('AzurePostgreSQL only: server name (globally unique, lower case).')
param postgresServerName string = 'psql-${appName}'

param postgresAdminLogin string = 'accessreviewadmin'

@description('AzurePostgreSQL with Password only: administrator password of the server. Do not use ; or quotes.')
@secure()
param postgresAdminPassword string = ''

@description('PostgreSQL compute size. See docs/SIZING.md.')
param postgresSkuName string = 'Standard_B2s'

@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param postgresSkuTier string = 'Burstable'

@description('PostgreSQL storage in GB (32, 64, 128, 256, 512, ...). Storage auto-grow is on.')
param postgresStorageSizeGB int = 32

@allowed(['16', '17', '18'])
param postgresVersion string = '18'

@description('Quick start only: your public IP address, to connect with psql or pgAdmin. Empty = no rule.')
param administratorClientIp string = ''

@description('false = quick start with public endpoints. true = private network (VNet integration, private PostgreSQL, Key Vault with a private endpoint).')
param privateNetwork bool = false

@description('Private network: Key Vault name (3-24 characters, globally unique). Default: a name derived from the resource group and the app name.')
@maxLength(24)
param keyVaultName string = 'kv-${uniqueString(resourceGroup().id, appName)}'

@description('Private network: purge protection of the Key Vault. With it, a deleted vault name stays reserved until the retention period ends.')
param enablePurgeProtection bool = true

@description('Private network, new VNet: address space (/24 or larger). Not used when you give existing subnets.')
param vnetAddressPrefix string = '10.20.0.0/24'

@description('Private network, existing VNet: subnet delegated to Microsoft.Web/serverFarms (/27 or larger).')
param appSubnetResourceId string = ''

@description('Private network, existing VNet: subnet delegated to Microsoft.DBforPostgreSQL/flexibleServers (/28 or larger).')
param postgresSubnetResourceId string = ''

@description('Private network, existing VNet: subnet for private endpoints.')
param privateEndpointSubnetResourceId string = ''

@description('Private network: existing private DNS zone for PostgreSQL, for example <name>.private.postgres.database.azure.com. Empty = create one in this resource group.')
param postgresPrivateDnsZoneResourceId string = ''

@description('Private network: existing private DNS zone privatelink.vaultcore.azure.net. Empty = create one in this resource group.')
param keyVaultPrivateDnsZoneResourceId string = ''

param tags object = {
  workload: 'access-review'
}

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var portalUrl = 'https://${appName}.azurewebsites.net'
var databaseName = 'accessreview'
var createPostgres = databaseServer == 'AzurePostgreSQL'
var useManagedIdentity = createPostgres && databaseAuthentication == 'ManagedIdentity'
// The user-assigned identity reads Key Vault (private network) and signs in to PostgreSQL (managed identity).
var useIdentity = privateNetwork || useManagedIdentity
var identityName = 'id-${appName}'
var createVnet = privateNetwork && empty(appSubnetResourceId)
var vnetName = 'vnet-${appName}'
var appSubnetId = createVnet ? resourceId('Microsoft.Network/virtualNetworks/subnets', vnetName, 'snet-app') : appSubnetResourceId
var postgresSubnetId = createVnet ? resourceId('Microsoft.Network/virtualNetworks/subnets', vnetName, 'snet-postgres') : postgresSubnetResourceId
var endpointSubnetId = createVnet ? resourceId('Microsoft.Network/virtualNetworks/subnets', vnetName, 'snet-private-endpoints') : privateEndpointSubnetResourceId
var vnetId = createVnet ? resourceId('Microsoft.Network/virtualNetworks', vnetName) : (privateNetwork ? split(appSubnetResourceId, '/subnets/')[0] : '')
var createPostgresZone = privateNetwork && createPostgres && empty(postgresPrivateDnsZoneResourceId)
var createVaultZone = privateNetwork && empty(keyVaultPrivateDnsZoneResourceId)
var postgresZoneName = '${postgresServerName}.private.postgres.database.azure.com'
var postgresZoneId = createPostgresZone ? postgresZone.id : postgresPrivateDnsZoneResourceId
var vaultZoneId = createVaultZone ? vaultZone.id : keyVaultPrivateDnsZoneResourceId

// ------------------------------------------------------------------------------------------------ network (private mode)
resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = if (createVnet) {
  name: vnetName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressPrefix]
    }
    subnets: [
      {
        name: 'snet-app'
        properties: {
          addressPrefix: cidrSubnet(vnetAddressPrefix, 26, 0)
          delegations: [{ name: 'web', properties: { serviceName: 'Microsoft.Web/serverFarms' } }]
        }
      }
      {
        name: 'snet-postgres'
        properties: {
          addressPrefix: cidrSubnet(vnetAddressPrefix, 28, 4)
          delegations: [{ name: 'postgres', properties: { serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers' } }]
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: cidrSubnet(vnetAddressPrefix, 28, 5)
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource postgresZone 'Microsoft.Network/privateDnsZones@2024-06-01' = if (createPostgresZone) {
  name: postgresZoneName
  location: 'global'
  tags: tags
}

resource postgresZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = if (createPostgresZone) {
  parent: postgresZone
  name: 'link-${appName}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnetId
    }
  }
  dependsOn: [vnet]
}

resource vaultZone 'Microsoft.Network/privateDnsZones@2024-06-01' = if (createVaultZone) {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
  tags: tags
}

resource vaultZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = if (createVaultZone) {
  parent: vaultZone
  name: 'link-${appName}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnetId
    }
  }
  dependsOn: [vnet]
}

// ------------------------------------------------------------------------------------------------ identity of the web app
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (useIdentity) {
  name: identityName
  location: location
  tags: tags
}

// ------------------------------------------------------------------------------------------------ database
module postgres 'postgres-flexible.bicep' = if (createPostgres) {
  name: 'postgres-${uniqueString(postgresServerName)}'
  params: {
    serverName: postgresServerName
    location: location
    postgresVersion: postgresVersion
    administratorLogin: postgresAdminLogin
    administratorPassword: postgresAdminPassword
    // Managed identity: Microsoft Entra sign-in only; the identity of the web app administers this server.
    passwordAuth: !useManagedIdentity
    entraAdministrators: useManagedIdentity
      ? [{ objectId: identity!.properties.principalId, principalName: identityName, principalType: 'ServicePrincipal' }]
      : []
    skuName: postgresSkuName
    skuTier: postgresSkuTier
    storageSizeGB: postgresStorageSizeGB
    databaseName: databaseName
    allowAzureServices: !privateNetwork
    allowedIpRanges: privateNetwork || empty(administratorClientIp) ? [] : [{ start: administratorClientIp, end: administratorClientIp }]
    delegatedSubnetResourceId: privateNetwork ? postgresSubnetId : ''
    privateDnsZoneArmResourceId: privateNetwork ? postgresZoneId : ''
    tags: tags
  }
  dependsOn: [vnet, postgresZoneLink]
}

var postgresHost = createPostgres ? postgres!.outputs.fqdn : ''
var connectionString = !createPostgres
  ? existingServerConnectionString
  : useManagedIdentity
      ? 'Host=${postgresHost};Port=5432;Database=${databaseName};Username=${identityName};Ssl Mode=VerifyFull'
      : 'Host=${postgresHost};Port=5432;Database=${databaseName};Username=${postgresAdminLogin};Password=${postgresAdminPassword};Ssl Mode=VerifyFull'

// ------------------------------------------------------------------------------------------------ secrets (private mode)
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = if (privateNetwork) {
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
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'None'
    }
  }
}

resource clientSecretValue 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (privateNetwork) {
  parent: vault
  name: 'portal-client-secret'
  properties: {
    value: clientSecret
    contentType: 'Client secret of the app registration'
  }
}

resource connectionStringValue 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (privateNetwork) {
  parent: vault
  name: 'portal-db-connection'
  properties: {
    value: connectionString
    contentType: 'Npgsql connection string'
  }
}

resource vaultEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (privateNetwork) {
  name: 'pe-${keyVaultName}'
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
  dependsOn: [vnet]
}

resource vaultEndpointDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (privateNetwork) {
  parent: vaultEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'vault'
        properties: {
          privateDnsZoneId: vaultZoneId
        }
      }
    ]
  }
  dependsOn: [vaultZoneLink]
}

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (privateNetwork) {
  name: guid(vault.id, identity.id, keyVaultSecretsUserRoleId)
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity!.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ------------------------------------------------------------------------------------------------ web app
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
  identity: useIdentity
    ? {
        type: 'UserAssigned'
        userAssignedIdentities: {
          '${identity.id}': {}
        }
      }
    : null
  // Private mode: the identity can read the secrets before the app settings are applied.
  dependsOn: [secretsUser, vaultEndpointDns]
  properties: {
    serverFarmId: plan.id
    keyVaultReferenceIdentity: privateNetwork ? identity.id : null
    httpsOnly: true
    // Blazor Server keeps the user session on one instance.
    clientAffinityEnabled: true
    virtualNetworkSubnetId: privateNetwork ? appSubnetId : null
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
      // Private mode: Key Vault references, PostgreSQL and the Microsoft APIs go through the VNet.
      vnetRouteAllEnabled: privateNetwork
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
    Database__Authentication: useManagedIdentity ? 'ManagedIdentity' : 'Password'
    Database__ManagedIdentityClientId: useManagedIdentity ? identity!.properties.clientId : ''
    Database__RootCertificate: createPostgres ? '' : existingServerCaCertificate
    Authentication__Mode: 'EntraId'
    Portal__PublicBaseUrl: portalUrl
    AzureAd__TenantId: tenantId
    AzureAd__ClientId: clientId
    AzureAd__ClientSecret: privateNetwork ? '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=portal-client-secret)' : clientSecret
    ConnectionStrings__DefaultConnection: privateNetwork ? '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=portal-db-connection)' : connectionString
  }
  dependsOn: [clientSecretValue, connectionStringValue]
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

// Deployments sign in with Microsoft Entra ID (az webapp deploy, GitHub Actions with OpenID Connect).
// Basic authentication (publishing user name and password) for the SCM site and FTP is off.
resource scmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: webApp
  name: 'scm'
  properties: {
    allow: false
  }
}

resource ftpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: webApp
  name: 'ftp'
  properties: {
    allow: false
  }
}

output portalUrl string = portalUrl
output webAppName string = webApp.name
output postgresFqdn string = postgresHost
@description('User name of the portal in PostgreSQL. Managed identity: the identity name.')
output postgresUser string = useManagedIdentity ? identityName : (createPostgres ? postgresAdminLogin : '')
output keyVaultName string = privateNetwork ? keyVaultName : ''
output outboundIpAddresses string = webApp.properties.outboundIpAddresses
