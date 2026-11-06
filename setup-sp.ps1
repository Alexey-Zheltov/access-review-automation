<#
.SYNOPSIS
    Creates or updates the app registration of the Access Review Automation Portal (sample / prototype).

.DESCRIPTION
    The script is idempotent: you can run it again, it only adds what is missing.

    One app registration has two roles:

    A. Scanner (application permissions, used by the background scan) - READ-ONLY (zero-privilege):
         Microsoft Graph: User.Read.All, Directory.Read.All, RoleManagement.Read.Directory, AuditLog.Read.All, AccessReview.Read.All
         Azure RBAC:      Reader and Monitoring Reader on a management group (default: Tenant Root Group) or on subscriptions.
       By default the script REMOVES write rights that older versions gave to the scanner
       (User Access Administrator / Owner / Contributor / RBAC Administrator, Graph *.ReadWrite.* application permissions).

    B. Portal sign-in (OpenID Connect) and revoke "as the signed-in user" (delegated permissions):
         Azure Resource Manager: user_impersonation
         Microsoft Graph:        RoleAssignmentSchedule.ReadWrite.Directory, RoleEligibilitySchedule.ReadWrite.Directory,
                                 RoleManagement.ReadWrite.Directory, GroupMember.ReadWrite.All (+ openid, profile, offline_access, User.Read)
       Delegated permissions never give more rights than the user already has: a reviewer without Owner / User Access
       Administrator (Azure) or Privileged Role Administrator (Entra ID) cannot remove anything.
       Also: web redirect URIs, "Allow public client flows" (device code sign-in) and the app roles
       AccessReview.Admin and AccessReview.Reviewer.

    Run it as a Global Administrator (or Privileged Role Administrator + Application Administrator) who is also
    Owner or User Access Administrator on the target management group / subscriptions.

.PARAMETER TenantId
    Microsoft Entra tenant id.

.PARAMETER DisplayName
    Name of the app registration. Default: AccessReview-POC-Scanner.

.PARAMETER PortalUrl
    Public https address of the portal, for example https://accessreview.contoso.com:8088.
    Adds <PortalUrl>/signin-oidc and <PortalUrl>/signout-callback-oidc as web redirect URIs.

.PARAMETER ManagementGroupId
    Management group for Reader + Monitoring Reader. Default: the Tenant Root Group (its id equals the tenant id).

.PARAMETER SubscriptionId
    Assign Reader + Monitoring Reader on these subscriptions instead of a management group.

.PARAMETER AdminUpn
    Users who get the app role AccessReview.Admin (settings, discovery, automation; review and revoke within their own rights).

.PARAMETER ReviewerUpn
    Users who get the app role AccessReview.Reviewer (review; revoke within their own rights).

.PARAMETER GrantDelegatedConsent
    Grant tenant-wide admin consent for the delegated permissions. Without it, the first user who needs a permission
    is asked for consent (permissions that require admin consent then need an administrator).

.PARAMETER KeepExistingPrivileges
    Do not remove write rights of the scanner (not recommended).

.PARAMETER SecretValidityMonths
    Lifetime of the client secret. Default: 6 months.

.PARAMETER EnvFile
    .env file to create or update with AzureAd__TenantId, AzureAd__ClientId, AzureAd__ClientSecret (and Portal__PublicBaseUrl).

.PARAMETER SkipSecret
    Do not create a new client secret (for example when you only re-apply permissions).

.EXAMPLE
    .\setup-sp.ps1 -TenantId <tenant> -PortalUrl https://accessreview.contoso.com:8088 -AdminUpn admin@contoso.com -GrantDelegatedConsent -EnvFile .\.env

.EXAMPLE
    .\setup-sp.ps1 -TenantId <tenant> -SubscriptionId <sub1>,<sub2> -SkipSecret
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [string] $DisplayName = 'AccessReview-POC-Scanner',
    [string] $PortalUrl,
    [string] $ManagementGroupId,
    [string[]] $SubscriptionId,
    [string[]] $AdminUpn,
    [string[]] $ReviewerUpn,
    [switch] $GrantDelegatedConsent,
    [switch] $KeepExistingPrivileges,
    [int] $SecretValidityMonths = 6,
    [string] $EnvFile,
    [switch] $SkipSecret
)

#Requires -Modules Az.Accounts, Az.Resources

$ErrorActionPreference = 'Stop'
$graphAppId = '00000003-0000-0000-c000-000000000000'
$armAppId = '797f4846-ba00-4fd7-ba43-dac1f8f63013'

# A. Read-only application permissions of the scanner.
$graphApplicationPermissions = @('User.Read.All', 'Directory.Read.All', 'RoleManagement.Read.Directory', 'AuditLog.Read.All', 'AccessReview.Read.All')
$rbacRoles = @('Reader', 'Monitoring Reader')
$privilegedRbacRoles = @('Owner', 'Contributor', 'User Access Administrator', 'Role Based Access Control Administrator')

# B. Delegated permissions (portal sign-in and revoke as the signed-in user).
$graphDelegatedPermissions = @('openid', 'profile', 'offline_access', 'User.Read',
    'RoleAssignmentSchedule.ReadWrite.Directory', 'RoleEligibilitySchedule.ReadWrite.Directory',
    'RoleManagement.ReadWrite.Directory', 'GroupMember.ReadWrite.All')
$armDelegatedPermissions = @('user_impersonation')

# App roles. The ids are fixed so that the script can run many times.
$appRoles = @(
    @{ id = '16d397f3-f411-4368-af61-63368217c646'; value = 'AccessReview.Admin'; displayName = 'Access Review Admin'
       description = 'Portal settings, subscription discovery, automation. Review and revoke within the own rights of the user.' },
    @{ id = '1140f6bf-f26e-49ce-90a6-bf306e9d3a3f'; value = 'AccessReview.Reviewer'; displayName = 'Access Review Reviewer'
       description = 'Review access and approve removals. Revoke within the own rights of the user.' }
)

function Invoke-Graph {
    param([string] $Method, [string] $Path, $Body)
    $params = @{ Method = $Method; Uri = "https://graph.microsoft.com/v1.0$Path" }
    if ($null -ne $Body) { $params.Payload = ($Body | ConvertTo-Json -Depth 8 -Compress) }
    $response = Invoke-AzRestMethod @params
    if ($response.StatusCode -ge 400) {
        throw "Graph $Method $Path failed with HTTP $($response.StatusCode): $($response.Content)"
    }
    if ($response.Content) { return $response.Content | ConvertFrom-Json }
}

function Set-EnvValue {
    param([string] $Path, [string] $Name, [string] $Value)
    $lines = [System.Collections.Generic.List[string]]::new()
    if (Test-Path $Path) { $lines.AddRange([string[]]@(Get-Content -Path $Path)) }
    $index = $lines.FindIndex({ param($l) $l -match "^\s*$([regex]::Escape($Name))=" })
    if ($index -ge 0) { $lines[$index] = "$Name=$Value" } else { $lines.Add("$Name=$Value") }
    Set-Content -Path $Path -Value $lines -Encoding utf8
}

function Get-ServicePrincipalByAppId([string] $AppId) {
    $result = Invoke-Graph -Method GET -Path "/servicePrincipals?`$filter=appId eq '$AppId'"
    if (-not $result.value) { throw "Service principal of application $AppId was not found in the tenant." }
    return $result.value[0]
}

# 1. Sign in to the right tenant.
$context = Get-AzContext
if (-not $context -or $context.Tenant.Id -ne $TenantId) {
    Write-Host "Signing in to tenant $TenantId ..."
    Connect-AzAccount -Tenant $TenantId | Out-Null
}
Write-Host "Signed in as $((Get-AzContext).Account.Id) to tenant $TenantId."

# 2. App registration and service principal.
$app = Get-AzADApplication -DisplayName $DisplayName | Select-Object -First 1
if (-not $app) {
    Write-Host "Creating app registration '$DisplayName' ..."
    $app = New-AzADApplication -DisplayName $DisplayName -SignInAudience AzureADMyOrg
}
else {
    Write-Host "App registration '$DisplayName' exists (appId $($app.AppId))."
}

$sp = Get-AzADServicePrincipal -ApplicationId $app.AppId
if (-not $sp) {
    Write-Host 'Creating service principal ...'
    $sp = New-AzADServicePrincipal -ApplicationId $app.AppId
}

$graphSp = Get-ServicePrincipalByAppId $graphAppId
$armSp = Get-ServicePrincipalByAppId $armAppId

# 3. Sign-in settings: redirect URIs, public client flows (device code), app roles, delegated permissions.
$current = Invoke-Graph -Method GET -Path "/applications/$($app.Id)?`$select=id,web,appRoles,requiredResourceAccess,isFallbackPublicClient"

$redirectUris = [System.Collections.Generic.List[string]]::new()
if ($current.web.redirectUris) { $redirectUris.AddRange([string[]]@($current.web.redirectUris)) }
$logoutUrl = $current.web.logoutUrl
if ($PortalUrl) {
    $base = $PortalUrl.TrimEnd('/')
    if (-not $base.StartsWith('https://') -and -not $base.StartsWith('http://localhost')) {
        throw 'PortalUrl must use https (Microsoft Entra ID accepts http only for localhost).'
    }
    foreach ($uri in "$base/signin-oidc", "$base/signout-callback-oidc") {
        if (-not $redirectUris.Contains($uri)) { $redirectUris.Add($uri) }
    }
    $logoutUrl = "$base/signout-oidc"
}

$roles = [System.Collections.Generic.List[object]]::new()
# Only writable properties are sent back ("origin" is read-only).
if ($current.appRoles) {
    $current.appRoles | ForEach-Object {
        $roles.Add([pscustomobject]@{
            id = $_.id; value = $_.value; displayName = $_.displayName; description = $_.description
            allowedMemberTypes = @($_.allowedMemberTypes); isEnabled = $_.isEnabled
        })
    }
}
foreach ($r in $appRoles) {
    if (-not ($roles | Where-Object { $_.value -eq $r.value })) {
        $roles.Add([pscustomobject]@{
            id = $r.id; value = $r.value; displayName = $r.displayName; description = $r.description
            allowedMemberTypes = @('User'); isEnabled = $true
        })
    }
}

# Required resource access = what the app asks for (application + delegated). Read-only application permissions only.
function Get-Access($ResourceSp, [string[]] $ApplicationValues, [string[]] $DelegatedValues) {
    $access = @()
    foreach ($value in $ApplicationValues) {
        $role = $ResourceSp.appRoles | Where-Object { $_.value -eq $value -and $_.allowedMemberTypes -contains 'Application' }
        if (-not $role) { throw "Application permission '$value' was not found on $($ResourceSp.displayName)." }
        $access += @{ id = $role.id; type = 'Role' }
    }
    foreach ($value in $DelegatedValues) {
        $scope = $ResourceSp.oauth2PermissionScopes | Where-Object { $_.value -eq $value }
        if (-not $scope) { throw "Delegated permission '$value' was not found on $($ResourceSp.displayName)." }
        $access += @{ id = $scope.id; type = 'Scope' }
    }
    return $access
}
$requiredResourceAccess = @(
    @{ resourceAppId = $graphAppId; resourceAccess = @(Get-Access $graphSp $graphApplicationPermissions $graphDelegatedPermissions) },
    @{ resourceAppId = $armAppId; resourceAccess = @(Get-Access $armSp @() $armDelegatedPermissions) }
)
# Keep permissions for other APIs that someone added by hand.
foreach ($rra in @($current.requiredResourceAccess)) {
    if ($rra -and $rra.resourceAppId -notin @($graphAppId, $armAppId)) { $requiredResourceAccess += $rra }
}

Invoke-Graph -Method PATCH -Path "/applications/$($app.Id)" -Body @{
    web                    = @{ redirectUris = @($redirectUris); logoutUrl = $logoutUrl; implicitGrantSettings = @{ enableIdTokenIssuance = $false; enableAccessTokenIssuance = $false } }
    isFallbackPublicClient = $true
    appRoles               = @($roles)
    requiredResourceAccess = $requiredResourceAccess
} | Out-Null
Write-Host "  Redirect URIs: $($redirectUris -join ', ')"
Write-Host '  Public client flows (device code sign-in): enabled'
Write-Host "  App roles: $(($roles | ForEach-Object { $_.value }) -join ', ')"

# 4. Admin consent for the read-only application permissions. Remove write application permissions.
$granted = @((Invoke-Graph -Method GET -Path "/servicePrincipals/$($sp.Id)/appRoleAssignments").value)
foreach ($permission in $graphApplicationPermissions) {
    $role = $graphSp.appRoles | Where-Object { $_.value -eq $permission -and $_.allowedMemberTypes -contains 'Application' }
    if ($granted | Where-Object { $_.appRoleId -eq $role.id }) {
        Write-Host "  Graph application permission $permission is granted"
        continue
    }
    Invoke-Graph -Method POST -Path "/servicePrincipals/$($graphSp.Id)/appRoleAssignedTo" -Body @{
        principalId = $sp.Id; resourceId = $graphSp.Id; appRoleId = $role.id
    } | Out-Null
    Write-Host "  Granted Graph application permission $permission"
}
foreach ($assignment in $granted | Where-Object { $_.resourceId -eq $graphSp.Id }) {
    $value = ($graphSp.appRoles | Where-Object { $_.id -eq $assignment.appRoleId }).value
    if ($value -in $graphApplicationPermissions) { continue }
    if ($value -notmatch 'Write|FullControl|Manage|Send') {
        Write-Warning "The scanner has the extra read permission $value (kept)."
        continue
    }
    if ($KeepExistingPrivileges) {
        Write-Warning "The scanner has the extra application permission $value (kept because of -KeepExistingPrivileges)."
        continue
    }
    Invoke-Graph -Method DELETE -Path "/servicePrincipals/$($sp.Id)/appRoleAssignments/$($assignment.id)" | Out-Null
    Write-Host "  Removed extra Graph application permission $value (the scanner must be read-only)"
}

# 5. Admin consent for the delegated permissions (optional).
if ($GrantDelegatedConsent) {
    foreach ($item in @(@{ Sp = $graphSp; Scopes = $graphDelegatedPermissions }, @{ Sp = $armSp; Scopes = $armDelegatedPermissions })) {
        $scope = $item.Scopes -join ' '
        $existing = (Invoke-Graph -Method GET -Path "/oauth2PermissionGrants?`$filter=clientId eq '$($sp.Id)' and resourceId eq '$($item.Sp.id)' and consentType eq 'AllPrincipals'").value
        if ($existing) {
            $merged = @(($existing[0].scope -split ' ') + $item.Scopes | Where-Object { $_ } | Select-Object -Unique) -join ' '
            Invoke-Graph -Method PATCH -Path "/oauth2PermissionGrants/$($existing[0].id)" -Body @{ scope = $merged } | Out-Null
        }
        else {
            Invoke-Graph -Method POST -Path '/oauth2PermissionGrants' -Body @{
                clientId = $sp.Id; consentType = 'AllPrincipals'; resourceId = $item.Sp.id; scope = $scope
            } | Out-Null
        }
        Write-Host "  Admin consent (delegated) for $($item.Sp.displayName): $scope"
    }
}

# 6. App role assignments for users.
$assigned = @((Invoke-Graph -Method GET -Path "/servicePrincipals/$($sp.Id)/appRoleAssignedTo").value)
foreach ($pair in @(@{ Upns = $AdminUpn; Role = $appRoles[0] }, @{ Upns = $ReviewerUpn; Role = $appRoles[1] })) {
    foreach ($upn in @($pair.Upns | Where-Object { $_ })) {
        $user = Invoke-Graph -Method GET -Path "/users/$([uri]::EscapeDataString($upn))?`$select=id,userPrincipalName"
        if ($assigned | Where-Object { $_.principalId -eq $user.id -and $_.appRoleId -eq $pair.Role.id }) {
            Write-Host "  $upn already has $($pair.Role.value)"
            continue
        }
        Invoke-Graph -Method POST -Path "/servicePrincipals/$($sp.Id)/appRoleAssignedTo" -Body @{
            principalId = $user.id; resourceId = $sp.Id; appRoleId = $pair.Role.id
        } | Out-Null
        Write-Host "  Assigned $($pair.Role.value) to $upn"
    }
}

# 7. Azure RBAC: Reader + Monitoring Reader. Remove write roles of the scanner.
if (-not $ManagementGroupId) { $ManagementGroupId = $TenantId }
$scopes = if ($SubscriptionId) {
    $SubscriptionId | ForEach-Object { "/subscriptions/$_" }
}
else {
    "/providers/Microsoft.Management/managementGroups/$ManagementGroupId"
}

foreach ($scope in $scopes) {
    foreach ($roleName in $rbacRoles) {
        $existing = Get-AzRoleAssignment -ObjectId $sp.Id -RoleDefinitionName $roleName -Scope $scope -ErrorAction SilentlyContinue |
            Where-Object { $_.Scope -eq $scope }
        if ($existing) {
            Write-Host "  $roleName on $scope already assigned"
            continue
        }
        # A new service principal needs a short time to replicate before RBAC accepts it.
        for ($attempt = 1; ; $attempt++) {
            try {
                New-AzRoleAssignment -ObjectId $sp.Id -ObjectType ServicePrincipal -RoleDefinitionName $roleName -Scope $scope | Out-Null
                Write-Host "  Assigned $roleName on $scope"
                break
            }
            catch {
                if ($attempt -ge 6) { throw }
                Write-Host "  Waiting for replication ($($_.Exception.Message.Split([Environment]::NewLine)[0])) ..."
                Start-Sleep -Seconds (10 * $attempt)
            }
        }
    }

    foreach ($assignment in @(Get-AzRoleAssignment -ObjectId $sp.Id -Scope $scope -ErrorAction SilentlyContinue |
            Where-Object { $_.RoleDefinitionName -in $privilegedRbacRoles })) {
        if ($KeepExistingPrivileges) {
            Write-Warning "The scanner has $($assignment.RoleDefinitionName) on $($assignment.Scope) (kept because of -KeepExistingPrivileges)."
            continue
        }
        Remove-AzRoleAssignment -ObjectId $sp.Id -RoleDefinitionName $assignment.RoleDefinitionName -Scope $assignment.Scope | Out-Null
        Write-Host "  Removed $($assignment.RoleDefinitionName) on $($assignment.Scope) (the scanner must be read-only)"
    }
}

# 8. Client secret (used by the scanner and for the sign-in code exchange).
if (-not $SkipSecret) {
    $credential = New-AzADAppCredential -ObjectId $app.Id -EndDate (Get-Date).AddMonths($SecretValidityMonths)
    if ($EnvFile) {
        Set-EnvValue -Path $EnvFile -Name 'AzureAd__TenantId' -Value $TenantId
        Set-EnvValue -Path $EnvFile -Name 'AzureAd__ClientId' -Value $app.AppId
        Set-EnvValue -Path $EnvFile -Name 'AzureAd__ClientSecret' -Value $credential.SecretText
        Write-Host "Client secret (valid until $($credential.EndDateTime)) written to $EnvFile."
    }
    else {
        Write-Warning 'No -EnvFile given. Copy the secret now, it is shown only once:'
        Write-Host $credential.SecretText
    }
}
if ($EnvFile -and $PortalUrl) {
    Set-EnvValue -Path $EnvFile -Name 'Portal__PublicBaseUrl' -Value $PortalUrl.TrimEnd('/')
}

Write-Host ''
Write-Host 'Done.'
Write-Host "  AzureAd__TenantId = $TenantId"
Write-Host "  AzureAd__ClientId = $($app.AppId)"
Write-Host "  Service principal object id = $($sp.Id)"
Write-Host "  Scanner (application, read-only): Graph $($graphApplicationPermissions -join ', '); Azure $($rbacRoles -join ', ') on $($scopes -join ', ')"
Write-Host "  Delegated (revoke as the signed-in user): Graph $($graphDelegatedPermissions -join ', '); Azure Resource Manager user_impersonation"
if (-not $GrantDelegatedConsent) {
    Write-Host '  Delegated permissions are not consented yet. Run again with -GrantDelegatedConsent, or grant consent in the Entra admin center.'
}
