// Azure Database for PostgreSQL Flexible Server for the Access Review portal (sample - adapt it to your environment).
// Use it when the database moves from the container on the VM to PaaS. The schema is created by the migration bundle
// (efbundle) of the pipeline, not by this template.
//
//   az deployment group create -g <rg> -f deploy/bicep/postgres-flexible.bicep \
//     -p serverName=<name> administratorLogin=<user> administratorPassword=<secret>
//
// Network: either public access with firewall rules (default), private access in a delegated subnet
// (set delegatedSubnetResourceId and privateDnsZoneArmResourceId), or no public access for a server that you reach
// through a private endpoint (publicNetworkAccess = false; create the private endpoint separately).
// Sign-in: password (administratorLogin / administratorPassword) and/or Microsoft Entra ID. With passwordAuth = false the server
// accepts only Microsoft Entra tokens; entraAdministrators sets who administers it (for example a managed identity).

@description('Server name (globally unique, lower case).')
param serverName string

param location string = resourceGroup().location

@description('PostgreSQL major version.')
@allowed(['16', '17', '18'])
param postgresVersion string = '18'

@description('Administrator login. Only with passwordAuth = true.')
param administratorLogin string = ''

@description('Administrator password. Only with passwordAuth = true.')
@secure()
param administratorPassword string = ''

@description('Sign-in with administratorLogin and administratorPassword. false = Microsoft Entra authentication only.')
param passwordAuth bool = true

@description('Microsoft Entra administrators of the server: objects { objectId, principalName, principalType (User, Group or ServicePrincipal) }.')
param entraAdministrators array = []

@description('Compute size. Standard_D2ds_v5 (2 vCores, 8 GB) is enough for tens of thousands of assignments per scan; use D4ds_v5 for very large tenants.')
param skuName string = 'Standard_D2ds_v5'

@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param skuTier string = 'GeneralPurpose'

@description('Storage in GB. Each snapshot row needs about 2-3 KB including indexes.')
param storageSizeGB int = 64

param backupRetentionDays int = 14

@allowed(['Disabled', 'ZoneRedundant', 'SameZone'])
param highAvailability string = 'Disabled'

@description('Availability zone of the server (1, 2 or 3). Empty = Azure chooses. Used only when Azure creates the server: a later deployment with another zone does not move it.')
@allowed(['', '1', '2', '3'])
param availabilityZone string = ''

@description('With highAvailability = ZoneRedundant: availability zone of the standby server, another zone than availabilityZone. Empty = Azure chooses.')
@allowed(['', '1', '2', '3'])
param standbyAvailabilityZone string = ''

@description('Database used by the portal.')
param databaseName string = 'accessreview'

@description('Public access: client IP ranges allowed to connect (for example the pipeline agent and the portal host).')
param allowedIpRanges array = []

@description('Public access: allow connections from Azure services (App Service, Container Apps without VNet integration).')
param allowAzureServices bool = true

@description('Without a delegated subnet: false = no public endpoint and no firewall rules, for a server that you reach through a private endpoint.')
param publicNetworkAccess bool = true

@description('Private access: resource id of a subnet delegated to Microsoft.DBforPostgreSQL/flexibleServers. Empty = public access.')
param delegatedSubnetResourceId string = ''

@description('Private access: resource id of the private DNS zone (privatelink.postgres.database.azure.com or <name>.private.postgres.database.azure.com).')
param privateDnsZoneArmResourceId string = ''

@description('Also allow Microsoft Entra ID authentication (for administrators and managed identities).')
param enableEntraAuth bool = true

param tags object = {
  workload: 'access-review'
}

var privateAccess = !empty(delegatedSubnetResourceId)
var firewallRules = !privateAccess && publicNetworkAccess
var entraAuth = enableEntraAuth || !passwordAuth

// Server parameters for scan-heavy workloads: bulk inserts in chunks, large filtered queries, bulk deletes of old snapshots.
// shared_buffers and effective_cache_size are set by the service from the compute size.
var serverParameters = {
  work_mem: '16384' // kB
  maintenance_work_mem: '262144' // kB
  max_wal_size: '4096' // MB
  random_page_cost: '1.1'
  default_statistics_target: '200'
  autovacuum_vacuum_scale_factor: '0.05'
  autovacuum_analyze_scale_factor: '0.02'
  log_min_duration_statement: '2000' // ms
  log_lock_waits: 'on'
}

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' = {
  name: serverName
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: union({
    version: postgresVersion
    administratorLogin: passwordAuth ? administratorLogin : null
    administratorLoginPassword: passwordAuth ? administratorPassword : null
    storage: {
      storageSizeGB: storageSizeGB
      autoGrow: 'Enabled'
    }
    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: union({
      mode: highAvailability
    }, highAvailability == 'ZoneRedundant' && !empty(standbyAvailabilityZone) ? { standbyAvailabilityZone: standbyAvailabilityZone } : {})
    network: privateAccess
      ? {
          delegatedSubnetResourceId: delegatedSubnetResourceId
          privateDnsZoneArmResourceId: privateDnsZoneArmResourceId
          publicNetworkAccess: 'Disabled'
        }
      : {
          publicNetworkAccess: publicNetworkAccess ? 'Enabled' : 'Disabled'
        }
    authConfig: {
      activeDirectoryAuth: entraAuth ? 'Enabled' : 'Disabled'
      passwordAuth: passwordAuth ? 'Enabled' : 'Disabled'
      tenantId: entraAuth ? subscription().tenantId : null
    }
  }, empty(availabilityZone) ? {} : { availabilityZone: availabilityZone })
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2025-08-01' = {
  parent: server
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// The service changes one parameter at a time.
@batchSize(1)
resource parameters 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2025-08-01' = [
  for p in items(serverParameters): {
    parent: server
    name: p.key
    properties: {
      value: p.value
      source: 'user-override'
    }
    dependsOn: [database]
  }
]

// Microsoft Entra administrators (for example the managed identity of the web app), after the server parameters.
@batchSize(1)
resource administrators 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2025-08-01' = [
  for a in entraAdministrators: {
    parent: server
    name: a.objectId
    properties: {
      principalName: a.principalName
      principalType: a.principalType
      tenantId: subscription().tenantId
    }
    dependsOn: [parameters]
  }
]

resource azureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2025-08-01' = if (firewallRules && allowAzureServices) {
  parent: server
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource clientRules 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2025-08-01' = [
  for (range, i) in (firewallRules ? allowedIpRanges : []): {
    parent: server
    name: 'client-${i}'
    properties: {
      startIpAddress: range.start
      endIpAddress: range.end
    }
  }
]

output id string = server.id
output fqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = databaseName
@description('Connection string for ConnectionStrings__DefaultConnection (add the password).')
output connectionStringTemplate string = 'Host=${server.properties.fullyQualifiedDomainName};Port=5432;Database=${databaseName};Username=${administratorLogin};Password=<password>;Ssl Mode=Require'
