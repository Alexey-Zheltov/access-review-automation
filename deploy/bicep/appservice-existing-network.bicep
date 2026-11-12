// Access Review portal in your existing virtual network: Azure App Service + Azure Database for PostgreSQL Flexible Server,
// reachable only through private endpoints.
// Sample template: adapt it to your environment (names, network, policies) before production use.
// Step-by-step guide: docs/DEPLOY-AZURE-APP-SERVICE.md, option D.
//
// The template creates no virtual network and no subnets. You give existing subnets in the region of the deployment:
//   - app subnet: VNet integration of the web app. All outbound traffic of the web app goes through your network.
//     Only for App Service: delegated to Microsoft.Web/serverFarms before the deployment. /28 or larger, /26 recommended.
//   - private endpoint subnet: the private endpoints of the web app, PostgreSQL, Key Vault and the container registry.
//     Optional: another subnet for single private endpoints (webApp/postgres/keyVault/containerRegistryPrivateEndpointSubnetResourceId).
// No public endpoints:
//   - web app: private endpoint, public network access disabled. Users, deployments and the Kudu console work only from
//     your network. Your DNS must resolve <appName>.azurewebsites.net and <appName>.scm.azurewebsites.net to the private endpoint.
//   - PostgreSQL Flexible Server: private endpoint, public network access disabled.
//   - Key Vault: a new vault (in this resource group or in keyVaultResourceGroup) with a private endpoint and no public access
//     (keyvault-private.bicep), or your existing vault that uses Azure RBAC (existingKeyVaultResourceId, keyvault-existing.bicep).
//     The web app reads the client secret and the connection string through Key Vault references with the user-assigned
//     managed identity id-<appName>, which gets Key Vault Secrets User.
// Code of the portal (deploymentType):
//   Code (default) - the workflow deploy-appservice.yml uploads the built app (zip) to the built-in .NET 10 runtime.
//   Container - the web app runs the image <registry>/access-review-portal from Azure Container Registry and pulls it through
//     the VNet with id-<appName>. A new registry (Premium, private endpoint, no public access, no admin user) or your existing
//     one (existingContainerRegistryResourceId). The workflow deploy-appservice-container.yml builds and pushes the image.
//     Microsoft Defender for Containers or Defender CSPM scans the images in the registry.
// Private DNS zones privatelink.azurewebsites.net, privatelink.postgres.database.azure.com, privatelink.vaultcore.azure.net and
// privatelink.azurecr.io: your existing zones (for example in a hub subscription), or new zones in this resource group, linked to
// the VNet of the app subnet. A zone is needed only for a private endpoint that the template creates.
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

@description('Zone redundancy of the App Service plan: the instances run in different availability zones. Azure chooses the zones; App Service has no setting for them. Needs a Premium plan (P0v3 or larger) and a region with availability zones. The portal supports more than one instance.')
param appServiceZoneRedundant bool = false

@description('Instances of the App Service plan. With zone redundancy at least 2: Azure spreads them over the availability zones of the region, so 3 instances in a region with three zones run one in each zone.')
@minValue(1)
@maxValue(10)
param appServiceInstanceCount int = 1

@description('Code: the workflow deploy-appservice.yml uploads the built app to the built-in .NET 10 runtime. Container: the web app runs the image from Azure Container Registry; the workflow deploy-appservice-container.yml builds and pushes it.')
@allowed(['Code', 'Container'])
param deploymentType string = 'Code'

@description('Microsoft Entra tenant id of the app registration.')
param tenantId string = subscription().tenantId

@description('Application (client) id of the app registration (setup-sp.ps1 prints it).')
param clientId string

@description('Client secret of the app registration.')
@secure()
param clientSecret string

@description('Resource id of an existing subnet for the VNet integration of the web app: /subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/<subnet>. Only for App Service and delegated to Microsoft.Web/serverFarms (the deployment does not change your subnets). /28 or larger, /26 recommended.')
param appSubnetResourceId string

@description('Resource id of an existing subnet for the private endpoints of the web app, PostgreSQL, Key Vault and the container registry. The four parameters below put single private endpoints into other subnets.')
param privateEndpointSubnetResourceId string

@description('Optional: subnet for the private endpoint of the web app. Empty = privateEndpointSubnetResourceId. The subnet of an existing private endpoint cannot be changed: delete the endpoint first.')
param webAppPrivateEndpointSubnetResourceId string = ''

@description('AzurePostgreSQL only, optional: subnet for the private endpoint of PostgreSQL. Empty = privateEndpointSubnetResourceId.')
param postgresPrivateEndpointSubnetResourceId string = ''

@description('Optional: subnet for the private endpoint of the Key Vault (new vault, or existing vault with existingKeyVaultPrivateEndpoint). Empty = privateEndpointSubnetResourceId.')
param keyVaultPrivateEndpointSubnetResourceId string = ''

@description('Container only, optional: subnet for the private endpoint of the registry (new registry, or existing registry with existingContainerRegistryPrivateEndpoint). Empty = privateEndpointSubnetResourceId.')
param containerRegistryPrivateEndpointSubnetResourceId string = ''

@description('Existing private DNS zone privatelink.azurewebsites.net, for example in a hub subscription. Empty = create one in this resource group and link it to the VNet of the app subnet.')
param webAppPrivateDnsZoneResourceId string = ''

@description('AzurePostgreSQL only: existing private DNS zone privatelink.postgres.database.azure.com. Empty = create one in this resource group and link it to the VNet of the app subnet.')
param postgresPrivateDnsZoneResourceId string = ''

@description('Existing private DNS zone privatelink.vaultcore.azure.net. Empty = create one in this resource group and link it to the VNet of the app subnet. Not needed for an existing vault without a new private endpoint.')
param keyVaultPrivateDnsZoneResourceId string = ''

@description('Container: existing private DNS zone privatelink.azurecr.io. Empty = create one in this resource group and link it to the VNet of the app subnet. Not needed for an existing registry without a new private endpoint.')
param containerRegistryPrivateDnsZoneResourceId string = ''

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

@description('Resource id of an existing Key Vault that uses Azure RBAC (permission model "Azure role-based access control"), in any subscription of the tenant. The template adds the secrets <appName>-client-secret and <appName>-db-connection and gives id-<appName> the role Key Vault Secrets User. It changes nothing else in the vault. Empty = the template creates a new vault (keyVaultName, keyVaultResourceGroup, enablePurgeProtection).')
param existingKeyVaultResourceId string = ''

@description('Existing Key Vault: scope of the role Key Vault Secrets User for id-<appName>. Secrets = only the two secrets of the portal (for a vault that other apps also use). Vault = all secrets of the vault.')
@allowed(['Secrets', 'Vault'])
param existingKeyVaultAccess string = 'Secrets'

@description('Existing Key Vault: create a private endpoint for the vault in the private endpoint subnet, with its record in the zone privatelink.vaultcore.azure.net. false = the vault already has a private endpoint that the App Service subnet reaches, for example in a hub network with central DNS zones.')
param existingKeyVaultPrivateEndpoint bool = false

@description('Container: resource id of an existing Azure Container Registry, in any subscription of the tenant. id-<appName> gets the pull role: AcrPull, or Container Registry Repository Reader for a registry with ABAC repository permissions. Empty = the template creates a new registry (Premium, private endpoint, no public access, no admin user).')
param existingContainerRegistryResourceId string = ''

@description('Container with an existing registry: create a private endpoint for the registry in the private endpoint subnet, with its records in the zone privatelink.azurecr.io. false = the registry already has a private endpoint that the App Service subnet reaches.')
param existingContainerRegistryPrivateEndpoint bool = false

@description('Container with a new registry: registry name (5-50 letters and digits, globally unique). Empty = cr<unique string>.')
@maxLength(50)
param containerRegistryName string = ''

@description('Container: repository and tag of the portal image in the registry. The workflow deploy-appservice-container.yml pushes the tags <commit> and latest, and switches the web app to <commit>.')
param containerImage string = 'access-review-portal:latest'

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

// Key Vault: a new vault (keyvault-private.bicep) or an existing one (keyvault-existing.bicep). In an existing vault the secret
// names carry the app name, because other apps may use the same vault.
var useExistingVault = !empty(existingKeyVaultResourceId)
var existingVaultIdParts = split(existingKeyVaultResourceId, '/')
var vaultSubscriptionId = useExistingVault ? existingVaultIdParts[2] : subscription().subscriptionId
var vaultResourceGroup = useExistingVault
  ? existingVaultIdParts[4]
  : (empty(keyVaultResourceGroup) ? resourceGroup().name : keyVaultResourceGroup)
var vaultName = useExistingVault
  ? last(existingVaultIdParts)
  : (empty(keyVaultName) ? 'kv-${uniqueString(resourceGroup().id, appName)}' : keyVaultName)
var clientSecretName = useExistingVault ? '${appName}-client-secret' : 'portal-client-secret'
var connectionSecretName = useExistingVault ? '${appName}-db-connection' : 'portal-db-connection'
var vaultEndpointNeeded = !useExistingVault || existingKeyVaultPrivateEndpoint

// Container: a new registry (containerregistry-private.bicep) or an existing one (containerregistry-existing.bicep).
var useContainer = deploymentType == 'Container'
var useExistingRegistry = useContainer && !empty(existingContainerRegistryResourceId)
var existingRegistryIdParts = split(existingContainerRegistryResourceId, '/')
var registrySubscriptionId = useExistingRegistry ? existingRegistryIdParts[2] : subscription().subscriptionId
var registryResourceGroup = useExistingRegistry ? existingRegistryIdParts[4] : resourceGroup().name
var registryName = useExistingRegistry
  ? last(existingRegistryIdParts)
  : (empty(containerRegistryName) ? 'cr${uniqueString(resourceGroup().id, appName)}' : containerRegistryName)
var registryLoginServer = '${toLower(registryName)}${environment().suffixes.acrLoginServer}'
var registryEndpointNeeded = useContainer && (!useExistingRegistry || existingContainerRegistryPrivateEndpoint)

// The subnets must be subnet resource ids. The check stops the deployment before anything is created.
var subnetIdPart = '/providers/microsoft.network/virtualnetworks/'
var badSubnetParameters = concat(
  contains(toLower(appSubnetResourceId), subnetIdPart) && contains(toLower(appSubnetResourceId), '/subnets/')
    ? []
    : ['appSubnetResourceId'],
  contains(toLower(privateEndpointSubnetResourceId), subnetIdPart) && contains(toLower(privateEndpointSubnetResourceId), '/subnets/')
    ? []
    : ['privateEndpointSubnetResourceId'],
  empty(webAppPrivateEndpointSubnetResourceId) || (contains(toLower(webAppPrivateEndpointSubnetResourceId), subnetIdPart) && contains(toLower(webAppPrivateEndpointSubnetResourceId), '/subnets/'))
    ? []
    : ['webAppPrivateEndpointSubnetResourceId'],
  empty(postgresPrivateEndpointSubnetResourceId) || (contains(toLower(postgresPrivateEndpointSubnetResourceId), subnetIdPart) && contains(toLower(postgresPrivateEndpointSubnetResourceId), '/subnets/'))
    ? []
    : ['postgresPrivateEndpointSubnetResourceId'],
  empty(keyVaultPrivateEndpointSubnetResourceId) || (contains(toLower(keyVaultPrivateEndpointSubnetResourceId), subnetIdPart) && contains(toLower(keyVaultPrivateEndpointSubnetResourceId), '/subnets/'))
    ? []
    : ['keyVaultPrivateEndpointSubnetResourceId'],
  empty(containerRegistryPrivateEndpointSubnetResourceId) || (contains(toLower(containerRegistryPrivateEndpointSubnetResourceId), subnetIdPart) && contains(toLower(containerRegistryPrivateEndpointSubnetResourceId), '/subnets/'))
    ? []
    : ['containerRegistryPrivateEndpointSubnetResourceId']
)
var appSubnetId = empty(badSubnetParameters)
  ? appSubnetResourceId
  : fail('Give the resource id of an existing subnet (/subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/<subnet>) in: ${join(badSubnetParameters, ', ')}')
// One subnet for all private endpoints, or another subnet for single endpoints.
var siteEndpointSubnetId = empty(webAppPrivateEndpointSubnetResourceId) ? privateEndpointSubnetResourceId : webAppPrivateEndpointSubnetResourceId
var postgresEndpointSubnetId = empty(postgresPrivateEndpointSubnetResourceId) ? privateEndpointSubnetResourceId : postgresPrivateEndpointSubnetResourceId
var vaultEndpointSubnetId = empty(keyVaultPrivateEndpointSubnetResourceId) ? privateEndpointSubnetResourceId : keyVaultPrivateEndpointSubnetResourceId
var registryEndpointSubnetId = empty(containerRegistryPrivateEndpointSubnetResourceId) ? privateEndpointSubnetResourceId : containerRegistryPrivateEndpointSubnetResourceId

// Combinations that Azure rejects. The check stops the deployment before anything is created.
var badOptions = concat(
  appServiceZoneRedundant && !startsWith(appServicePlanSku, 'P') ? ['appServiceZoneRedundant needs a Premium plan (P0v3 or larger)'] : [],
  createPostgres && postgresHighAvailability != 'Disabled' && postgresSkuTier == 'Burstable' ? ['postgresHighAvailability needs a General Purpose or Memory Optimized size, not Burstable'] : [],
  createPostgres && !empty(postgresStandbyAvailabilityZone) && postgresHighAvailability != 'ZoneRedundant' ? ['postgresStandbyAvailabilityZone needs postgresHighAvailability = ZoneRedundant'] : [],
  createPostgres && !empty(postgresStandbyAvailabilityZone) && postgresStandbyAvailabilityZone == postgresAvailabilityZone ? ['postgresStandbyAvailabilityZone must be another zone than postgresAvailabilityZone'] : [],
  useExistingVault && !(contains(toLower(existingKeyVaultResourceId), '/providers/microsoft.keyvault/vaults/') && length(existingVaultIdParts) == 9) ? ['existingKeyVaultResourceId must be the resource id of a Key Vault (/subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.KeyVault/vaults/<name>)'] : [],
  useExistingRegistry && !(contains(toLower(existingContainerRegistryResourceId), '/providers/microsoft.containerregistry/registries/') && length(existingRegistryIdParts) == 9) ? ['existingContainerRegistryResourceId must be the resource id of a container registry (/subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.ContainerRegistry/registries/<name>)'] : []
)
var planSku = empty(badOptions) ? appServicePlanSku : fail('Change these options: ${join(badOptions, '; ')}')
// lastIndexOf is not case-sensitive.
var vnetId = substring(appSubnetId, 0, lastIndexOf(appSubnetId, '/subnets/'))

var createSiteZone = empty(webAppPrivateDnsZoneResourceId)
var createPostgresZone = createPostgres && empty(postgresPrivateDnsZoneResourceId)
var createVaultZone = vaultEndpointNeeded && empty(keyVaultPrivateDnsZoneResourceId)
var createRegistryZone = registryEndpointNeeded && empty(containerRegistryPrivateDnsZoneResourceId)
var siteZoneId = createSiteZone ? siteZone.id : webAppPrivateDnsZoneResourceId
var postgresZoneId = createPostgresZone ? postgresZone.id : postgresPrivateDnsZoneResourceId
var vaultZoneId = createVaultZone ? vaultZone.id : keyVaultPrivateDnsZoneResourceId
var registryZoneId = createRegistryZone ? registryZone.id : containerRegistryPrivateDnsZoneResourceId

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

resource registryZone 'Microsoft.Network/privateDnsZones@2024-06-01' = if (createRegistryZone) {
  name: 'privatelink.azurecr.io'
  location: 'global'
  tags: tags
  dependsOn: [postgres]
}

resource registryZoneLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = if (createRegistryZone) {
  parent: registryZone
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
      id: postgresEndpointSubnetId
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
// New: Key Vault with a private endpoint only (public network access disabled), in the resource group keyVaultResourceGroup.
module keyVault 'keyvault-private.bicep' = if (!useExistingVault) {
  name: take('keyvault-${vaultName}', 64)
  scope: resourceGroup(vaultResourceGroup)
  params: {
    vaultName: vaultName
    location: location
    tags: tags
    enablePurgeProtection: enablePurgeProtection
    clientSecret: clientSecret
    connectionString: connectionString
    endpointSubnetId: vaultEndpointSubnetId
    privateDnsZoneId: vaultZoneId
    identityId: identity.id
    identityPrincipalId: identity.properties.principalId
  }
  dependsOn: [vaultZoneLink]
}

// Existing: the two secrets and the role Key Vault Secrets User (Azure RBAC), in the resource group of the vault.
module existingKeyVault 'keyvault-existing.bicep' = if (useExistingVault) {
  name: take('keyvault-${vaultName}', 64)
  scope: resourceGroup(vaultSubscriptionId, vaultResourceGroup)
  params: {
    vaultName: vaultName
    clientSecret: clientSecret
    connectionString: connectionString
    clientSecretName: clientSecretName
    connectionSecretName: connectionSecretName
    access: existingKeyVaultAccess
    identityId: identity.id
    identityPrincipalId: identity.properties.principalId
  }
}

// Existing vault, optional: a private endpoint in this resource group.
resource existingVaultEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (useExistingVault && existingKeyVaultPrivateEndpoint) {
  name: take('pe-${vaultName}-${appName}', 80)
  location: location
  tags: tags
  properties: {
    subnet: {
      id: vaultEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'vault'
        properties: {
          privateLinkServiceId: existingKeyVaultResourceId
          groupIds: ['vault']
        }
      }
    ]
  }
  dependsOn: [postgres]
}

resource existingVaultEndpointDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (useExistingVault && existingKeyVaultPrivateEndpoint) {
  parent: existingVaultEndpoint
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

// ------------------------------------------------------------------------------------------------ container registry
// New: Premium registry with a private endpoint and no public access, in this resource group.
module registry 'containerregistry-private.bicep' = if (useContainer && !useExistingRegistry) {
  name: take('registry-${registryName}', 64)
  params: {
    registryName: registryName
    location: location
    tags: tags
    endpointSubnetId: registryEndpointSubnetId
    privateDnsZoneId: registryZoneId
    identityId: identity.id
    identityPrincipalId: identity.properties.principalId
  }
  dependsOn: [registryZoneLink, postgres]
}

// Existing: the pull role for id-<appName>, in the resource group of the registry.
module existingRegistry 'containerregistry-existing.bicep' = if (useExistingRegistry) {
  name: take('registry-${registryName}', 64)
  scope: resourceGroup(registrySubscriptionId, registryResourceGroup)
  params: {
    registryName: registryName
    identityId: identity.id
    identityPrincipalId: identity.properties.principalId
  }
  dependsOn: [postgres]
}

// Existing registry, optional: a private endpoint in this resource group (records for the registry and its data endpoints).
resource existingRegistryEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (useExistingRegistry && existingContainerRegistryPrivateEndpoint) {
  name: take('pe-${registryName}-${appName}', 80)
  location: location
  tags: tags
  properties: {
    subnet: {
      id: registryEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'registry'
        properties: {
          privateLinkServiceId: existingContainerRegistryResourceId
          groupIds: ['registry']
        }
      }
    ]
  }
  dependsOn: [postgres]
}

resource existingRegistryEndpointDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (useExistingRegistry && existingContainerRegistryPrivateEndpoint) {
  parent: existingRegistryEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'registry'
        properties: {
          privateDnsZoneId: registryZoneId
        }
      }
    ]
  }
  dependsOn: [registryZoneLink]
}

// ------------------------------------------------------------------------------------------------ web app
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-${appName}'
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: planSku
    // Zone redundancy needs at least two instances. Azure spreads them over the zones of the region.
    capacity: appServiceZoneRedundant ? max(2, appServiceInstanceCount) : appServiceInstanceCount
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
  kind: useContainer ? 'app,linux,container' : 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  // The identity can read the secrets and pull the image before the app settings are applied.
  dependsOn: [keyVault, existingKeyVault, existingVaultEndpointDns, registry, existingRegistry, existingRegistryEndpointDns]
  properties: {
    serverFarmId: plan.id
    keyVaultReferenceIdentity: identity.id
    httpsOnly: true
    // Only through the private endpoint: users, deployments (SCM) and the Kudu console.
    publicNetworkAccess: 'Disabled'
    // Blazor Server keeps the user session on one instance.
    clientAffinityEnabled: true
    virtualNetworkSubnetId: appSubnetId
    // Container: the image comes through the VNet, from the private endpoint of the registry.
    vnetImagePullEnabled: useContainer
    siteConfig: {
      linuxFxVersion: useContainer ? 'DOCKER|${registryLoginServer}/${containerImage}' : 'DOTNETCORE|10.0'
      appCommandLine: useContainer ? '' : 'dotnet AccessReviewPortal.dll'
      acrUseManagedIdentityCreds: useContainer
      acrUserManagedIdentityID: useContainer ? identity.properties.clientId : null
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
  properties: union(useContainer
    ? {
        // The image listens on 8080 (ASPNETCORE_HTTP_PORTS in the Dockerfile).
        WEBSITES_PORT: '8080'
        WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'false'
      }
    : {
        SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
      }, {
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
    // The schema is migrated when the app starts. EF Core locks the migration history, so several instances can start together.
    Database__ApplyMigrationsOnStartup: 'true'
    Database__Authentication: useManagedIdentity ? 'ManagedIdentity' : 'Password'
    Database__ManagedIdentityClientId: useManagedIdentity ? identity.properties.clientId : ''
    Database__RootCertificate: createPostgres ? '' : existingServerCaCertificate
    Authentication__Mode: 'EntraId'
    Portal__PublicBaseUrl: portalUrl
    AzureAd__TenantId: tenantId
    AzureAd__ClientId: clientId
    AzureAd__ClientSecret: '@Microsoft.KeyVault(VaultName=${vaultName};SecretName=${clientSecretName})'
    ConnectionStrings__DefaultConnection: '@Microsoft.KeyVault(VaultName=${vaultName};SecretName=${connectionSecretName})'
  })
  dependsOn: [keyVault, existingKeyVault, existingVaultEndpointDns, postgresEndpointDns]
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
      id: siteEndpointSubnetId
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
@description('Container: login server of the registry, for the workflow variable ACR_LOGIN_SERVER.')
output containerRegistryLoginServer string = useContainer ? registryLoginServer : ''
@description('Container: resource id of the registry. The GitHub identity needs AcrPush on it (Container Registry Repository Writer with ABAC repository permissions).')
output containerRegistryId string = useContainer
  ? resourceId(registrySubscriptionId, registryResourceGroup, 'Microsoft.ContainerRegistry/registries', registryName)
  : ''
