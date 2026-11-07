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
 Reviewer's browser ──HTTPS──> Azure App Service (Linux, .NET 10, one instance)
                                  │  sign-in: Microsoft Entra ID
                                  ├──> Microsoft Graph, Azure Resource Manager, Azure Resource Graph
                                  │      scanner: read-only application permissions of the app registration
                                  │      revoke:  delegated token of the signed-in reviewer
                                  ├──> Azure Database for PostgreSQL Flexible Server (snapshots, decisions, sign-in keys)
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

Sizes for larger tenants: [SIZING.md](SIZING.md).

In both options the portal address is `https://<app-name>.azurewebsites.net` and the sign-in page is public. Only users with a portal role can open the portal.

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

Read the values from the script output and create a random password for the PostgreSQL administrator:

```powershell
$values = Get-Content $envFile -Raw | ConvertFrom-StringData
$clientId = $values.AzureAd__ClientId
$clientSecret = $values.AzureAd__ClientSecret
$postgresPassword = 'Pg1-' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16))

az group create --name $resourceGroup --location $location --output none
```

The template stores the password in the connection string of the portal. You do not need to keep it. To connect to the database yourself later, set a new password with `az postgres flexible-server update --resource-group $resourceGroup --name $postgresName --admin-password <new password>`, then update the connection string (run the template again with the new password).

### Option A: quick start

```powershell
az deployment group create --resource-group $resourceGroup --name accessreview `
    --template-file deploy/bicep/appservice.bicep `
    --parameters appName=$appName postgresServerName=$postgresName `
                 clientId=$clientId clientSecret=$clientSecret postgresAdminPassword=$postgresPassword
```

### Option B: private network

With a new virtual network (`10.20.0.0/24`, change it with `vnetAddressPrefix=<range>`) and new private DNS zones in the resource group:

```powershell
az deployment group create --resource-group $resourceGroup --name accessreview `
    --template-file deploy/bicep/appservice.bicep `
    --parameters appName=$appName postgresServerName=$postgresName keyVaultName=$keyVaultName privateNetwork=true `
                 clientId=$clientId clientSecret=$clientSecret postgresAdminPassword=$postgresPassword
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
                 clientId=$clientId clientSecret=$clientSecret postgresAdminPassword=$postgresPassword
```

Leave out the two zone parameters to create new zones in the resource group and link them to your virtual network.

The Key Vault has purge protection. After you delete it, its name stays reserved for 90 days. For a test, add `enablePurgeProtection=false`.

### For both options

The deployment takes about 8 minutes for the quick start and about 11 minutes for the private network option. PostgreSQL takes most of the time.

- To see the planned changes first, run the same command with `az deployment group what-if` instead of `az deployment group create`.
- To connect to the quick start database from your computer (psql, pgAdmin), add `administratorClientIp=<your public IP>`.
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

The folder `artifacts` is in `.gitignore`. The upload takes about one minute. `--track-status false` returns when the package is uploaded: on a new web app the status tracking of Azure CLI can wait 10 minutes and then report an error, although the portal starts normally. Step 6 checks the start instead.

At the first start the portal creates the database schema (setting `Database__ApplyMigrationsOnStartup=true`). The first start takes one to two minutes.

## Step 6. Check the installation

Wait until the portal answers `ok`:

```powershell
for ($i = 0; $i -lt 30; $i++) {
    try { Invoke-RestMethod "https://$appName.azurewebsites.net/healthz"; break } catch { Start-Sleep -Seconds 10 }
}
```

If there is no `ok` after five minutes, see [Troubleshooting](#troubleshooting).

**Private network option only:** check that the web app can read both secrets from Key Vault. Both rows must show `Resolved`:

```powershell
$siteId = az webapp show --resource-group $resourceGroup --name $appName --query id -o tsv
az rest --method get --url "https://management.azure.com$siteId/config/configreferences/appsettings?api-version=2022-03-01" `
    --query "value[].{name:name, status:properties.status}" -o table
```

Then:

1. Open `https://<app-name>.azurewebsites.net` and sign in as `$adminUpn`.
2. Open **Settings > Diagnostics**. All checks of the scanner must be **OK**. The zero-privilege checks must say "Read-only". The section "PostgreSQL" shows the database version and size.
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

To use a GitHub-hosted runner instead, add the **repository** variable `RUNNER_LABELS` with the value `["ubuntu-latest"]` (**Settings > Secrets and variables > Actions > Variables**). The web app must then accept deployments from the internet, which is the case in both options of the template.

### Run the workflow

**Actions > deploy-appservice > Run workflow**, branch `main`. Or with GitHub CLI:

```bash
gh workflow run deploy-appservice.yml --repo <owner>/<repo> --ref main
gh run watch --repo <owner>/<repo>
```

The first run takes about five minutes. The step **Health check** ends with "The portal answers". To deploy every push to `main`, add the `push` trigger at the top of the workflow file (see the comment there).

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
- Private network: run the commands of step 4 again (from `$values = ...`). A new PostgreSQL password is fine: the template changes the password of the server and the connection string together. Then restart the web app: `az webapp restart --resource-group $resourceGroup --name $appName`.

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
| Deployment: `Conflict` on the App Service plan, "No available instances to satisfy this request. App Service is attempting to increase capacity." | The region has no free capacity for this plan size at the moment. Run the deployment again some minutes later (the template is idempotent), or choose another plan size or region. |
| Deployment: `RequestDisallowedByPolicy` | A policy blocks a resource or a setting. The message names the policy. See [PERMISSIONS.md, section 5](PERMISSIONS.md#5-azure-policy). |
| Deployment: `AuthorizationFailed`, `LinkedAuthorizationFailed` | Rights at the scope in the message. See [PERMISSIONS.md, section 9](PERMISSIONS.md#9-errors-and-what-they-mean). |
| `/healthz` returns 503 or times out | Read the start log of the portal: `az webapp log tail --resource-group $resourceGroup --name $appName`. "Database is not ready" means that the portal cannot reach PostgreSQL. "The database schema is not up to date" means that `Database__ApplyMigrationsOnStartup` is false. |
| Private network: Key Vault reference status `AccessToKeyVaultDenied` | The web app cannot read Key Vault: no role, no network path, or the role is not active yet. Check the role `Key Vault Secrets User` of the identity `id-<app-name>` on the vault, and the private endpoint `pe-<vault name>`. Then restart the web app: `az webapp restart --resource-group $resourceGroup --name $appName`. If you changed the template to use a vault with public access, check that no policy set public network access to **Disabled** ([PERMISSIONS.md, section 5](PERMISSIONS.md#5-azure-policy)). |
| Sign-in fails | `AADSTS50011`: the address is not a redirect URI; run `setup-sp.ps1 -PortalUrl https://$appName.azurewebsites.net -SkipSecret`. More errors: [PERMISSIONS.md, section 9](PERMISSIONS.md#9-errors-and-what-they-mean). |
| Diagnostics: a Graph or Azure check fails | Admin consent or a role of the scanner is missing. Run `setup-sp.ps1` again. |
| The scan runs for a long time | Large tenants and busy subscriptions. See [SIZING.md](SIZING.md). |
| GitHub Actions: the job stays "Waiting for a runner" | No online runner has all labels `self-hosted`, `linux`, `access-review`. Check **Settings > Actions > Runners**, or set `RUNNER_LABELS`. |
| GitHub Actions: `AADSTS70021` or `AADSTS700213` in **Sign in to Azure** | The subject of the federated credential does not match. See [Managed identity and federated credential](#managed-identity-and-federated-credential). |
| GitHub Actions: `AuthorizationFailed` in **Deploy to App Service** | The managed identity has no **Website Contributor** role on the web app, or the role is new: wait some minutes and run the workflow again. |
