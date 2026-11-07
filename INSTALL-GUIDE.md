# Access Review Automation Portal - installation and configuration guide

This guide installs the Access Review Automation Portal. The portal is a **sample / prototype**: use it as a reference and adapt it to your environment, your security rules and your change process before you use it in production.

Contents

1. [Architecture](#1-architecture)
2. [Network and API access](#2-network-and-api-access)
3. [App registration in Microsoft Entra ID](#3-app-registration-in-microsoft-entra-id)
4. [Deploy on a VM with Docker](#4-deploy-on-a-vm-with-docker)
5. [Alternative: Azure App Service and Azure Database for PostgreSQL](#5-alternative-azure-app-service-and-azure-database-for-postgresql)
6. [CI/CD pipelines](#6-cicd-pipelines)
7. [Subscription discovery and onboarding](#7-subscription-discovery-and-onboarding)
8. [Scheduled snapshots, quarterly campaigns and Power Automate](#8-scheduled-snapshots-quarterly-campaigns-and-power-automate)
9. [Operations](#9-operations)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Architecture

```
 Reviewer's browser ──HTTPS──> Portal container (ASP.NET Core .NET 10, Blazor)
        │                         │   ├─ scan (background): read-only app identity ("scanner")
        │                         │   └─ revoke: delegated token of the signed-in reviewer
        │                         │
        └─ sign-in ─> Microsoft Entra ID      ├──> Microsoft Graph            (users, groups, Entra roles, PIM, sign-ins, audit)
                                              ├──> Azure Resource Manager     (RBAC, Activity Log, permissions, Defender pricing)
                                              ├──> Azure Resource Graph       (subscriptions, management groups, Defender CSPM/CIEM)
                                              ├──> Log Analytics (optional)   (90 days of Activity Log and sign-ins)
                                              └──> PostgreSQL 18              (snapshots, decisions, sign-in keys, token cache)
```

Main ideas:

- **Zero-privilege scanner.** The app identity only reads: Graph application permissions `*.Read.*` and Azure `Reader` + `Monitoring Reader`. It cannot change anything in the tenant.
- **Revoke as the signed-in user.** Every removal (Azure role assignment, PIM schedule, Entra role, cloud group member) runs with the delegated token of the reviewer who clicks **Apply** on the **Changes Pending** page. The portal first checks the rights of this user on the scope. A reviewer without the needed role can record decisions, but cannot apply them.
- **Containers.** The same image runs on a VM with Docker Compose, on Azure App Service (Web App for Containers) or on Azure Container Apps.
- **State in PostgreSQL.** Snapshots, decisions, Data Protection keys (sign-in cookies) and the token cache are in PostgreSQL. Sign-ins survive a restart or an update of the container.
- **Schema migrations** run as a separate step (EF Core migration bundle, `migrate` container). The step is idempotent: it applies only what is missing.

## 2. Network and API access

### Connections

| From | To | Port | Purpose |
|---|---|---|---|
| Reviewer's browser | Portal | 443 or the published port (sample: 8088), HTTPS | User interface |
| Reviewer's browser | `login.microsoftonline.com` | 443 | Sign-in |
| Portal | `login.microsoftonline.com` | 443 | Tokens (scanner and delegated) |
| Portal | `graph.microsoft.com` | 443 | Directory, Entra roles, PIM, sign-in and audit logs, access reviews, group member removal |
| Portal | `management.azure.com` | 443 | Role assignments, Activity Log, permission checks, Defender for Cloud pricing, Resource Graph |
| Portal | `api.loganalytics.io` | 443 | Optional: Log Analytics workspace with AzureActivity / SigninLogs |
| Portal | PostgreSQL | 5432 | Database (internal Docker network or Azure Database for PostgreSQL) |
| Portal | Webhook URL | 443 | Optional: "campaign ready" notification (Power Automate, Logic Apps) |
| Pipeline runner / agent | Container registry, package feeds | 443 | Build and push images |
| Pipeline runner / agent | Target VM | 22 | Deployment over SSH |
| Target VM | Container registry | 443 | Pull images |

### Permissions of the scanner (application permissions, read-only)

| API | Permission | Used for |
|---|---|---|
| Microsoft Graph | `User.Read.All` | Users, department, manager, account status |
| Microsoft Graph | `Directory.Read.All` | Groups, owners, transitive members, principals |
| Microsoft Graph | `RoleManagement.Read.Directory` | Entra role definitions, assignments, PIM schedules |
| Microsoft Graph | `AuditLog.Read.All` | Sign-in logs, directory audit, `signInActivity` |
| Microsoft Graph | `AccessReview.Read.All` | Decisions of native Entra ID access reviews |
| Azure RBAC | `Reader` | Role assignments, resources, Resource Graph, Defender for Cloud assessments |
| Azure RBAC | `Monitoring Reader` | Activity Log |

The scanner must **not** have `Owner`, `Contributor`, `User Access Administrator`, `Role Based Access Control Administrator` or any Graph `*.ReadWrite.*` application permission. The page **Settings > Diagnostics** checks this.

### Delegated permissions (used only with the token of the signed-in reviewer)

| API | Permission | Used for |
|---|---|---|
| Microsoft Graph | `openid`, `profile`, `offline_access`, `User.Read` | Sign-in |
| Azure Resource Manager | `user_impersonation` | Delete Azure role assignments, PIM `AdminRemove` requests, permission checks |
| Microsoft Graph | `RoleAssignmentSchedule.ReadWrite.Directory` | Remove active Entra role assignments (PIM `adminRemove`) |
| Microsoft Graph | `RoleEligibilitySchedule.ReadWrite.Directory` | Remove eligible Entra role assignments |
| Microsoft Graph | `RoleManagement.ReadWrite.Directory` | Entra role management requests |
| Microsoft Graph | `GroupMember.ReadWrite.All` | Remove members from cloud groups |

A delegated permission never gives more rights than the user has. The portal asks for each permission when it is needed (incremental consent), or an administrator grants consent for all users once.

### Rights a reviewer needs to apply changes

| What is removed | Role of the signed-in reviewer |
|---|---|
| Azure role assignment (permanent) | `Owner`, `User Access Administrator` or `Role Based Access Control Administrator` on the scope (`Microsoft.Authorization/roleAssignments/delete`) |
| Azure PIM eligible / active assignment | Same roles (`roleEligibilityScheduleRequests/write`, `roleAssignmentScheduleRequests/write`) |
| Entra ID role assignment | Active `Privileged Role Administrator` or `Global Administrator` |
| Member of a cloud group | Group owner, `Groups Administrator` or `User Administrator`. Role-assignable groups: owner or `Privileged Role Administrator` |
| Member of a group synced from on-premises AD | Only in AD. The portal creates a PowerShell script (`Remove-ADGroupMember`). |

### Portal roles (app roles)

| App role | Can do |
|---|---|
| `AccessReview.Admin` | Everything below, plus scans, subscription discovery, "Monitor in Portal", automation settings |
| `AccessReview.Reviewer` | Review, decisions, Excel export, revoke within the own rights |

Assign the app roles in **Microsoft Entra admin center > Enterprise applications > (portal app) > Users and groups**. Use groups for larger teams.

## 3. App registration in Microsoft Entra ID

You can do this with the script `setup-sp.ps1` (recommended) or by hand.

### With the script

Run it as a Global Administrator (or Privileged Role Administrator + Application Administrator) who is also `Owner` or `User Access Administrator` on the management group. Modules: `Az.Accounts`, `Az.Resources`.

The full list of rights for each step, the checks to run before you start and the common errors are in [docs/PERMISSIONS.md](docs/PERMISSIONS.md).

```powershell
Connect-AzAccount -Tenant <tenant-id>
.\setup-sp.ps1 -TenantId <tenant-id> `
    -PortalUrl https://accessreview.contoso.com:8088 `
    -AdminUpn alice@contoso.com `
    -ReviewerUpn bob@contoso.com `
    -GrantDelegatedConsent `
    -EnvFile .\.env
```

What the script does (it is safe to run again):

1. Creates the app registration and the service principal (single tenant).
2. Adds the web redirect URIs `<PortalUrl>/signin-oidc` and `<PortalUrl>/signout-callback-oidc`, and turns on **Allow public client flows** (needed for "sign in with a code from another device").
3. Adds the app roles `AccessReview.Admin` and `AccessReview.Reviewer`, and assigns them to the given users.
4. Grants the read-only Graph application permissions (admin consent).
5. With `-GrantDelegatedConsent`: grants admin consent for the delegated permissions.
6. Assigns `Reader` and `Monitoring Reader` to the scanner on the Tenant Root Group (or `-ManagementGroupId` / `-SubscriptionId`).
7. **Removes write rights** of the scanner that an older setup gave (for example `User Access Administrator`, Graph `*.ReadWrite.*` application permissions). Use `-KeepExistingPrivileges` to only report them.
8. Creates a client secret and writes `AzureAd__TenantId`, `AzureAd__ClientId`, `AzureAd__ClientSecret` and `Portal__PublicBaseUrl` to the `.env` file. The secret is not printed.

### By hand (Entra admin center)

1. **App registrations > New registration**. Name: `AccessReview-Portal`. Supported account types: this organizational directory only. Redirect URI: **Web**, `https://<portal address>/signin-oidc`.
2. **Authentication**: add the redirect URI `https://<portal address>/signout-callback-oidc`. Front-channel logout URL: `https://<portal address>/signout-oidc`. Under **Advanced settings**, set **Allow public client flows** to **Yes**.
3. **App roles > Create app role** (allowed member types: Users/Groups): `AccessReview.Admin` and `AccessReview.Reviewer`.
4. **API permissions > Add a permission**:
   - Microsoft Graph, **Application permissions**: `User.Read.All`, `Directory.Read.All`, `RoleManagement.Read.Directory`, `AuditLog.Read.All`, `AccessReview.Read.All`.
   - Microsoft Graph, **Delegated permissions**: `openid`, `profile`, `offline_access`, `User.Read`, `RoleAssignmentSchedule.ReadWrite.Directory`, `RoleEligibilitySchedule.ReadWrite.Directory`, `RoleManagement.ReadWrite.Directory`, `GroupMember.ReadWrite.All`.
   - Azure Service Management, **Delegated permissions**: `user_impersonation`.
   - Click **Grant admin consent for <tenant>**.
5. **Certificates & secrets > New client secret**. Copy the value into `.env` (`AzureAd__ClientSecret`).
6. **Enterprise applications > AccessReview-Portal > Users and groups**: assign the app roles.
7. Azure portal, **Management groups > Tenant Root Group > Access control (IAM)**: assign `Reader` and `Monitoring Reader` to the service principal `AccessReview-Portal`.

### Defender for Cloud CSPM (optional)

The column **Defender CSPM (CIEM)** shows findings of Microsoft Defender for Cloud: overprovisioned identities, inactive identities (45 days), permanent privileged roles, and guest or blocked accounts with permissions. The subscription needs the **Defender CSPM** plan with the **Permissions management (CIEM)** extension. The scanner reads the findings with `Reader`; no extra permission is needed.

### Conditional Access

- Sign-in with a code from another device uses the OAuth device code flow. If a Conditional Access policy blocks **Authentication flows > Device code flow**, exclude the portal app or use the normal **Sign in with Microsoft** button.
- The scanner signs in as an application (client credentials). Workload identity Conditional Access policies apply to it.

## 4. Deploy on a VM with Docker

### Requirements

- Linux VM with Docker Engine and the Compose plugin, or Windows Server with Docker (Linux containers).
- 4 vCPU, 16 GB RAM, 100 GB disk for a large tenant (tens of thousands of assignments per scan). 2 vCPU / 8 GB is enough for tests.
- A DNS name for the portal and a TLS certificate (PFX) for this name. Entra ID accepts `http` redirect URIs only for `localhost`, so the portal needs HTTPS.

### Folder layout

```
/opt/access-review            (Windows: C:\access-review)
├── docker-compose.yml
├── .env                      configuration and secrets (from .env.example)
├── certs/portal.pfx          TLS certificate of the portal
└── deploy/postgres/postgresql.conf
```

For a build on the VM itself, copy the whole repository. For a deployment from a pipeline, the pipeline copies `docker-compose.yml` and `deploy/postgres/postgresql.conf` and pulls the images.

### Steps

1. Copy `.env.example` to `.env` and fill it in:

   ```bash
   cp .env.example .env
   openssl rand -hex 24    # use the output as POSTGRES_PASSWORD
   ```

   | Setting | Value |
   |---|---|
   | `POSTGRES_PASSWORD` | Random password, no `;` or spaces |
   | `AzureAd__TenantId`, `AzureAd__ClientId`, `AzureAd__ClientSecret` | From step 3 (`setup-sp.ps1 -EnvFile .env` writes them) |
   | `ASPNETCORE_Kestrel__Certificates__Default__Password` | Password of `certs/portal.pfx` |
   | `Portal__PublicBaseUrl` | `https://accessreview.contoso.com:8088` |
   | `WEB_PORT` | Port on the VM (sample: `8088`) |

2. Put the certificate into `certs/portal.pfx`.

3. Start everything:

   ```bash
   docker compose up -d --build
   ```

   Compose starts PostgreSQL 18, runs the migration bundle (`migrate` exits when the schema is up to date) and starts the portal. Without `--build` it uses the images given in `PORTAL_IMAGE` / `MIGRATOR_IMAGE`.

4. Check:

   ```bash
   docker compose ps
   docker compose logs migrate
   curl -fsS https://accessreview.contoso.com:8088/healthz
   ```

   `/healthz` returns `ok`. Open the portal, sign in, and open **Settings > Diagnostics**: all checks of the scanner should be **OK**, and the zero-privilege checks should say "Read-only".

5. Run the first scan on **Snapshots & Scan**, or wait for the monthly schedule.

### Windows Server notes

- Docker commands in an SSH session cannot use the Windows credential store. Set `DOCKER_CONFIG` to a folder whose `config.json` has no credential helper, for example:

  ```powershell
  New-Item -ItemType Directory -Force C:\access-review\.docker-nocreds | Out-Null
  '{"auths":{"example.invalid":{}}}' | Set-Content C:\access-review\.docker-nocreds\config.json -NoNewline
  $env:DOCKER_CONFIG = 'C:\access-review\.docker-nocreds'
  ```

  The pipeline scripts do this.
- Open the portal port in Windows Firewall: `New-NetFirewallRule -DisplayName "Access Review portal" -Direction Inbound -Protocol TCP -LocalPort 8088 -Action Allow`.

### PostgreSQL settings

`deploy/postgres/postgresql.conf` replaces the default configuration. It is sized for a VM with 4 vCPU / 16 GB shared with the portal (shared_buffers 1 GB, larger WAL, earlier autovacuum). Change the memory values for other VM sizes. The database port is published on `127.0.0.1:5435` only, for backups and administration from the VM. Remove the `ports` section in `docker-compose.yml` to keep it fully internal.

The image tag is `postgres:${POSTGRES_TAG:-18-alpine}`. A pipeline run pulls the newest 18.x patch image and recreates the database container (the data stays on the volume). To update PostgreSQL only when you plan it, pin the version in `.env`, for example `POSTGRES_TAG=18.6-alpine`.

### External PostgreSQL server

By default the database runs in the container `db` on the same VM. To use another PostgreSQL server (version 16 or later), for example on a separate VM:

1. The database administrator creates the login and the database. The login owns the database, so the migrate step can create the tables:

   ```sql
   CREATE ROLE accessreview LOGIN PASSWORD '<password>';
   CREATE DATABASE accessreview OWNER accessreview;
   ```

2. The server accepts the portal VM over TLS, for example with this line in `pg_hba.conf`:
   `hostssl accessreview accessreview <IP of the portal VM>/32 scram-sha-256`
3. In `.env`, set `DB_CONNECTION_STRING`. `POSTGRES_PASSWORD` still needs a value, because `docker-compose.yml` reads it:

   ```
   DB_CONNECTION_STRING=Host=pg01.contoso.com;Port=5432;Database=accessreview;Username=accessreview;Password=<password>;Ssl Mode=VerifyFull
   ```

   If the server certificate comes from your own CA, copy the CA certificate (PEM) to `certs/db-ca.crt` and add `;Root Certificate=/https/db-ca.crt` to the connection string. `Ssl Mode=Require` encrypts the connection but does not check the server certificate.
4. Start without the container `db`:

   ```bash
   docker compose run --rm --no-deps migrate
   docker compose up -d --no-deps web
   ```

The pipelines with `database=AzurePostgreSQLFlexible` (section 6) also use `DB_CONNECTION_STRING` from `.env` on the VM. The firewall of the server must allow the VM.

### DNS

The portal name must resolve to the VM for the reviewers and for the pipeline health check. If your internal DNS hosts its own copy of the zone (split-horizon, for example an Active Directory integrated zone), add the record there too, not only in the public zone. The pipelines connect to the VM directly for the health check (`deploy/scripts/health-check.sh`), so they do not depend on DNS.

### Upgrade from an existing PostgreSQL 17 container

PostgreSQL 18 cannot open the data folder of version 17. Move the data with a dump:

```bash
# old stack (PostgreSQL 17) still running
docker exec <old-db-container> pg_dump -U accessreview -d accessreview -Fc -f /tmp/ar.dump
docker cp <old-db-container>:/tmp/ar.dump ./ar.dump
docker compose -p <old project> down          # keeps the old volume as a backup

# new stack (PostgreSQL 18)
docker compose up -d db
docker cp ./ar.dump access-review-db-1:/tmp/ar.dump
docker exec access-review-db-1 pg_restore -U accessreview -d accessreview --no-owner --no-privileges /tmp/ar.dump
docker compose up -d                          # migrate adds the new tables, then the portal starts
```

## 5. Alternative: Azure App Service and Azure Database for PostgreSQL

The step-by-step guide is in [docs/DEPLOY-AZURE-APP-SERVICE.md](docs/DEPLOY-AZURE-APP-SERVICE.md). It deploys the portal as a .NET 10 web app on Linux (code deployment, no container registry), Azure Database for PostgreSQL Flexible Server and Azure Key Vault with one Bicep template (`deploy/bicep/appservice.bicep`).

Notes:

- Run **one instance**. The scan runs inside the portal process, and Blazor Server needs session affinity (ARR affinity is on by default in App Service).
- The scheduler uses a PostgreSQL advisory lock, so a second instance would not start a second scheduled scan.
- Azure Container Apps works the same way with the container image: ingress target port 8080, one replica, the same settings.

## 6. CI/CD pipelines

Both pipelines have the same three stages:

| Stage | What it does |
|---|---|
| Build | .NET 10 build and unit tests, EF Core migration bundle, images `access-review-portal` and `access-review-migrator` pushed to the registry |
| Database | PostgreSQL container on the VM (`docker compose up -d db`) or Flexible Server (Bicep), then the migration bundle |
| Application | Portal container on the VM over SSH (`docker compose up -d --no-deps web`) or on App Service, then `/healthz` |

The VM keeps its `.env` and certificate. The pipeline only changes the images and the compose file. The registry credentials on the VM are short-lived: the pipeline copies a token to the VM, the VM logs in, pulls, logs out and deletes the token file.

### GitHub Actions

File: `.github/workflows/deploy-access-review.yml`. The step-by-step setup of the self-hosted runner, variables and secrets is in [docs/GITHUB-RUNNER-SETUP.md](docs/GITHUB-RUNNER-SETUP.md).

Start it by hand. In this repository the workflow has no push trigger, because a fork has no self-hosted runner. After you set up the runner, add the trigger at the top of the workflow file (see the comment there).

```bash
gh workflow run deploy-access-review.yml -f target=ReservedVM_DockerCompose -f database=ContainerOnVM
```

For `Azure_AppService` / `AzurePostgreSQLFlexible`, add the variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (federated credential for GitHub OIDC), `AZURE_RESOURCE_GROUP`, `APPSERVICE_NAME`, `PG_SERVER_NAME`, `PG_ADMIN_USER` and the secret `PG_ADMIN_PASSWORD`. With `ReservedVM_DockerCompose` and `AzurePostgreSQLFlexible`, also set `DB_CONNECTION_STRING` in `.env` on the VM ([External PostgreSQL server](#external-postgresql-server)). App Service keeps the registry credentials, so use a registry user with a long-lived pull right (variables `REGISTRY`, `REGISTRY_USERNAME`, secret `REGISTRY_PASSWORD`), or Azure Container Registry with the managed identity of the web app.

The registry is GitHub Container Registry by default. To use Azure Container Registry, set `REGISTRY`, `REGISTRY_USERNAME` and `REGISTRY_PASSWORD` (see the runner guide, "Container registry").

### Azure DevOps

File: `deploy/azdo/azure-pipelines.yml`. Stages: `Build_Image_And_Bundle` > `Deploy_And_Migrate_Database` > `Deploy_Container_Host_Or_AppService`. Parameters: `target` (`ReservedVM_DockerCompose` or `Azure_AppService`), `database` (`ContainerOnVM` or `AzurePostgreSQLFlexible`), agent pool, service connections, registry name.

Setup:

1. **Agent pool**: a self-hosted Linux agent with Docker, access to the registry and SSH access to the VM. Add the pool to the project (**Project settings > Agent pools > Add pool > Existing**).
2. **Service connections** (workload identity federation recommended):
   - Docker registry, type Azure Container Registry, with `AcrPush`. The pipeline also uses it to give the VM a short-lived pull token. If the registry uses **ABAC repository permissions** (`az acr show -n <registry> --query roleAssignmentMode` returns `AbacRepositoryPermissions`), `AcrPush` / `AcrPull` do not apply: assign **Container Registry Repository Writer** instead (optionally with a condition for the two repositories).
   - Azure Resource Manager, only for the Azure options (App Service, Flexible Server). These steps need Azure CLI on the agent.
3. **Library > Variable groups**: `access-review-deploy` with `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_PATH`, `DEPLOY_HOST_OS`, `PORTAL_URL` (for the Azure options also `AZURE_RESOURCE_GROUP`, `APPSERVICE_NAME`, `PG_SERVER_NAME`, `PG_ADMIN_USER`, `PG_ADMIN_PASSWORD` as secret).
4. **Library > Secure files**: upload the private deploy key as `access-review-deploy-key` (the public key goes to the VM, see the runner guide, step 1).
5. **Pipelines > New pipeline > Azure Repos Git > Existing Azure Pipelines YAML file** > `/deploy/azdo/azure-pipelines.yml`.
6. On the first run, permit the pipeline to use the pool, the service connections, the variable group and the secure file.

Or with Azure CLI:

```bash
az pipelines create --name deploy-access-review --repository <repo> --repository-type tfsgit --branch main \
  --yml-path deploy/azdo/azure-pipelines.yml --skip-first-run
az pipelines run --name deploy-access-review --parameters target=ReservedVM_DockerCompose database=ContainerOnVM
```

## 7. Subscription discovery and onboarding

The page **Settings > Subscriptions** lists management groups and subscriptions from Azure Resource Graph, with:

- management group path, state, number of resources, last scan;
- **Scanner access**: `Reader` and `Monitoring Reader` OK or missing, and a warning if the scanner has write rights;
- **Defender CSPM / CIEM**: state of the Defender CSPM plan (`Microsoft.Security/pricings/CloudPosture`, tier `Standard`) and of its Permissions management extension (`EntraPermissionsManagement`, in some tenants shown as `Ciem`), shown as CIEM Enabled / Disabled;
- **Monitor in Portal**: the next scan reads only the monitored subscriptions. New subscriptions are monitored automatically (`Discovery__MonitorNewSubscriptionsByDefault`).

Subscriptions that you can see but the scanner cannot are listed as **not onboarded**. The **Onboarding script generator** creates a PowerShell or Azure CLI script that assigns only `Reader` and `Monitoring Reader` to the scanner on the selected management groups or subscriptions. Run it as `Owner`, `User Access Administrator` or `Role Based Access Control Administrator`, then click **Run discovery now**.

## 8. Scheduled snapshots, quarterly campaigns and Power Automate

Page **Settings > Automation** (role `AccessReview.Admin`), defaults in `.env` / `appsettings.json` (`Automation__*`):

| Schedule | Default | What it does |
|---|---|---|
| Monthly activity snapshot | Day 1, 02:00 UTC | A scan that stores sign-in and audit evidence in PostgreSQL. Graph keeps sign-in logs for 30 days only; monthly snapshots keep the full 90-day evidence. |
| Quarterly campaign | January, April, July, October, day 1, 04:00 UTC | Snapshot, comparison with the baseline of the quarter, recommendations, Excel report in the campaign archive, notification |

If the portal was down at the planned time, the run starts when it is up again (up to 7 days later). A failed run is retried after `Automation__RetryAfterMinutes` (default 60).

### Notification to Power Automate

1. In Power Automate create an **Instant cloud flow** with the trigger **When an HTTP request is received**. Use this JSON schema:

   ```json
   {
     "type": "object",
     "properties": {
       "eventType": { "type": "string" },
       "campaignId": { "type": "integer" },
       "campaign": { "type": "string" },
       "snapshotId": { "type": "integer" },
       "baselineSnapshotId": { "type": ["integer", "null"] },
       "totalRows": { "type": "integer" },
       "removeRecommended": { "type": "integer" },
       "reviewRecommended": { "type": "integer" },
       "reportUrl": { "type": ["string", "null"] },
       "portalReviewUrl": { "type": ["string", "null"] },
       "createdAtUtc": { "type": "string" }
     }
   }
   ```

2. Add an action, for example **Post message in a chat or channel** (Teams) or **Send an email (V2)**, with the campaign name, the counts and the links `portalReviewUrl` and `reportUrl`.
3. Save the flow and copy the HTTP POST URL of the trigger into **Automation > Notification webhook URL** (or `Automation__NotificationWebhookUrl`).

The links open the portal; the reviewer signs in with Entra ID. A manager approval flow is not part of this version.

## 9. Operations

| Task | How |
|---|---|
| Backup | `docker exec access-review-db-1 pg_dump -U accessreview -d accessreview -Fc -f /tmp/ar.dump` then `docker cp access-review-db-1:/tmp/ar.dump .` Azure Database for PostgreSQL has automatic backups. |
| Restore | `pg_restore -U accessreview -d accessreview --clean --no-owner /tmp/ar.dump` |
| Update | Pipeline run, or `docker compose pull && docker compose up -d`. The `migrate` step updates the schema before the new portal starts. After a successful deployment the pipeline writes the deployed image tags to `.env` (`PORTAL_IMAGE`, `MIGRATOR_IMAGE`), so a manual `docker compose up -d` on the VM uses the same images. |
| Client secret | `setup-sp.ps1` creates a secret valid for 6 months. Before it expires run `setup-sp.ps1 -EnvFile .env` again and restart the portal (`docker compose up -d web`). |
| TLS certificate | Put the new PFX (with the private key and the intermediate certificates) at `certs/portal.pfx`, change `ASPNETCORE_Kestrel__Certificates__Default__Password` in `.env`, then `docker compose up -d --no-deps --force-recreate web`. Check the chain from a Linux machine: `curl -sS -o /dev/null https://<portal>/healthz` must not report a certificate error. |
| Logs | `docker compose logs -f web`. Scan logs are also on the snapshot (**Snapshots > Log**). |
| Database size | Shown on **Snapshots & Scan**. Delete old snapshots there; decisions of deleted snapshots are deleted too. |
| Sign-in keys | Data Protection keys are stored in PostgreSQL. Protect the database (disk encryption, restricted access); anyone with database access could create sign-in cookies. |

## 10. Troubleshooting

| Problem | Check |
|---|---|
| `AADSTS50011` redirect URI mismatch | The address in the browser (host and port) must match a redirect URI of the app. Run `setup-sp.ps1 -PortalUrl <address> -SkipSecret`. |
| "Access denied" after sign-in | The user has no app role. Assign `AccessReview.Reviewer` or `AccessReview.Admin`. |
| Device code sign-in fails | "Allow public client flows" must be **Yes**. A Conditional Access policy may block the device code flow. |
| **Apply** is disabled, or applies fewer changes than are pending | The *Your rights* column of **Changes Pending** shows the missing role for each change. Activate the role in PIM, sign out and sign in again. Changes in groups synced from on-premises AD are made in AD. |
| "Consent is needed" | Click **Grant consent**, or let an administrator grant admin consent for the delegated permissions. |
| Diagnostics: scanner has write rights | Remove `Owner` / `Contributor` / `User Access Administrator` from the scanner, or run `setup-sp.ps1` again. |
| Defender CSPM column is empty | Defender CSPM with the CIEM extension is not enabled on the subscription (see **Settings > Subscriptions**), or there are no unhealthy CIEM findings. |
| `insufficient_scope: authorization failed` on push to Azure Container Registry | The registry uses ABAC repository permissions. Assign **Container Registry Repository Writer** (push) or **Reader** (pull) to the pipeline identity; `AcrPush` / `AcrPull` are not used in this mode. |
| The portal does not start: "schema is not up to date" | Run the migration: `docker compose run --rm migrate`. |
| `Cannot load library libgssapi_krb5.so.2` | Harmless message of the PostgreSQL driver. The images contain the library. |
