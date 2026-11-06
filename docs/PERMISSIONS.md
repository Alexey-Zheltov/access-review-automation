# Permissions for the person who sets up the portal

The setup touches three places:

- **Microsoft Entra ID**: the app registration, the API permissions of the scanner, the portal roles.
- **Azure**: the management group that the scanner reads, and the resource group of the portal.
- **GitHub or Azure DevOps**, only if you deploy with a pipeline.

Most setup problems are not caused by a missing role. They are caused by a role **in the wrong place**: Owner on one resource group does not reach a virtual network in another resource group, a DNS zone in another subscription, or the management group. Check every place before you start (section 2).

Contents

1. [Rights for each step](#1-rights-for-each-step)
2. [Check your access before you start](#2-check-your-access-before-you-start)
3. [Privileged Identity Management (PIM) and role assignment conditions](#3-privileged-identity-management-pim-and-role-assignment-conditions)
4. [Private network: rights outside the resource group](#4-private-network-rights-outside-the-resource-group)
5. [Azure Policy](#5-azure-policy)
6. [Key Vault](#6-key-vault)
7. [Two people: Entra ID administrator and Azure administrator](#7-two-people-entra-id-administrator-and-azure-administrator)
8. [Pipelines (optional)](#8-pipelines-optional)
9. [Errors and what they mean](#9-errors-and-what-they-mean)

---

## 1. Rights for each step

### Microsoft Entra ID

| Step | Minimum role | Notes |
|---|---|---|
| Create the app registration | **Application Developer**, or none if "Users can register applications" is **Yes** in your tenant | The creator becomes owner of the app registration. |
| Admin consent for the **application** permissions of the scanner (Microsoft Graph `User.Read.All`, `Directory.Read.All`, `RoleManagement.Read.Directory`, `AuditLog.Read.All`, `AccessReview.Read.All`) | **Privileged Role Administrator** (or Global Administrator) | Cloud Application Administrator and Application Administrator **cannot** consent to Microsoft Graph application permissions. This is the most common blocker. |
| Admin consent for the **delegated** permissions (sign-in, revoke as the signed-in user) | Cloud Application Administrator or Application Administrator (or Privileged Role Administrator) | Optional: without it, each user is asked for consent when the permission is first needed. |
| Assign the portal roles `AccessReview.Admin` and `AccessReview.Reviewer` | Owner of the enterprise application, Cloud Application Administrator, Application Administrator or User Administrator | Assignment to groups needs Microsoft Entra ID P1 or P2. Nested groups do not work. |

`setup-sp.ps1` does all four steps in one run. Run it as a user with **Privileged Role Administrator** who can also create app registrations, or split the work between two people (section 7).

### Azure

| Step | Where | Minimum role |
|---|---|---|
| `Reader` and `Monitoring Reader` for the scanner | Management group that the portal reads (default: Tenant Root Group), or the subscriptions | **Owner**, **User Access Administrator** or **Role Based Access Control Administrator** on that management group or subscription. A condition on the role must allow `Reader` and `Monitoring Reader` (section 3). |
| Register resource providers (`Microsoft.Web`, `Microsoft.DBforPostgreSQL`, `Microsoft.KeyVault`, `Microsoft.Network`, `Microsoft.ManagedIdentity`) | Subscription | **Contributor** or **Owner** on the subscription. Only once per subscription. A role on a resource group is not enough. |
| Create the resource group | Subscription | **Contributor** on the subscription, or ask for an empty resource group. |
| Deploy `deploy/bicep/appservice.bicep`, quick start | Resource group | **Contributor** on the resource group. |
| Deploy `deploy/bicep/appservice.bicep`, private network | Resource group, plus the network if it is not in this resource group | **Contributor** and **Role Based Access Control Administrator** (or Owner) on the resource group: the template gives the role `Key Vault Secrets User` to the identity of the web app. With an existing network or existing DNS zones, also the rights in section 4. |
| Deploy the code (`az webapp deploy`) | Web app | **Website Contributor** on the web app, or Contributor on the resource group. |
| Read the scan data in PostgreSQL | Database | The administrator login from the deployment. No Azure role is needed. |

### Reviewers

Reviewers do not set up anything. Their rights decide which approved changes they can **apply** in the portal. The table is in [INSTALL-GUIDE.md, section 2](../INSTALL-GUIDE.md#2-network-and-api-access) ("Rights a reviewer needs to apply changes").

## 2. Check your access before you start

Run these checks in a PowerShell 7 window **after** you activate your PIM roles (section 3). Replace the values in angle brackets.

### Sign in

```powershell
$tenantId = '<tenant-id>'
$subscriptionId = '<subscription-id>'
az login --tenant $tenantId
az account set --subscription $subscriptionId
$me = az ad signed-in-user show --query id -o tsv
```

### Your active Microsoft Entra roles

```powershell
az rest --method get --url 'https://graph.microsoft.com/v1.0/me/memberOf/microsoft.graph.directoryRole?$select=displayName' --query 'value[].displayName' -o tsv
```

The list shows **active** roles only. An eligible PIM role is not in the list until you activate it. To see eligible roles, open the Microsoft Entra admin center: **ID Governance > Privileged Identity Management > My roles > Microsoft Entra roles > Eligible assignments**.

For `setup-sp.ps1` you need `Privileged Role Administrator` (or `Global Administrator`) in the list. If "Users can register applications" is **No** in your tenant, you also need `Application Developer`, `Application Administrator` or `Cloud Application Administrator`.

### Your Azure roles, with conditions

```powershell
az role assignment list --assignee $me --all --include-inherited --include-groups `
    --query "[].{role:roleDefinitionName, scope:scope, condition:condition}" -o table
```

Check three things:

1. **Scope.** You need a role on the management group of the scanner (`/providers/Microsoft.Management/managementGroups/<id>`) and on the subscription or resource group of the portal. For the private network option, also where the virtual network and the private DNS zones are.
2. **Condition.** If the `condition` column is not empty, read it. It can block the roles that the setup assigns (section 3).
3. **Eligible roles.** The command shows active roles only. In the Azure portal, open the scope, then **Access control (IAM) > Check access > View my access**. The tab **Eligible assignments** shows PIM roles that you must activate first.

### Resource providers

```powershell
foreach ($p in 'Microsoft.Web', 'Microsoft.DBforPostgreSQL', 'Microsoft.KeyVault', 'Microsoft.Network', 'Microsoft.ManagedIdentity') {
    '{0,-28} {1}' -f $p, (az provider show --namespace $p --query registrationState -o tsv)
}
```

All must be `Registered`. If one is not, someone with Contributor on the subscription runs:

```powershell
az provider register --namespace Microsoft.DBforPostgreSQL --wait
```

### Policies on the subscription

```powershell
az policy assignment list --scope "/subscriptions/$subscriptionId" --disable-scope-strict-match `
    --query "[].{name:displayName, enforcement:enforcementMode, scope:scope}" -o table
```

Look for policies about public network access, private endpoints, allowed regions, allowed SKUs and required tags. Section 5 explains the effects.

### Region and size of PostgreSQL

```powershell
az postgres flexible-server list-skus --location <region> --query "[0].supportedServerEditions[].supportedServerSkus[].name" -o tsv
```

A size in this list can still be blocked for your subscription type. If the deployment fails with `SkuNotAvailable`, use another region or another size (`postgresSkuName`, `postgresSkuTier`).

## 3. Privileged Identity Management (PIM) and role assignment conditions

### PIM

- An **eligible** role gives no rights until you **activate** it. Activate every role you need before you start: the Entra role (for example Privileged Role Administrator) and the Azure roles on each scope.
- Choose an activation time that covers the whole session, for example 8 hours.
- The role may need **approval**. Ask for it before the planned date.
- After the activation, **sign out and sign in again** in every tool: `az logout` then `az login`, `Disconnect-AzAccount` then `Connect-AzAccount`, and a new browser session. A token issued before the activation does not contain the new role.
- Azure can take **up to 10 minutes** to apply a new role assignment.
- Check both lists in PIM: **Eligible assignments** and the role settings. A role that is eligible but also excluded or limited by a policy of your organization does not help.

### Conditions on role assignments

Owner, User Access Administrator and Role Based Access Control Administrator can have a **condition** that limits the roles the user can assign. In the Azure portal, open **Access control (IAM) > Role assignments** and look at the **Condition** column. Two templates use a list of roles in opposite ways:

| Template | Meaning |
|---|---|
| **Constrain roles** | The user can assign **only** the listed roles. |
| **Allow all except specific roles** | The user can assign all roles **except** the listed roles. |

The pane of both templates has the title "Constrain roles". Read the template name on the **Conditions** tab.

The setup assigns these roles. The condition must allow them:

| Role | Scope | When |
|---|---|---|
| `Reader` | Management group of the scanner, or subscriptions | Always (`setup-sp.ps1`) |
| `Monitoring Reader` | Same | Always (`setup-sp.ps1`) |
| `Key Vault Secrets User` | Key Vault of the portal | Private network option (Bicep template) |

If a condition blocks the assignment, Azure shows the same message as for a missing role:

```
... does not have authorization or an ABAC condition not fulfilled to perform action
'Microsoft.Authorization/roleAssignments/write' over scope '...'
```

So check the scope and the condition of your role first, then ask for a change.

## 4. Private network: rights outside the resource group

With `privateNetwork=true` and the default values, the template creates the virtual network and the private DNS zones **in the resource group of the portal**. Contributor and Role Based Access Control Administrator on this resource group are enough.

In many organizations the network is managed centrally: the virtual network is in another resource group, and the private DNS zones are in a hub subscription. Then you give existing subnets and zones to the template (`appSubnetResourceId`, `postgresSubnetResourceId`, `privateEndpointSubnetResourceId`, `postgresPrivateDnsZoneResourceId`, `keyVaultPrivateDnsZoneResourceId`), and you need rights **there** too:

| What the deployment does | Action | Role on that resource |
|---|---|---|
| Puts the web app, PostgreSQL and the private endpoint into existing subnets | `Microsoft.Network/virtualNetworks/subnets/join/action` | **Network Contributor** on the virtual network (or a custom role with this action) |
| Links a new private DNS zone to the existing virtual network | `Microsoft.Network/virtualNetworks/join/action` | **Network Contributor** on the virtual network |
| Adds the DNS record of the Key Vault private endpoint to an existing zone | `Microsoft.Network/privateDnsZones/join/action` | **Private DNS Zone Contributor** on the zone `privatelink.vaultcore.azure.net` |
| PostgreSQL adds its DNS record to an existing zone | write on the zone | **Private DNS Zone Contributor** on the PostgreSQL zone |

Owner on the subscription of the portal does not give these rights when the network or the zones are in another subscription. The error is `LinkedAuthorizationFailed` (section 9). In this case the private endpoint itself is created, and only its DNS record fails.

Before the session, agree with the network team:

- the three subnets (sizes: app `/27` or larger, delegated to `Microsoft.Web/serverFarms`; PostgreSQL `/28` or larger, delegated to `Microsoft.DBforPostgreSQL/flexibleServers`; private endpoints `/28` or larger);
- who adds the DNS records if you get no rights on the hub zones;
- outbound internet access from the app subnet (firewall rules for `login.microsoftonline.com`, `graph.microsoft.com`, `management.azure.com`, `api.loganalytics.io`). With the private network option, all outbound traffic of the web app goes through the virtual network.

## 5. Azure Policy

Policies can stop a deployment, or change resources after a successful deployment.

| Effect | What you see | What to do |
|---|---|---|
| **Deny** | The deployment fails with `RequestDisallowedByPolicy`. The message names the policy. | Change the parameters (for example `privateNetwork=true`, another region or size), or ask for an exemption. |
| **Modify** | The deployment **succeeds**, but a property is changed afterwards. Example: a policy sets **public network access = Disabled** on every Key Vault. The web app then cannot read its secrets: it does not start, `/healthz` returns 503, and the Key Vault reference status is `AccessToKeyVaultDenied` ([DEPLOY-AZURE-APP-SERVICE.md, troubleshooting](DEPLOY-AZURE-APP-SERVICE.md#troubleshooting)). | Use `privateNetwork=true` (Key Vault with a private endpoint), or ask for an exemption. The quick start does not use Key Vault for this reason. |
| **DeployIfNotExists** | Extra resources appear (diagnostic settings, Defender plans, private DNS records). | Usually nothing. Keep them in mind when you delete the resource group. |
| **Audit** | Non-compliant resources in the compliance report. | Nothing for the deployment. |

`az deployment group what-if` shows the planned changes. It does not show the change of a Modify policy: for the Key Vault example above, what-if shows public network access **Enabled** from the template.

## 6. Key Vault

The private network option keeps the client secret and the database connection string in Key Vault.

- **Deployment.** The Bicep template writes the secrets through Azure Resource Manager. Contributor on the resource group is enough. You need no Key Vault data role and no network access to the vault for the deployment.
- **The web app** reads the secrets with its user-assigned managed identity and the role `Key Vault Secrets User`. The template assigns this role before it creates the web app.
- **Read or change a secret yourself later** (for example a new client secret): Owner and Contributor do **not** give access to secret values. You need **Key Vault Secrets Officer** on the vault, and a network path to the private endpoint (a machine in the virtual network or a connected network). Or run the Bicep template again with the new value.

## 7. Two people: Entra ID administrator and Azure administrator

Often one person cannot have both the Entra role and the Azure roles. Split the work:

**Entra ID administrator** (Privileged Role Administrator), only on the subscription of the portal:

```powershell
.\setup-sp.ps1 -TenantId $tenantId -DisplayName 'AccessReview-Portal' `
    -PortalUrl "https://<app-name>.azurewebsites.net" -AdminUpn alice@contoso.com `
    -GrantDelegatedConsent -SubscriptionId $subscriptionId -EnvFile $envFile
```

`-SubscriptionId` limits the Azure part of the script to one subscription, where this person can have User Access Administrator for the session. The script prints the **service principal object id**. Give it to the Azure administrator, and give the client id and the client secret to the person who deploys the template (through a secure channel).

**Azure administrator** (Owner, User Access Administrator or Role Based Access Control Administrator on the management group), for the scanner on the whole tenant:

```powershell
$spObjectId = '<service principal object id>'
$scope = '/providers/Microsoft.Management/managementGroups/<management-group-id>'
az role assignment create --assignee-object-id $spObjectId --assignee-principal-type ServicePrincipal --role 'Reader' --scope $scope
az role assignment create --assignee-object-id $spObjectId --assignee-principal-type ServicePrincipal --role 'Monitoring Reader' --scope $scope
```

You can also onboard subscriptions later from the portal: **Settings > Subscriptions > Onboarding script generator** creates the same commands for the selected management groups or subscriptions.

### Tenant Root Group

The id of the Tenant Root Group is the tenant id. Often nobody has a role there. A Global Administrator can give himself **User Access Administrator** on the root scope for a short time: Microsoft Entra admin center > **Properties** > **Access management for Azure resources** = **Yes**. Remove this right after the session. Microsoft Entra ID logs both changes. You can also use a lower management group, or only the subscriptions that you want to review.

## 8. Pipelines (optional)

### GitHub Actions

| Task | Right |
|---|---|
| Add secrets, variables, environments, self-hosted runners | **Admin** on the repository (or the organization owner) |
| Federated credential for Azure (OIDC) on an app registration | Owner of that app registration, or Application Administrator |
| Deploy to App Service from the workflow | The workflow identity needs **Website Contributor** on the web app. For the Bicep deployment, the same rights as a person (section 1). |

### Azure DevOps

| Task | Right |
|---|---|
| Create the pipeline from the YAML file | **Contributors** group of the project, and **Edit build pipeline** |
| See runs and logs | **View builds** on the pipeline (without it, Runs is empty or shows "Build not found", `TF215002`) |
| Create a service connection | **Creator** or **Administrator** on service connections (project level) |
| Use a service connection, an agent pool, a variable group, a secure file in a pipeline | **User** on each of them. At the first run, a user with **Administrator** on the resource can **Permit** the pipeline. |
| Push to a protected branch | Check the branch policies: a **commit author email** policy rejects pushes with another email (`VS403702`); a **locked** branch rejects all pushes (`TF401389`) |
| Self-hosted agent | Enough free disk space on the agent (`df -h`, `docker system df`). "No space left on device" at *Initialize job* means the agent disk is full. |

## 9. Errors and what they mean

| Error | Meaning | What to do |
|---|---|---|
| `AuthorizationFailed` ... `does not have authorization to perform action '<action>' over scope '<scope>'` | No role with this action at this scope, or the PIM role is not active, or the token is older than the activation. | Check the scope in the message. Activate the role, sign out and sign in. Wait up to 10 minutes after a new assignment. |
| `... does not have authorization or an ABAC condition not fulfilled to perform action 'Microsoft.Authorization/roleAssignments/write'` | No role at this scope, **or** a condition on your role blocks the role you try to assign. | Check your role at this scope and its condition (section 3). |
| `LinkedAuthorizationFailed` ... `has permission to perform action '...privateDnsZoneGroups/write' on scope '...'; however, it does not have permission to perform action(s) 'Microsoft.Network/privateDnsZones/join/action' on the linked scope(s) '...'` | You have rights on the resource, but not on a resource it uses in another place (DNS zone, virtual network, subnet). | Get the role in section 4 on the linked scope, or ask the network team to add the DNS record. |
| `RequestDisallowedByPolicy` | A Deny policy blocks the resource or a property. | Read the policy name in the message. Change the parameters or ask for an exemption (section 5). |
| Deployment `Succeeded`, but the web app returns 503 and the Key Vault reference status is `AccessToKeyVaultDenied` | The web app cannot read Key Vault: a Modify policy disabled public access, or the identity has no role, or no network path. | Section 5 and 6. Use the private network option. |
| `MissingSubscriptionRegistration`: `The subscription is not registered to use namespace '...'` | The resource provider is not registered. | Section 2, "Resource providers". Needs Contributor on the subscription. |
| `SkuNotAvailable`: `Specified SKU is not supported in this region` | This size of PostgreSQL is not available for your subscription in this region. | Another region or another size. |
| `PrincipalNotFound`: `Principal ... does not exist in the directory` | A new identity is not yet replicated. | Run the deployment again after a few minutes. The template sets `principalType`, which avoids most of these errors. |
| `RoleAssignmentExists` | The same role for the same identity on the same scope already exists with another name. | Nothing to do, or delete the old assignment if it was created by hand. |
| `setup-sp.ps1`: `Graph POST /servicePrincipals/.../appRoleAssignedTo failed with HTTP 403` ... `Authorization_RequestDenied` | You cannot consent to Microsoft Graph application permissions. | Privileged Role Administrator (section 1). |
| Sign-in: "Need admin approval" or `AADSTS65001` | Delegated permissions are not consented. | Run `setup-sp.ps1 -GrantDelegatedConsent -SkipSecret`, or **Grant admin consent** in the Entra admin center. |
| Sign-in: `AADSTS50011` (redirect URI mismatch) | The portal address is not registered in the app registration. | `setup-sp.ps1 -PortalUrl https://<address> -SkipSecret`. |
| Portal: "Access denied" after sign-in | The user has no portal role. | Assign `AccessReview.Reviewer` or `AccessReview.Admin` (section 1). |
