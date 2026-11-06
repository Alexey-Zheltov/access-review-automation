// Azure Database for PostgreSQL Flexible Server for the Access Review portal (sample - adapt it to your environment).
// Use it when the database moves from the container on the VM to PaaS. The schema is created by the migration bundle
// (efbundle) of the pipeline, not by this template.
//
//   az deployment group create -g <rg> -f deploy/bicep/postgres-flexible.bicep \
//     -p serverName=<name> administratorLogin=<user> administratorPassword=<secret>
//
// Network: either public access with firewall rules (default) or private access in a delegated subnet
// (set delegatedSubnetResourceId and privateDnsZoneArmResourceId).

@description('Server name (globally unique, lower case).')
param serverName string

param location string = resourceGroup().location

@description('PostgreSQL major version.')
@allowed(['16', '17', '18'])
param postgresVersion string = '18'

param administratorLogin string

@secure()
param administratorPassword string

@description('Compute size. Standard_D2ds_v5 (2 vCores, 8 GB) is enough for tens of thousands of assignments per scan; use D4ds_v5 for very large tenants.')
param skuName string = 'Standard_D2ds_v5'

@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param skuTier string = 'GeneralPurpose'

@description('Storage in GB. Each snapshot row needs about 2-3 KB including indexes.')
param storageSizeGB int = 64

param backupRetentionDays int = 14

@allowed(['Disabled', 'ZoneRedundant', 'SameZone'])
param highAvailability string = 'Disabled'

@description('Database used by the portal.')
param databaseName string = 'accessreview'

@description('Public access: client IP ranges allowed to connect (for example the pipeline agent and the portal host).')
param allowedIpRanges array = []

@description('Public access: allow connections from Azure services (App Service, Container Apps without VNet integration).')
param allowAzureServices bool = true

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
  properties: {
    version: postgresVersion
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    storage: {
      storageSizeGB: storageSizeGB
      autoGrow: 'Enabled'
    }
    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: highAvailability
    }
    network: privateAccess
      ? {
          delegatedSubnetResourceId: delegatedSubnetResourceId
          privateDnsZoneArmResourceId: privateDnsZoneArmResourceId
          publicNetworkAccess: 'Disabled'
        }
      : {
          publicNetworkAccess: 'Enabled'
        }
    authConfig: {
      activeDirectoryAuth: enableEntraAuth ? 'Enabled' : 'Disabled'
      passwordAuth: 'Enabled'
      tenantId: enableEntraAuth ? subscription().tenantId : null
    }
  }
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

resource azureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2025-08-01' = if (!privateAccess && allowAzureServices) {
  parent: server
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource clientRules 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2025-08-01' = [
  for (range, i) in (privateAccess ? [] : allowedIpRanges): {
    parent: server
    name: 'client-${i}'
    properties: {
      startIpAddress: range.start
      endIpAddress: range.end
    }
  }
]

output fqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = databaseName
@description('Connection string for ConnectionStrings__DefaultConnection (add the password).')
output connectionStringTemplate string = 'Host=${server.properties.fullyQualifiedDomainName};Port=5432;Database=${databaseName};Username=${administratorLogin};Password=<password>;Ssl Mode=Require'
