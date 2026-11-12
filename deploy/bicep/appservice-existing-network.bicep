// Access Review portal in your existing virtual network: Azure App Service + Azure Database for PostgreSQL Flexible Server,
// reachable only through private endpoints.
// Sample template: adapt it to your environment (names, network, policies) before production use.
// Step-by-step guide: docs/DEPLOY-AZURE-APP-SERVICE.md, option D.
//
// The template creates no virtual network and no subnets. You give two existing subnets in the region of the deployment:
//   - app subnet: VNet integration of the web app. All outbound traffic of the web app goes through your network.
//     Only for App Service: delegated to Microsoft.Web/serverFarms before the deployment. /28 or larger, /26 recommended.
//   - private endpoint subnet: the private endpoints of the web app, PostgreSQL and Key Vault.
// No public endpoints:
//   - web app: private endpoint, public network access disabled. Users, deployments and the Kudu console work only from
//     your network. Your DNS must resolve <appName>.azurewebsites.net and <appName>.scm.azurewebsites.net to the private endpoint.
//   - PostgreSQL Flexible Server: private endpoint, public network access disabled.
//   - Key Vault (new, in this resource group or in keyVaultResourceGroup): private endpoint, public network access disabled.
//     The web app reads the client secret and the connection string through Key Vault references with the user-assigned
//     managed identity id-<appName>, which gets Key Vault Secrets User on this vault (keyvault-private.bicep).
// Private DNS zones privatelink.azurewebsites.net, privatelink.postgres.database.azure.com, privatelink.vaultcore.azure.net:
// your existing zones (for example in a hub subscription), or new zones in this resource group, linked to the VNet of the app subnet.
//
// Database (databaseServer):
//   AzurePostgreSQL (default) - the template creates the server and the database accessreview.
//     databaseAuthentication = ManagedIdentity (default): id-<appName> is the Microsoft Entra administrator of the server and the
//       portal signs in with it; the server accepts no passwords.
//     databaseAuthentication = Password: the portal signs in with the administrator login and password.
//   ExistingServer - your own PostgreSQL server. Give existingServerConnectionString; the app subnet needs a network path to it.
//
// Azure portal form with lists of your virtual networks, subnets and private DNS zones:
// appservice-existing-network.createUiDefinition.json. Rights of the deploying user: docs/PERMISSIONS.md.

@description('Web app name. The portal address is https://<appName>.azurewebsites.net. Must be globally unique.')
@minLength(2)
@maxLength(58)
param appName string

@description('Region of your virtual network. The web app, PostgreSQL, Key Vault and the private endpoints are created in this region.')
param location string = resourceGroup().location

@description('App Service plan size. See docs/SIZING.md.')
@allowed(['B1', 'B2', 'B3', 'P0v3', 'P1v3', 'P2v3', 'P3v3', 'P1mv3', 'P2mv3'])
param appServicePlanSku string = 'P0v3'

@description('Zone redundancy of the App Service plan: two instances in different availability zones. Azure chooses the zones; App Service has no setting for them. Needs a Premium plan (P0v3 or larger) and a region with availability zones. The portal supports more than one instance.')
param appServiceZoneRedundant bool = false

@description('Microsoft Entra tenant id of the app registration.')
param tenantId string = subscription().tenantId

@description('Application (client) id of the app registration (setup-sp.ps1 prints it).')
param clientId string

@description('Client secret of the app registration.')
@secure()
param clientSecret string

@description('Resource id of an existing subnet for the VNet integration of the web app: /subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/<subnet>. Only for App Service and delegated to Microsoft.Web/serverFarms (the deployment does not change your subnets). /28 or larger, /26 recommended.')
param appSubnetResourceId string

@description('Resource id of an existing subnet for the private endpoints of the web app, PostgreSQL and Key Vault.')
param privateEndpointSubnetResourceId string

@description('Existing private DNS zone privatelink.azurewebsites.net, for example in a hub subscription. Empty = create one in this resource group and link it to the VNet of the app subnet.')
param webAppPrivateDnsZoneResourceId string = ''

@description('AzurePostgreSQL only: existing private DNS zone privatelink.postgres.database.azure.com. Empty = create one in this resource group and link it to the VNet of the app subnet.')
param postgresPrivateDnsZoneResourceId string = ''

@description('Existing private DNS zone privatelink.vaultcore.azure.net. Empty = create one in this resource group and link it to the VNet of the app subnet.')
param keyVaultPrivateDnsZoneResourceId string = ''

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

@description('AzurePostgreSQL only: server name (globally unique, lower case). Empty = psql-<appName>.')
param postgresServerName string = ''

param postgresAdminLogin string = 'accessreviewadmin'

@description('AzurePostgreSQL with Password only: administrator password of the server. Do not use ; or quotes.')
@secure()
param postgresAdminPassword string = ''

@description('PostgreSQL compute size. The tier follows from the name: Standard_B = Burstable, Standard_D = General Purpose, Standard_E = Memory Optimized. Burstable sizes are not available for every subscription in every region (SkuNotAvailable). See docs/SIZING.md.')
@allowed([
  'Standard_B1ms'
  'Standard_B2s'
  'Standard_B2ms'
  'Standard_D2ds_v5'
  'Standard_D4ds_v5'
  'Standard_D8ds_v5'
  'Standard_D2ads_v5'
  'Standard_D4ads_v5'
  'Standard_E2ds_v5'
  'Standard_E4ds_v5'
])
param postgresSkuName string = 'Standard_D2ds_v5'

@description('PostgreSQL storage in GB (32, 64, 128, 256, 512, ...). Storage auto-grow is on.')
param postgresStorageSizeGB int = 32

@allowed(['16', '17', '18'])
param postgresVersion string = '18'

@description('High availability of PostgreSQL: ZoneRedundant = standby server in another availability zone, SameZone = standby in the same zone. Not with Burstable sizes. The standby doubles the compute cost.')
@allowed(['Disabled', 'ZoneRedundant', 'SameZone'])
param postgresHighAvailability string = 'Disabled'

@description('Availability zone of the PostgreSQL server: 1, 2 or 3. Empty = Azure chooses. Used only when Azure creates the server: a later deployment with another zone does not move the server.')
@allowed(['', '1', '2', '3'])
param postgresAvailabilityZone string = ''

@description('With postgresHighAvailability = ZoneRedundant: availability zone of the standby server, 1, 2 or 3, another zone than postgresAvailabilityZone. Empty = Azure chooses.')
@allowed(['', '1', '2', '3'])
param postgresStandbyAvailabilityZone string = ''

@description('Key Vault name (3-24 characters, globally unique). Empty = a name derived from the resource group and the app name.')
@maxLength(24)
param keyVaultName string = ''

@description('Resource group of the Key Vault, in the same subscription. It must exist. Empty = the resource group of the deployment. The Key Vault always has a private endpoint and no public access.')
param keyVaultResourceGroup string = ''

@description('Purge protection of the Key Vault. With it, a deleted vault name stays reserved until the retention period ends.')
param enablePurgeProtection bool = true

param tags object = {
  workload: 'access-review'
}

var portalUrl = 'https://${appName}.azurewebsites.net'
var databaseName = 'accessreview'
var createPostgres = databaseServer == 'AzurePostgreSQL'
var useManagedIdentity = createPostgres && databaseAuthentication == 'ManagedIdentity'
var identityName = 'id-${appName}'
var postgresSkuTier = startsWith(postgresSkuName, 'Standard_B') ? 'Burstable' : (startsWith(postgresSkuName, 'Standard_E') ? 'MemoryOptimized' : 'GeneralPurpose')
var serverName = empty(postgresServerName) ? 'psql-${appName}' : postgresServerName
var vaultName = empty(keyVaultName) ? 'kv-${uniqueString(resourceGroup().id, appName)}' : keyVaultName
var vaultResourceGroup = empty(keyVaultResourceGroup) ? resourceGroup().name : keyVaultResourceGroup

// Both subnets must be subnet resource ids. The check stops the deployment before anything is created.
var subnetIdPart = '/providers/microsoft.network/virtualnetworks/'
var badSubnetParameters = concat(
  contains(toLower(appSubnetResourceId), subnetIdPart) && contains(toLower(appSubnetResourceId), '/subnets/')
    ? []
    : ['appSubnetResourceId'],
  contains(toLower(privateEndpointSubnetResourceId), subnetIdPart) && contains(toLower(privateEndpointSubnetResourceId), '/subnets/')
    ? []
    : ['privateEndpointSubnetResourceId']
)
var appSubnetId = empty(badSubnetParameters)
  ? appSubnetResourceId
  : fail('Give the resource id of an existing subnet (/subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/<subnet>) in: ${join(badSubnetParameters, ', ')}')
var endpointSubnetId = privateEndpointSubnetResourceId

// Combinations that Azure rejects. The check stops the deployment before anything is created.
var badOptions = concat(
  appServiceZoneRedundant && !startsWith(appServicePlanSku, 'P') ? ['appServiceZoneRedundant needs a Premium plan (P0v3 or larger)'] : [],
  createPostgres && postgresHighAvailability != 'Disabled' && postgresSkuTier == 'Burstable' ? ['postgresHighAvailability needs a General Purpose or Memory Optimized size, not Burstable'] : [],
  createPostgres && !empty(postgresStandbyAvailabilityZone) && postgresHighAvailability != 'ZoneRedundant' ? ['postgresStandbyAvailabilityZone needs postgresHighAvailability = ZoneRedundant'] : [],
  createPostgres && !empty(postgresStandbyAvailabilityZone) && postgresStandbyAvailabilityZone == postgresAvailabilityZone ? ['postgresStandbyAvailabilityZone must be another zone than postgresAvailabilityZone'] : []
)
var planSku = empty(badOptions) ? appServicePlanSku : fail('Change these options: ${join(badOptions, '; ')}')
// lastIndexOf is not case-sensitive.
var vnetId = substring(appSubnetId, 0, lastIndexOf(appSubnetId, '/subnets/'))

var createSiteZone = empty(webAppPrivateDnsZoneResourceId)
var createPostgresZone = createPostgres && empty(postgresPrivateDnsZoneResourceId)
var createVaultZone = empty(keyVaultPrivateDnsZoneResourceId)
var siteZoneId = createSiteZone ? siteZone.id : webAppPrivateDnsZoneResourceId
var postgresZoneId = createPostgresZone ? postgresZone.id : postgresPrivateDnsZoneResourceId
var vaultZoneId = createVaultZone ? vaultZone.id : keyVaultPrivateDnsZoneResourceId

// PostgreSQL is created first. Some sizes are not available for every subscription in every region, and Azure reports this
// (SkuNotAvailable) only when it creates the server. So the zones, the plan and everything after them wait for the server:
// with an unavailable size the deployment stops after about two minutes and creates nothing else but the identity.

// ------------------------------------------------------------------------------------------------ private DNS zones (new)
resource siteZone 'Microsoft.Network/privateDnsZones@2024-06-01' = if (createSiteZone) {
  name: 'privatelink.azurewebsites.net'
  location: 'global'
  tags: tags
  dependsOn: [postgres]
}

resource siteZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = if (createSiteZone) {
  parent: siteZone
  name: 'link-${appName}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnetId
    }
  }
}

resource postgresZone 'Microsoft.Network/privateDnsZones@2024-06-01' = if (createPostgresZone) {
  name: 'privatelink.postgres.database.azure.com'
  location: 'global'
  tags: tags
  dependsOn: [postgres]
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
}

resource vaultZone 'Microsoft.Network/privateDnsZones@2024-06-01' = if (createVaultZone) {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
  tags: tags
  dependsOn: [postgres]
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
}

// ------------------------------------------------------------------------------------------------ identity of the web app
// Reads Key Vault, and signs in to PostgreSQL with databaseAuthentication = ManagedIdentity.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

// ------------------------------------------------------------------------------------------------ database
module postgres 'postgres-flexible.bicep' = if (createPostgres) {
  name: take('postgres-${serverName}', 64)
  params: {
    serverName: serverName
    location: location
    postgresVersion: postgresVersion
    administratorLogin: postgresAdminLogin
    administratorPassword: postgresAdminPassword
    // Managed identity: Microsoft Entra sign-in only; the identity of the web app administers this server.
    passwordAuth: !useManagedIdentity
    entraAdministrators: useManagedIdentity
      ? [{ objectId: identity.properties.principalId, principalName: identityName, principalType: 'ServicePrincipal' }]
      : []
    skuName: postgresSkuName
    skuTier: postgresSkuTier
    storageSizeGB: postgresStorageSizeGB
    highAvailability: postgresHighAvailability
    availabilityZone: postgresAvailabilityZone
    standbyAvailabilityZone: postgresStandbyAvailabilityZone
    databaseName: databaseName
    publicNetworkAccess: false
    allowAzureServices: false
    tags: tags
  }
}

// After the server, its database, parameters and administrators: the server accepts one operation at a time.
resource postgresEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (createPostgres) {
  name: 'pe-${serverName}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: endpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'postgresqlServer'
        properties: {
          privateLinkServiceId: postgres!.outputs.id
          groupIds: ['postgresqlServer']
        }
      }
    ]
  }
}

resource postgresEndpointDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (createPostgres) {
  parent: postgresEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'postgres'
        properties: {
          privateDnsZoneId: postgresZoneId
        }
      }
    ]
  }
  dependsOn: [postgresZoneLink]
}

// The host name resolves to the private endpoint (CNAME to <server>.privatelink.postgres.database.azure.com).
var postgresHost = createPostgres ? postgres!.outputs.fqdn : ''
var connectionString = !createPostgres
  ? existingServerConnectionString
  : useManagedIdentity
      ? 'Host=${postgresHost};Port=5432;Database=${databaseName};Username=${identityName};Ssl Mode=VerifyFull'
      : 'Host=${postgresHost};Port=5432;Database=${databaseName};Username=${postgresAdminLogin};Password=${postgresAdminPassword};Ssl Mode=VerifyFull'

// ------------------------------------------------------------------------------------------------ secrets
// Key Vault with a private endpoint only (public network access disabled), in the resource group keyVaultResourceGroup.
module keyVault 'keyvault-private.bicep' = {
  name: take('keyvault-${vaultName}', 64)
  scope: resourceGroup(vaultResourceGroup)
  params: {
    vaultName: vaultName
    location: location
    tags: tags
    enablePurgeProtection: enablePurgeProtection
    clientSecret: clientSecret
    connectionString: connectionString
    endpointSubnetId: endpointSubnetId
    privateDnsZoneId: vaultZoneId
    identityId: identity.id
    identityPrincipalId: identity.properties.principalId
  }
  dependsOn: [vaultZoneLink]
}
// ------------------------------------------------------------------------------------------------ web app
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-${appName}'
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: planSku
    // Zone redundancy needs at least two instances.
    capacity: appServiceZoneRedundant ? 2 : 1
  }
  properties: {
    reserved: true
    zoneRedundant: appServiceZoneRedundant
  }
  dependsOn: [postgres]
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
  // The identity can read the secrets before the app settings are applied.
  dependsOn: [keyVault]
  properties: {
    serverFarmId: plan.id
    keyVaultReferenceIdentity: identity.id
    httpsOnly: true
    // Only through the private endpoint: users, deployments (SCM) and the Kudu console.
    publicNetworkAccess: 'Disabled'
    // Blazor Server keeps the user session on one instance.
    clientAffinityEnabled: true
    virtualNetworkSubnetId: appSubnetId
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
      // Key Vault references, PostgreSQL and the Microsoft APIs go through the VNet.
      vnetRouteAllEnabled: true
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
    Database__ManagedIdentityClientId: useManagedIdentity ? identity.properties.clientId : ''
    Database__RootCertificate: createPostgres ? '' : existingServerCaCertificate
    Authentication__Mode: 'EntraId'
    Portal__PublicBaseUrl: portalUrl
    AzureAd__TenantId: tenantId
    AzureAd__ClientId: clientId
    AzureAd__ClientSecret: '@Microsoft.KeyVault(VaultName=${vaultName};SecretName=portal-client-secret)'
    ConnectionStrings__DefaultConnection: '@Microsoft.KeyVault(VaultName=${vaultName};SecretName=portal-db-connection)'
  }
  dependsOn: [keyVault, postgresEndpointDns]
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

resource siteEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: 'pe-${appName}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: endpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'sites'
        properties: {
          privateLinkServiceId: webApp.id
          groupIds: ['sites']
        }
      }
    ]
  }
}

// Records <appName> and <appName>.scm in privatelink.azurewebsites.net.
resource siteEndpointDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: siteEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'sites'
        properties: {
          privateDnsZoneId: siteZoneId
        }
      }
    ]
  }
  dependsOn: [siteZoneLink]
}

output portalUrl string = portalUrl
output webAppName string = webApp.name
output postgresFqdn string = postgresHost
output postgresServerName string = createPostgres ? serverName : ''
@description('User name of the portal in PostgreSQL. Managed identity: the identity name.')
output postgresUser string = useManagedIdentity ? identityName : (createPostgres ? postgresAdminLogin : '')
output keyVaultName string = vaultName
output keyVaultResourceGroup string = vaultResourceGroup
output identityName string = identityName
