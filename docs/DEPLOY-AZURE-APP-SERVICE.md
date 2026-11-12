# Deploy to Azure App Service with Azure Database for PostgreSQL

This guide installs the portal from this repository into your Azure subscription. You run every step yourself from a PowerShell 7 window. A first installation takes about 30 minutes.

Contents

1. [What you deploy](#1-what-you-deploy)
2. [Before you start](#2-before-you-start)
3. [Step 1. Clone the repository](#step-1-clone-the-repository)
4. [Step 2. Set the values and sign in](#step-2-set-the-values-and-sign-in)
5. [Step 3. Create the app registration](#step-3-create-the-app-registration)
6. [Step 4. Create the Azure resources](#step-4-create-the-azure-resources)
7. [Step 5. Build and deploy the portal](#step-5-build-and-deploy-the-portal)
8. [Step 6. Check the installation](#step-6-check-the-installation)
9. [Step 7. Give access to the reviewers](#step-7-give-access-to-the-reviewers)
10. [Deploy with GitHub Actions (optional)](#deploy-with-github-actions-optional)
11. [Update, client secret, removal](#update-client-secret-removal)
12. [Troubleshooting](#troubleshooting)

---

## 1. What you deploy

```
 Reviewer's browser ──HTTPS──> Azure App Service (Linux, .NET 10, one instance; two or three with zone redundancy)
                                  │  sign-in: Microsoft Entra ID
                                  ├──> Microsoft Graph, Azure Resource Manager, Azure Resource Graph
                                  │      scanner: read-only application permissions of the app registration
                                  │      revoke:  delegated token of the signed-in reviewer
                                  ├──> PostgreSQL: Azure Database for PostgreSQL Flexible Server, or your own server (snapshots, decisions, sign-in keys)
                                  └──> Azure Key Vault (private network option only: client secret, connection string)
```

The Bicep template `deploy/bicep/appservice.bicep` has two options:

| | Quick start (`privateNetwork=false`, default) | Private network (`privateNetwork=true`) |
|---|---|---|
| Use it for | Tests, a first look, environments without network rules | Production |
| PostgreSQL | Public endpoint, firewall rule "Allow Azure services", TLS required | In a delegated subnet, no public endpoint |
| Secrets | App settings of the web app (encrypted at rest; people with Contributor on the web app can read them) | Key Vault with a private endpoint; the web app reads them with a managed identity |
| Network | None | VNet integration of the web app. A new VNet, or your existing subnets and private DNS zones |
| Rights of the deploying person | Contributor on the resource group | Contributor and Role Based Access Control Administrator on the resource group, plus network rights if the network is elsewhere ([PERMISSIONS.md](PERMISSIONS.md)) |
| Approximate cost per month with the default sizes (West Europe, pay-as-you-go list price) | about USD 125: App Service P0v3 (65), PostgreSQL B2s (58), 32 GB storage (4) | about USD 135: the same, plus a private endpoint (about 7) and two private DNS zones (1) |

The template has three database choices:

| Database | Parameters | What the template does | The portal signs in with |
|---|---|---|---|
| Azure PostgreSQL with managed identity (default) | none | Creates Azure Database for PostgreSQL Flexible Server. The server accepts only Microsoft Entra sign-in. The managed identity `id-<app-name>` of the web app is its administrator. | A Microsoft Entra token of the managed identity. There is no database password. |
| Azure PostgreSQL with password | `databaseAuthentication=Password`, `postgresAdminPassword` | Creates the server with the administrator login `accessreviewadmin`. | Login and password |
| Your own PostgreSQL server, for example on a VM | `databaseServer=ExistingServer`, `existingServerConnectionString` | Creates no database server ([Option C](#option-c-your-own-postgresql-server)). | Login and password from your connection string |

Sizes for larger tenants: [SIZING.md](SIZING.md).

In both options the portal address is `https://<app-name>.azurewebsites.net` and the sign-in page is public. Only users with a portal role can open the portal.

When the portal must be reachable only from your network, use the second template `deploy/bicep/appservice-existing-network.bicep` ([option D](#option-d-your-existing-network-private-endpoints-only)). It uses your existing virtual network. The web app, PostgreSQL and Key Vault get private endpoints and have no public access.

## 2. Before you start

### Rights

You need rights in Microsoft Entra ID and in Azure. The details, the checks and the common errors are in [PERMISSIONS.md](PERMISSIONS.md). In short:

- **Microsoft Entra ID:** Privileged Role Administrator (to consent to the read-only Microsoft Graph application permissions of the scanner), and the right to create app registrations.
- **Azure, management group** that the portal reads (default: Tenant Root Group): Owner, User Access Administrator or Role Based Access Control Administrator, to give the scanner `Reader` and `Monitoring Reader`.
- **Azure, subscription or resource group** of the portal: Contributor. For the private network option also Role Based Access Control Administrator.

**Activate your PIM roles before you start**, then sign in. A role that you activate later is not in your current token.

### Tools

| Tool | Check | Install |
|---|---|---|
| Git | `git --version` | <https://git-scm.com/downloads> |
| .NET 10 SDK | `dotnet --list-sdks` shows `10.0.x` | <https://dotnet.microsoft.com/download/dotnet/10.0> |
| Azure CLI 2.70 or later (includes Bicep) | `az version` | <https://learn.microsoft.com/cli/azure/install-azure-cli> |
| PowerShell 7 | `$PSVersionTable.PSVersion` | <https://learn.microsoft.com/powershell/scripting/install/installing-powershell> |
| Az PowerShell modules (for `setup-sp.ps1`) | `Get-Module -ListAvailable Az.Accounts, Az.Resources` | `Install-Module Az.Accounts, Az.Resources -Scope CurrentUser` |

The commands work on Windows, Linux and macOS in PowerShell 7.

### Choose a region

PostgreSQL Flexible Server sizes are not available in every region for every subscription type. Check the region before you start:

```powershell
az postgres flexible-server list-skus --location <region> --query "[0].supportedServerEditions[].supportedServerSkus[].name" -o tsv
```

The list must contain `Standard_B2s` (or the size you choose). If the deployment later fails with `SkuNotAvailable`, choose another region or size.

## Step 1. Clone the repository

```powershell
git clone https://github.com/Alexey-Zheltov/access-review-automation.git
Set-Location access-review-automation
```

All later commands run in this folder.

## Step 2. Set the values and sign in

Change the values in angle brackets. Keep this PowerShell window open until the end: the later steps use these variables.

```powershell
$tenantId       = '<tenant-id>'
$subscriptionId = '<subscription-id>'
$location       = '<region>'                     # for example westeurope
$resourceGroup  = 'rg-access-review'
$suffix         = '<suffix>'                     # 3-8 lower-case letters or digits; makes the names unique
$appName        = "accessreview-$suffix"         # portal: https://accessreview-<suffix>.azurewebsites.net
$postgresName   = "psql-accessreview-$suffix"
$keyVaultName   = "kv-accessreview-$suffix"      # private network option only (maximum 24 characters)
$adminUpn       = '<admin@contoso.com>'          # gets the portal role AccessReview.Admin
$envFile        = Join-Path $HOME 'accessreview-appservice.env'   # temporary file for the client secret, outside the repository

az login --tenant $tenantId
az account set --subscription $subscriptionId
Connect-AzAccount -Tenant $tenantId -Subscription $subscriptionId
```

Check your rights now, with the commands in [PERMISSIONS.md, section 2](PERMISSIONS.md#2-check-your-access-before-you-start). It takes five minutes and avoids most failed deployments.

## Step 3. Create the app registration

```powershell
.\setup-sp.ps1 -TenantId $tenantId -DisplayName 'AccessReview-Portal' `
    -PortalUrl "https://$appName.azurewebsites.net" `
    -AdminUpn $adminUpn -GrantDelegatedConsent -EnvFile $envFile
```

The script takes about one minute. It:

1. creates the app registration `AccessReview-Portal` and its service principal;
2. adds the redirect URIs of the portal address and turns on "Allow public client flows" (sign-in with a code from another device);
3. adds the portal roles `AccessReview.Admin` and `AccessReview.Reviewer`, and gives `AccessReview.Admin` to `$adminUpn`;
4. grants the read-only Microsoft Graph application permissions of the scanner and the delegated permissions (admin consent);
5. gives the scanner `Reader` and `Monitoring Reader` on the Tenant Root Group. To read less, add `-ManagementGroupId <id>` or `-SubscriptionId <id1>,<id2>`;
6. creates a client secret (valid for 6 months) and writes the tenant id, client id and secret to `$envFile`. The secret is not printed.

You can run the script again at any time. It only adds what is missing.

## Step 4. Create the Azure resources

Read the values from the script output:

```powershell
$values = Get-Content $envFile -Raw | ConvertFrom-StringData
$clientId = $values.AzureAd__ClientId
$clientSecret = $values.AzureAd__ClientSecret

az group create --name $resourceGroup --location $location --output none
```

The commands of options A and B create the default database: Azure PostgreSQL with the managed identity of the web app, without a password. To use a password instead, create one and add `databaseAuthentication=Password postgresAdminPassword=$postgresPassword` to the parameters:

```powershell
$postgresPassword = 'Pg1-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
```

The template stores the password in the connection string of the portal. You do not need to keep it. To connect to the database yourself later, set a new password with `az postgres flexible-server update --resource-group $resourceGroup --name $postgresName --admin-password <new password>`, then update the connection string (run the template again with the new password).

### Option A: quick start

```powershell
az deployment group create --resource-group $resourceGroup --name accessreview `
    --template-file deploy/bicep/appservice.bicep `
    --parameters appName=$appName postgresServerName=$postgresName `
                 clientId=$clientId clientSecret=$clientSecret
```

### Option B: private network

With a new virtual network (`10.20.0.0/24`, change it with `vnetAddressPrefix=<range>`) and new private DNS zones in the resource group:

```powershell
az deployment group create --resource-group $resourceGroup --name accessreview `
    --template-file deploy/bicep/appservice.bicep `
    --parameters appName=$appName postgresServerName=$postgresName keyVaultName=$keyVaultName privateNetwork=true `
                 clientId=$clientId clientSecret=$clientSecret
```

With your existing network, give the resource ids of the three subnets and, if the private DNS zones are central (for example in a hub subscription), of the two zones. Check the rights in [PERMISSIONS.md, section 4](PERMISSIONS.md#4-private-network-rights-outside-the-resource-group) first.

```powershell
$appSubnetId      = '<resource id of the subnet delegated to Microsoft.Web/serverFarms>'
$postgresSubnetId = '<resource id of the subnet delegated to Microsoft.DBforPostgreSQL/flexibleServers>'
$endpointSubnetId = '<resource id of the subnet for private endpoints>'
$postgresZoneId   = '<resource id of the zone <name>.private.postgres.database.azure.com>'
$vaultZoneId      = '<resource id of the zone privatelink.vaultcore.azure.net>'

az deployment group create --resource-group $resourceGroup --name accessreview `
    --template-file deploy/bicep/appservice.bicep `
    --parameters appName=$appName postgresServerName=$postgresName keyVaultName=$keyVaultName privateNetwork=true `
                 appSubnetResourceId=$appSubnetId postgresSubnetResourceId=$postgresSubnetId `
                 privateEndpointSubnetResourceId=$endpointSubnetId `
                 postgresPrivateDnsZoneResourceId=$postgresZoneId keyVaultPrivateDnsZoneResourceId=$vaultZoneId `
                 clientId=$clientId clientSecret=$clientSecret
```

Leave out the two zone parameters to create new zones in the resource group and link them to your virtual network.

The Key Vault has purge protection. After you delete it, its name stays reserved for 90 days. For a test, add `enablePurgeProtection=false`.

### Option C: your own PostgreSQL server

Use this option for PostgreSQL on a VM, on-premises or in Azure (version 16 or later). The template creates no database server. The database administrator prepares the server:

1. A login that owns the database. The portal creates its tables at the first start.

   ```sql
   CREATE ROLE accessreview LOGIN PASSWORD '<password>';
   CREATE DATABASE accessreview OWNER accessreview;
   ```

2. TLS on the server (`ssl = on`), and a line in `pg_hba.conf` for the address range of the app subnet:
   `hostssl accessreview accessreview <address range of the app subnet> scram-sha-256`
3. A network path from the app subnet to the server on port 5432: the same virtual network, peering, VPN or ExpressRoute. Firewalls on the way must allow the address range of the app subnet. Intrusion detection on a firewall or VPN device can block connections to port 5432: add an exception for the app subnet.

The web app reaches a server in your network only through VNet integration. So use the private network option with your existing subnets (see option B). The connection string with the password goes to Key Vault.

```powershell
$dbConnection = 'Host=pg01.contoso.com;Port=5432;Database=accessreview;Username=accessreview;Password=<password>;Ssl Mode=VerifyFull'
# Only when the server certificate comes from your own CA: the CA certificate (.cer file) as base64
$dbCa = [Convert]::ToBase64String([Security.Cryptography.X509Certificates.X509Certificate2]::new('C:\certs\contoso-root-ca.cer').RawData)

az deployment group create --resource-group $resourceGroup --name accessreview `
    --template-file deploy/bicep/appservice.bicep `
    --parameters appName=$appName keyVaultName=$keyVaultName privateNetwork=true `
                 appSubnetResourceId=$appSubnetId privateEndpointSubnetResourceId=$endpointSubnetId `
                 keyVaultPrivateDnsZoneResourceId=$vaultZoneId `
                 databaseServer=ExistingServer existingServerConnectionString=$dbConnection existingServerCaCertificate=$dbCa `
                 clientId=$clientId clientSecret=$clientSecret
```

- `Ssl Mode=VerifyFull` checks the certificate of the server and its name: `Host` must be a name or IP address in the certificate. `Ssl Mode=Require` encrypts the connection but does not check the certificate.
- The CA certificate becomes the app setting `Database__RootCertificate`. You can change it later in **Environment variables** of the web app.

### Option D: your existing network, private endpoints only

Use this option when the portal must be reachable only from your network. The template `deploy/bicep/appservice-existing-network.bicep` uses your existing virtual network. It creates no virtual network and no subnets.

| | What the template creates |
|---|---|
| Web app | Private endpoint, public network access disabled. VNet integration: all outbound traffic goes through your network. |
| Code of the portal | **Code** (`deploymentType=Code`, default): the workflow `deploy-appservice.yml` uploads the built app to the built-in .NET 10 runtime. **Container** (`deploymentType=Container`): the web app runs the image `<registry>/access-review-portal` from Azure Container Registry and pulls it through your network with its managed identity. A new registry (Premium, private endpoint, no public access, no admin user; trusted Microsoft services keep access, so Microsoft Defender for Cloud can scan the images), or your existing registry (`existingContainerRegistryResourceId`): the identity gets AcrPull, or Container Registry Repository Reader when the registry uses ABAC repository permissions. The workflow `deploy-appservice-container.yml` builds and pushes the image ([Container image](#container-image-option-d)). |
| PostgreSQL | Azure Database for PostgreSQL Flexible Server with a private endpoint, public network access disabled. Or your own server (option C). |
| Key Vault | A new Key Vault with a private endpoint, public network access disabled. It keeps the client secret and the connection string. In the resource group of the deployment, or in another existing resource group of the same subscription (`keyVaultResourceGroup`; in the portal form: section **Key Vault**). Or your existing vault that uses Azure RBAC (`existingKeyVaultResourceId`, any subscription of the tenant): the template adds the secrets `<app-name>-client-secret` and `<app-name>-db-connection` and gives the managed identity the role Key Vault Secrets User on these two secrets only (`existingKeyVaultAccess=Secrets`, default) or on the whole vault (`Vault`). It changes nothing else in the vault. |
| Subnets (existing) | **App Service subnet**: only for App Service and delegated to `Microsoft.Web/serverFarms` before the deployment (the template does not change your subnets), `/28` or larger, `/26` recommended. **Private endpoint subnet**: the private endpoints of the web app, PostgreSQL, Key Vault and, for a container, the registry. Both in the region of the deployment. |
| Private DNS zones | `privatelink.azurewebsites.net`, `privatelink.postgres.database.azure.com`, `privatelink.vaultcore.azure.net` and, for a container, `privatelink.azurecr.io`: your existing zones, or new zones in the resource group, linked to the virtual network of the App Service subnet. A zone is needed only for a private endpoint that the template creates: an existing vault or registry gets one only on request (`existingKeyVaultPrivateEndpoint=true`, `existingContainerRegistryPrivateEndpoint=true`). |
| Zone redundancy (optional) | App Service plan with two or three instances (`appServiceZoneRedundant=true`, `appServiceInstanceCount=2` or `3`, Premium plan). Azure spreads the instances over the availability zones of the region: with three instances in a region with three zones, one instance runs in each zone. Azure chooses the zones; App Service has no setting to pin an instance to a zone. PostgreSQL high availability with a standby server (`postgresHighAvailability=ZoneRedundant` or `SameZone`, not with Burstable sizes; the standby doubles the compute cost). The region must have availability zones. |
| PostgreSQL zones (optional) | The zone of the server (`postgresAvailabilityZone`: 1, 2 or 3) and, with `ZoneRedundant`, the zone of the standby (`postgresStandbyAvailabilityZone`, another zone). Empty = Azure chooses. Azure uses the zones only when it creates the server. A later deployment with other zones does not move the server or the standby. |

Checks before the deployment:

- The portal form lists only the virtual networks of the chosen region, subnets delegated to `Microsoft.Web/serverFarms` for App Service, and subnets without delegation for the private endpoints.
- In the section **Database** it reads the PostgreSQL offer of the region and your vCore quota, and shows the free quota. A red message tells you when the size is not offered, the quota of the size family or the total quota is too small (a standby counts double), the region is restricted for your subscription, high availability does not fit the size or the region, or the standby zone is the zone of the server. Choose other values before you create the deployment.
- In the sections **Key Vault** and **Container registry** it lists the vaults and registries of the subscriptions that you can read. A red message tells you when a vault uses access policies instead of Azure RBAC. A note tells you when a registry uses ABAC repository permissions, when trusted Microsoft services have no access to a registry without public access (then Defender for Cloud cannot scan the images), and when a new private endpoint would change the record of a vault or registry that already has one.
- The template itself stops before it creates anything when a subnet value is not a subnet resource id, zone redundancy is chosen with a Basic plan, high availability with a Burstable size, a standby zone without zone-redundant high availability or in the zone of the server, or the resource id of an existing vault or registry has the wrong form. Azure checks the App Service quota at **Review + create**. Zone redundancy needs its own quota for the size in the region, for example "P0v3 AZ App Service instances". If it is too small, the deployment stops with `InternalSubscriptionIsOverQuotaForSku`.
- Some limits are known only when Azure creates the server (`SkuNotAvailable`). The template therefore creates PostgreSQL first: with such a size the deployment stops after about two minutes and creates nothing else but the managed identity.

The PostgreSQL server gets the name from `postgresServerName` (portal form: **Server name**; empty = `psql-<app-name>`). The deployment step that creates it is named `postgres-<server name>`, the step of the Key Vault `keyvault-<vault name>`.

Agree with your network team before you deploy:

- Users open `https://<app-name>.azurewebsites.net` from your network. Your DNS must resolve `<app-name>.azurewebsites.net` and `<app-name>.scm.azurewebsites.net` to the private endpoint, for example with a conditional forwarder for `privatelink.azurewebsites.net` to an Azure DNS Private Resolver. Sign-in still goes to `login.microsoftonline.com`.
- If the virtual network uses custom DNS servers, use the existing zones that these servers resolve. New zones are linked only to the virtual network of the App Service subnet.
- All outbound traffic of the web app goes through your network. Allow `login.microsoftonline.com`, `graph.microsoft.com` and `management.azure.com` from the App Service subnet.
- Deploy the code (Step 5) from a computer or a runner in your network: the deployment address `<app-name>.scm.azurewebsites.net` has no public access either.

**Azure portal.** Open this link. The form lists the virtual networks in the region that you choose, their suitable subnets, and your private DNS zones:

```text
https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2FAlexey-Zheltov%2Faccess-review-automation%2Fmain%2Fdeploy%2Fbicep%2Fappservice-existing-network.json/createUIDefinitionUri/https%3A%2F%2Fraw.githubusercontent.com%2FAlexey-Zheltov%2Faccess-review-automation%2Fmain%2Fdeploy%2Fbicep%2Fappservice-existing-network.createUiDefinition.json
```

The form is `deploy/bicep/appservice-existing-network.createUiDefinition.json`. The portal asks "Do you trust the authors code?": select **Yes, I trust the authors**. The form then reads your virtual networks, subnets, private DNS zones, vaults and registries with your rights (read only) and lists them. The form is one page with the sections **Network**, **Web app**, **Database**, **Key Vault**, **Container registry** (only for a container) and **Private DNS zones**.

**Commands.** Find the resource ids of the subnets:

```powershell
az network vnet subnet list --resource-group <network resource group> --vnet-name <virtual network> `
    --query "[].{name:name, prefix:addressPrefix, delegation:delegations[0].serviceName, id:id}" -o table
```

```powershell
$appSubnetId      = '<resource id of the App Service subnet>'
$endpointSubnetId = '<resource id of the private endpoint subnet>'

az deployment group create --resource-group $resourceGroup --name accessreview `
    --template-file deploy/bicep/appservice-existing-network.bicep `
    --parameters appName=$appName postgresServerName=$postgresName keyVaultName=$keyVaultName `
                 appSubnetResourceId=$appSubnetId privateEndpointSubnetResourceId=$endpointSubnetId `
                 clientId=$clientId clientSecret=$clientSecret
```

- Existing zones: add `webAppPrivateDnsZoneResourceId=<id> postgresPrivateDnsZoneResourceId=<id> keyVaultPrivateDnsZoneResourceId=<id>` (and `containerRegistryPrivateDnsZoneResourceId=<id>` for a container).
- Key Vault in another resource group: add `keyVaultResourceGroup=<resource group>`. You need Contributor and Role Based Access Control Administrator there too.
- Your existing Key Vault: add `existingKeyVaultResourceId=<resource id of the vault>`. The vault must use Azure RBAC (`az keyvault show --name <vault> --query properties.enableRbacAuthorization` returns `true`). Add `existingKeyVaultAccess=Vault` to give access to all secrets of the vault, and `existingKeyVaultPrivateEndpoint=true` when the App Service subnet does not reach a private endpoint of the vault yet. You need Key Vault Contributor (or Contributor) and Role Based Access Control Administrator on the vault.
- Container image: add `deploymentType=Container`. For your existing registry add `existingContainerRegistryResourceId=<resource id of the registry>` (and `existingContainerRegistryPrivateEndpoint=true` when the App Service subnet does not reach a private endpoint of the registry yet); for a new registry you can add `containerRegistryName=<name>`. Then see [Container image](#container-image-option-d).
- Zone redundancy: add `appServiceZoneRedundant=true appServiceInstanceCount=3` and `postgresHighAvailability=ZoneRedundant` (with a General Purpose size). To choose the PostgreSQL zones, add for example `postgresAvailabilityZone=1 postgresStandbyAvailabilityZone=2`.
- `SkuNotAvailable` for PostgreSQL: this size is not available for your subscription in this region. Option D uses General Purpose `Standard_D2ds_v5` by default; Burstable sizes (`postgresSkuName=Standard_B2s`) are cheaper but not available everywhere. Choose another `postgresSkuName` and deploy again with the same values. The tier follows from the size.
- Your own PostgreSQL server: add `databaseServer=ExistingServer existingServerConnectionString=$dbConnection` (option C).
- The deployment stops before it creates anything if a subnet value is not a subnet resource id.
- Delegate the App Service subnet first: `az network vnet subnet update --resource-group <network resource group> --vnet-name <virtual network> --name <subnet> --delegations Microsoft.Web/serverFarms`. Without the delegation the web app fails with "Subnet ... is missing a delegation to Microsoft.Web/serverFarms".

### For all options

The deployment takes about 10 minutes for the quick start and a few minutes more for the private network option. PostgreSQL takes most of the time. With your own server (option C) it takes a few minutes.

- To see the planned changes first, run the same command with `az deployment group what-if` instead of `az deployment group create`.
- To connect to the quick start database from your computer (psql, pgAdmin), add `administratorClientIp=<your public IP>`.
- Managed identity: the server accepts only Microsoft Entra sign-in. To connect yourself, make your account (or a group) a second Microsoft Entra administrator, then use a token as the password. A Microsoft Entra administrator can read and change all tables of the portal, so remove the entry when you no longer need it:

  ```powershell
  $me = az ad signed-in-user show --query "{id:id, upn:userPrincipalName}" -o json | ConvertFrom-Json
  az postgres flexible-server microsoft-entra-admin create --resource-group $resourceGroup --server-name $postgresName `
      --display-name $me.upn --object-id $me.id --type User
  $env:PGPASSWORD = az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv
  psql "host=$postgresName.postgres.database.azure.com dbname=accessreview user=$($me.upn) sslmode=require"

  # later
  az postgres flexible-server microsoft-entra-admin delete --resource-group $resourceGroup --server-name $postgresName `
      --object-id $me.id --yes
  ```

- Other parameters (sizes, PostgreSQL version, storage) are described at the top of `deploy/bicep/appservice.bicep`.

When the deployment is finished, delete the temporary file. The secret is now stored in Azure.

```powershell
Remove-Item $envFile
```

## Step 5. Build and deploy the portal

```powershell
dotnet publish src/AccessReviewPortal/AccessReviewPortal.csproj --configuration Release --output artifacts/publish
Compress-Archive -Path artifacts/publish/* -DestinationPath artifacts/portal.zip -Force
az webapp deploy --resource-group $resourceGroup --name $appName --src-path artifacts/portal.zip --type zip --track-status false
```

Option D: run `az webapp deploy` on a computer in your network that resolves `<app-name>.scm.azurewebsites.net` to the private endpoint (check with `nslookup`), or use GitHub Actions with a runner in your network.

The folder `artifacts` is in `.gitignore`. The upload takes about one minute. `--track-status false` returns when the package is uploaded: on a new web app the status tracking of Azure CLI can wait 10 minutes and then report an error, although the portal starts normally. Step 6 checks the start instead.

At the first start the portal creates the database schema (setting `Database__ApplyMigrationsOnStartup=true`). The first start takes one to two minutes.

### Container image (option D)

With `deploymentType=Container` the web app runs the image from the registry instead of the package above. Until the first image is in the registry, the web app answers 503. Build and push the image on a computer or runner in your network with Docker and Azure CLI. It must resolve `<registry>.azurecr.io` and `<registry>.<region>.data.azurecr.io` to the private endpoint of the registry. Your account needs AcrPush on the registry (Container Registry Repository Writer when the registry uses ABAC repository permissions).

```powershell
$loginServer = az deployment group show --resource-group $resourceGroup --name accessreview `
    --query properties.outputs.containerRegistryLoginServer.value -o tsv
az acr login --name $loginServer.Split('.')[0]
docker build --pull --target runtime --build-arg SOURCE_REVISION=$(git rev-parse HEAD) -t "$loginServer/access-review-portal:latest" .
docker push "$loginServer/access-review-portal:latest"
az webapp restart --resource-group $resourceGroup --name $appName
```

- App Service keeps the old container running until the new one answers. `/healthz` returns the commit of the image in the header `X-Portal-Revision`: check it from your network with `curl -sI https://<app-name>.azurewebsites.net/healthz`.
- The template sets the image tag `latest`. A new run of the template switches the web app back to `latest`, so push `latest` with every build. The workflow `deploy-appservice-container.yml` pushes the tags `<commit>` and `latest` and switches the web app to `<commit>` ([Deploy with GitHub Actions](#deploy-with-github-actions-optional)).
- The web app pulls the image with its managed identity `id-<app-name>` through the VNet integration. No registry password is stored.
- Microsoft Defender for Cloud scans the images in the registry when the subscription of the registry has the plan **Defender for Containers** or **Defender CSPM** with agentless container vulnerability assessment. The findings are in **Defender for Cloud > Recommendations** for the registry: one recommendation per package, for example "Update openssl" (older view: "Azure registry container images should have vulnerabilities resolved"). A registry without public access must allow trusted Microsoft services; the template sets this for a new registry.

## Step 6. Check the installation

Wait until the portal answers `ok`:

```powershell
for ($i = 0; $i -lt 30; $i++) {
    try { Invoke-RestMethod "https://$appName.azurewebsites.net/healthz"; break } catch { Start-Sleep -Seconds 10 }
}
```

If there is no `ok` after five minutes, see [Troubleshooting](#troubleshooting).

**Private network option and option D:** check that the web app can read both secrets from Key Vault. Both rows must show `Resolved`:

```powershell
$siteId = az webapp show --resource-group $resourceGroup --name $appName --query id -o tsv
az rest --method get --url "https://management.azure.com$siteId/config/configreferences/appsettings?api-version=2022-03-01" `
    --query "value[].{name:name, status:properties.status}" -o table
```

Then:

1. Open `https://<app-name>.azurewebsites.net` and sign in as `$adminUpn`.
2. Open **Settings > Diagnostics**. All checks of the scanner must be **OK**. The zero-privilege checks must say "Read-only". The section "PostgreSQL" shows the database version and size, how the portal signs in (managed identity or user name and password) and the encryption of the connection.
3. Open **Snapshots & Scan**. If day 1 of the month was less than 7 days ago, the portal already started the monthly snapshot by itself, a few minutes after the first start. Otherwise click **Run Live Scan**. A scan of a small tenant takes about 15 minutes, most of it for the Azure Activity Log. Large tenants: see [SIZING.md](SIZING.md).

## Step 7. Give access to the reviewers

Assign the portal roles to the reviewers, in the Microsoft Entra admin center: **Enterprise apps > AccessReview-Portal > Users and groups > Add user/group**, role `AccessReview.Reviewer` or `AccessReview.Admin`. Groups need Microsoft Entra ID P1 or P2.

Or with the script:

```powershell
.\setup-sp.ps1 -TenantId $tenantId -DisplayName 'AccessReview-Portal' -ReviewerUpn bob@contoso.com, carol@contoso.com -SkipSecret
```

The roles a reviewer needs to **apply** approved removals are in [INSTALL-GUIDE.md, section 2](../INSTALL-GUIDE.md#2-network-and-api-access).

## Deploy with GitHub Actions (optional)

The workflow `.github/workflows/deploy-appservice.yml` builds, tests and deploys the portal to the web app. It signs in to Azure with OpenID Connect (OIDC) and a user-assigned managed identity. No secret is stored in GitHub. Do steps 1 to 4 first. The workflow deploys only the code.

For option D with a container image (`deploymentType=Container`), use `.github/workflows/deploy-appservice-container.yml` instead. It builds the image on the runner, pushes it to the registry and switches the web app to it. In addition to the steps below it needs the role AcrPush on the registry, the variable `ACR_LOGIN_SERVER` and a runner with Docker.

You need **Admin** on the GitHub repository. The Azure rights are in [PERMISSIONS.md, section 8](PERMISSIONS.md#8-pipelines-optional).

### Managed identity and federated credential

Open the repository and copy the subject prefix from **Settings > Actions > OIDC**, field **Default subject claim prefix**.

- Repositories created or renamed after 15 July 2026 use immutable subjects: `repo:<owner>@<owner_id>/<repo>@<repo_id>`.
- Older repositories use `repo:<owner>/<repo>`, until you switch them to immutable subjects on the same page.

The subject must match exactly. Otherwise the sign-in fails with `AADSTS70021` or `AADSTS700213` ("No matching federated identity record found").

```powershell
$repoSubject = 'repo:contoso@1234567/access-review@987654321'   # Settings > Actions > OIDC
az identity create --resource-group $resourceGroup --name id-accessreview-github --location $location --output none
az identity federated-credential create --resource-group $resourceGroup --identity-name id-accessreview-github `
    --name github-production --issuer https://token.actions.githubusercontent.com `
    --subject "${repoSubject}:environment:production" --audiences api://AzureADTokenExchange --output none

$identity = az identity show --resource-group $resourceGroup --name id-accessreview-github --query "{clientId:clientId, principalId:principalId}" -o json | ConvertFrom-Json
$siteId = az webapp show --resource-group $resourceGroup --name $appName --query id -o tsv
az role assignment create --assignee-object-id $identity.principalId --assignee-principal-type ServicePrincipal `
    --role 'Website Contributor' --scope $siteId --output none
$identity.clientId
```

Container image: the identity also pushes to the registry. With ABAC repository permissions use the role `Container Registry Repository Writer` instead of `AcrPush`.

```powershell
$registryId = az deployment group show --resource-group $resourceGroup --name accessreview `
    --query properties.outputs.containerRegistryId.value -o tsv
az role assignment create --assignee-object-id $identity.principalId --assignee-principal-type ServicePrincipal `
    --role AcrPush --scope $registryId --output none
```

The template turns off basic authentication (FTP and SCM). The workflow does not need it: `az webapp deploy` uses the Microsoft Entra token of the identity.

### GitHub environment and variables

1. **Settings > Environments > New environment**, name `production`.
2. **Deployment branches and tags**: choose **Selected branches and tags** and add the branch `main`.
3. **Environment variables > Add environment variable**, for each row:

| Name | Value |
|---|---|
| `AZURE_CLIENT_ID` | client id of the managed identity (`$identity.clientId`) |
| `AZURE_TENANT_ID` | `$tenantId` |
| `AZURE_SUBSCRIPTION_ID` | `$subscriptionId` |
| `AZURE_RESOURCE_GROUP` | `$resourceGroup` |
| `APPSERVICE_NAME` | `$appName` |
| `ACR_LOGIN_SERVER` | container image only: the template output `containerRegistryLoginServer`, for example `crcontoso.azurecr.io` |

### Runner

The workflow runs on a self-hosted runner with the labels `self-hosted`, `linux` and `access-review`. Prepare a Linux VM for it, build the runner image with Azure CLI, then start it with a registration token ([GITHUB-RUNNER-SETUP.md](GITHUB-RUNNER-SETUP.md): "Prepare the runner host" and steps 3 to 6):

```bash
cd ~/github-runner                      # the folder deploy/github-runner, copied to the runner VM
sudo docker build --build-arg AZURE_CLI=true -t access-review-runner:1 .

read -rsp 'Registration token: ' RUNNER_TOKEN; echo; export RUNNER_TOKEN
sudo --preserve-env=RUNNER_TOKEN docker run -d --name access-review-runner --restart unless-stopped \
  -e RUNNER_URL=https://github.com/<owner>/<repo> \
  -e RUNNER_TOKEN \
  -e RUNNER_NAME=access-review-runner-01 \
  -e RUNNER_LABELS=access-review \
  access-review-runner:1
unset RUNNER_TOKEN
```

`-e RUNNER_TOKEN` without a value passes the variable to the container, so the token is not in the command line or in the shell history.
This workflow does not use Docker, so the runner does not need the Docker socket. The runner needs outbound HTTPS to GitHub (see GITHUB-RUNNER-SETUP.md), `builds.dotnet.microsoft.com`, `api.nuget.org`, `login.microsoftonline.com`, `management.azure.com`, `<app-name>.scm.azurewebsites.net` and `<app-name>.azurewebsites.net` (health check).

The container workflow `deploy-appservice-container.yml` builds the image with the Docker engine of the host: start the runner with the Docker socket (`-v /var/run/docker.sock:/var/run/docker.sock`, see GITHUB-RUNNER-SETUP.md). It needs `mcr.microsoft.com` and `*.data.mcr.microsoft.com` for the base images, and the private endpoint of the registry, instead of the SCM address of the web app.

To use a GitHub-hosted runner instead, add the **repository** variable `RUNNER_LABELS` with the value `["ubuntu-latest"]` (**Settings > Secrets and variables > Actions > Variables**). The web app must then accept deployments from the internet, which is the case in both options of the template.

### Run the workflow

**Actions > deploy-appservice > Run workflow**, branch `main`. Or with GitHub CLI:

```bash
gh workflow run deploy-appservice.yml --repo <owner>/<repo> --ref main
gh run watch --repo <owner>/<repo>
```

The first run takes about five minutes. The step **Health check** ends with "The portal answers". To deploy every push to `main`, add the `push` trigger at the top of the workflow file (see the comment there). Container image: run `deploy-appservice-container.yml` the same way.

## Vulnerability checks

- **Packages.** Every build (local, CI, both workflows and `docker build`) runs the NuGet audit of the .NET SDK. `Directory.Build.props` turns known vulnerabilities of high and critical severity into errors (NU1903, NU1904), also for transitive packages: the build stops and names the package and the advisory. Update the package, or add a direct reference with the fixed version for a transitive one. Low and moderate vulnerabilities stay warnings. The audit reads the vulnerability data of the package source (nuget.org); warning NU1900 means that it could not read them, so the check did not run.
- **Container image.** Microsoft Defender for Containers or Defender CSPM scans the image in Azure Container Registry: the packages of the operating system and the .NET packages ([Container image](#container-image-option-d)). The build uses `--pull`, so each image gets the current patches of the .NET base image. Run the container workflow regularly, for example once a month, also without code changes: new fixes of the base image get into the portal only with a new image.
- **Code package.** App Service updates the operating system and the .NET runtime of the web app. The packages of the portal are checked by the build.
- Optional: Dependabot alerts in GitHub, and the GitHub connector of Defender for Cloud (DevOps security), also report vulnerable packages of the repository.

## Update, client secret, removal

### Update to a new version

```powershell
git pull
dotnet publish src/AccessReviewPortal/AccessReviewPortal.csproj --configuration Release --output artifacts/publish
Compress-Archive -Path artifacts/publish/* -DestinationPath artifacts/portal.zip -Force
az webapp deploy --resource-group $resourceGroup --name $appName --src-path artifacts/portal.zip --type zip
```

On an existing web app the command waits until the new version runs (usually under one minute). The portal updates the database schema at the start. A scan that runs during the update is stopped and marked as failed. Run it again.

With GitHub Actions, run the workflow `deploy-appservice` again instead.

### Renew the client secret

The client secret is valid for 6 months. Before it expires:

```powershell
.\setup-sp.ps1 -TenantId $tenantId -DisplayName 'AccessReview-Portal' -EnvFile $envFile
$values = Get-Content $envFile -Raw | ConvertFrom-StringData
```

- Quick start: `az webapp config appsettings set --resource-group $resourceGroup --name $appName --settings "AzureAd__ClientSecret=$($values.AzureAd__ClientSecret)" --output none`
- Private network: run the commands of step 4 again (from `$values = ...`), with the same database parameters. With `databaseAuthentication=Password`, a new PostgreSQL password is fine: the template changes the password of the server and the connection string together. Then restart the web app: `az webapp restart --resource-group $resourceGroup --name $appName`.

Then delete `$envFile`, and delete the old secret in **App registrations > AccessReview-Portal > Certificates & secrets**.

### Remove everything

```powershell
az group delete --name $resourceGroup --yes

$appId = az ad app list --display-name 'AccessReview-Portal' --query '[0].appId' -o tsv
$spId = az ad sp show --id $appId --query id -o tsv
az role assignment delete --assignee $spId --scope "/providers/Microsoft.Management/managementGroups/$tenantId"
az ad app delete --id $appId
```

If you used `-ManagementGroupId` or `-SubscriptionId` in step 3, delete the role assignments of the scanner on those scopes. A Key Vault with purge protection stays in the deleted state for 90 days. The managed identity of GitHub Actions is in the resource group and is deleted with it. Remove the runner as described in [GITHUB-RUNNER-SETUP.md](GITHUB-RUNNER-SETUP.md#remove-the-runner).

## Troubleshooting

| Problem | Check |
|---|---|
| Deployment: `SkuNotAvailable` | The PostgreSQL size is not available for your subscription in this region. Choose another region or size (`postgresSkuName`, `postgresSkuTier`). |
| Deployment: `InternalSubscriptionIsOverQuotaForSku`, "Current Limit (P0v3 AZ App Service instances)" | Zone redundancy needs its own quota for the plan size in the region. Ask for more quota (**Help + support**, link in the message), or choose fewer instances or no zone redundancy. |
| Build: `error NU1903` or `NU1904` ("has a known high/critical severity vulnerability") | A package has a known vulnerability ([Vulnerability checks](#vulnerability-checks)). Update the package, or add a direct reference with the fixed version for a transitive package. |
| Container image: the web app answers 503 | No image yet: run the container workflow or push the image ([Container image](#container-image-option-d)). Read the container log through Azure Resource Manager (the Kudu site has no public access): `az rest --method post --url "https://management.azure.com$siteId/containerlogs?api-version=2024-04-01"`. `unauthorized` or `insufficient_scope`: the identity `id-<app-name>` has no pull role; with ABAC repository permissions it needs Container Registry Repository Reader. A name or timeout error: the App Service subnet does not reach the private endpoint of the registry, or `privatelink.azurecr.io` has no records for the registry and its data endpoint. |
| GitHub Actions: `unauthorized` or `insufficient_scope` in **Push the image** | The managed identity of the workflow has no AcrPush on the registry (Container Registry Repository Writer with ABAC repository permissions), or the role is new: wait some minutes and run the workflow again. |
| Deployment: `AvailabilityZoneNotAvailable` | The PostgreSQL size is not offered in this zone of the region. Choose another zone, or leave the zones empty: then Azure chooses. |
| Azure portal: after **Review + create** the form keeps loading and **Create** stays grey | This happens in a form with more than one tab, for example your own copy of the form. When a form reads data from Azure, the Azure portal waits without end if you did not open every tab. Close the form, open it again, and open every tab before **Review + create**. The form of option D is one page. |
| Deployment: `Conflict` on the App Service plan, "No available instances to satisfy this request. App Service is attempting to increase capacity." | The region has no free capacity for this plan size at the moment. Run the deployment again some minutes later (the template is idempotent), or choose another plan size or region. |
| Deployment: `RequestDisallowedByPolicy` | A policy blocks a resource or a setting. The message names the policy. See [PERMISSIONS.md, section 5](PERMISSIONS.md#5-azure-policy). |
| Deployment: `AuthorizationFailed`, `LinkedAuthorizationFailed` | Rights at the scope in the message. See [PERMISSIONS.md, section 9](PERMISSIONS.md#9-errors-and-what-they-mean). |
| `/healthz` returns 503 or times out | Read the start log of the portal: `az webapp log tail --resource-group $resourceGroup --name $appName`. "Database is not ready" means that the portal cannot reach PostgreSQL. "The database schema is not up to date" means that `Database__ApplyMigrationsOnStartup` is false. |
| Your own server: "Database is not ready (Failed to connect ...)" or "The operation has timed out" | No network path from the app subnet to the server on port 5432: VNet integration, routes (peering, VPN, ExpressRoute), firewalls, intrusion detection, the firewall of the server. Test from the web app: **Advanced Tools > Go > Bash** (Kudu uses the VNet integration too), then `timeout 5 bash -c '</dev/tcp/<server>/5432' && echo open`. |
| Your own server: `no pg_hba.conf entry for host` or `password authentication failed` | Add the `hostssl` line for the app subnet to `pg_hba.conf` and reload PostgreSQL. Check the login and password in the connection string (Key Vault secret `portal-db-connection` or app setting `ConnectionStrings__DefaultConnection`). |
| Your own server: "The remote certificate was rejected" or a name mismatch | The CA of the server certificate is missing (`existingServerCaCertificate`, app setting `Database__RootCertificate`), or `Host` in the connection string is not a name or IP address in the certificate. |
| Managed identity: `role "id-<app-name>" does not exist` or a token error | The identity is not a Microsoft Entra administrator of the server, or `Database__ManagedIdentityClientId` is not the client ID of `id-<app-name>`. Check **Security > Authentication** of the server and run the template again. |
| Private network: Key Vault reference status `AccessToKeyVaultDenied` | The web app cannot read Key Vault: no role, no network path, or the role is not active yet. Check the role `Key Vault Secrets User` of the identity `id-<app-name>` on the vault (an existing vault of option D: on the secrets `<app-name>-client-secret` and `<app-name>-db-connection`, and the vault must use Azure RBAC), and the private endpoint of the vault. Then restart the web app: `az webapp restart --resource-group $resourceGroup --name $appName`. If you changed the template to use a vault with public access, check that no policy set public network access to **Disabled** ([PERMISSIONS.md, section 5](PERMISSIONS.md#5-azure-policy)). |
| Option D: `Subnet ... is missing a delegation to Microsoft.Web/serverFarms` | The App Service subnet is not delegated. Delegate it (command in [option D](#option-d-your-existing-network-private-endpoints-only)) and run the deployment again. |
| Option D: the browser shows `403 - Forbidden` or `Ip Forbidden`, or `az webapp deploy` fails with 403 | The request came from the internet: the web app has no public access. Use a computer in your network. `nslookup <app-name>.azurewebsites.net` must return the private IP address of the private endpoint `pe-<app-name>`. |
| Option D: deployment error "A virtual network cannot be linked to multiple zones with overlapping namespaces" | The virtual network is already linked to a zone with this name, for example a central `privatelink.*` zone. Choose **Use existing zones** in the portal form, or give the zone resource ids. |
| Sign-in fails | `AADSTS50011`: the address is not a redirect URI; run `setup-sp.ps1 -PortalUrl https://$appName.azurewebsites.net -SkipSecret`. More errors: [PERMISSIONS.md, section 9](PERMISSIONS.md#9-errors-and-what-they-mean). |
| Diagnostics: a Graph or Azure check fails | Admin consent or a role of the scanner is missing. Run `setup-sp.ps1` again. |
| The scan runs for a long time | Large tenants and busy subscriptions. See [SIZING.md](SIZING.md). |
| GitHub Actions: the job stays "Waiting for a runner" | No online runner has all labels `self-hosted`, `linux`, `access-review`. Check **Settings > Actions > Runners**, or set `RUNNER_LABELS`. |
| GitHub Actions: `AADSTS70021` or `AADSTS700213` in **Sign in to Azure** | The subject of the federated credential does not match. See [Managed identity and federated credential](#managed-identity-and-federated-credential). |
| GitHub Actions: `AuthorizationFailed` in **Deploy to App Service** | The managed identity has no **Website Contributor** role on the web app, or the role is new: wait some minutes and run the workflow again. |
