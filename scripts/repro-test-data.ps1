<#
.SYNOPSIS
    Creates test data for the "Department/Manager changed" repro of the Access Review Automation Portal (POC).

.DESCRIPTION
    Actions:
      Create  - cloud-only test users, a test security group, a test resource group and role assignments:
                  AR POC Mover   : Contributor on the test RG (permanent), Department "Finance", Manager "AR POC Manager A"
                  AR POC Reader  : Reader on the subscription (permanent), never signs in -> "No role usage"
                  AR POC Leaver  : Reader on the test RG (permanent)
                  AR POC Pim     : PIM Eligible Contributor on the test RG (180 days, with justification)
                  AR-POC-Operators group (Mover + Reader): Virtual Machine Contributor on the test RG (via group)
      Move    - simulates a mover and a leaver: Mover gets Department "IT Operations" and Manager "AR POC Manager B",
                Leaver is disabled.
      Revert  - restores Department, Manager and the Leaver account.
      Remove  - deletes the test users, the group and the test resource group (role assignments go with them).

    Run a scan in the portal after Create (baseline) and after Move (second snapshot).
    Sign in first with Connect-AzAccount -Tenant <tenant> as an administrator who can create users and assign roles.

.EXAMPLE
    .\repro-test-data.ps1 -Action Create -SubscriptionId <sub-guid>
    .\repro-test-data.ps1 -Action Move   -SubscriptionId <sub-guid>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Create', 'Move', 'Revert', 'Remove')] [string] $Action,
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [string] $ResourceGroupName = 'rg-accessreview-poc',
    [string] $Location = 'westeurope',
    [string] $Prefix = 'ar-poc'
)

#Requires -Modules Az.Accounts, Az.Resources

$ErrorActionPreference = 'Stop'

function Invoke-Graph {
    param([string] $Method, [string] $Path, $Body)
    $params = @{ Method = $Method; Uri = "https://graph.microsoft.com/v1.0$Path" }
    if ($null -ne $Body) { $params.Payload = ($Body | ConvertTo-Json -Depth 6 -Compress) }
    $r = Invoke-AzRestMethod @params
    if ($r.StatusCode -ge 400) { throw "Graph $Method $Path failed with HTTP $($r.StatusCode): $($r.Content)" }
    if ($r.Content) { return $r.Content | ConvertFrom-Json }
}

function Get-Domain {
    $org = Invoke-Graph -Method GET -Path '/organization?$select=verifiedDomains'
    ($org.value[0].verifiedDomains | Where-Object { $_.isInitial }).name
}

function Get-TestUser([string] $Name) {
    $upn = "$Prefix-$Name@$script:Domain"
    (Invoke-Graph -Method GET -Path "/users?`$filter=userPrincipalName eq '$upn'&`$select=id,displayName,userPrincipalName,department,accountEnabled").value | Select-Object -First 1
}

function New-TestUser([string] $Name, [string] $DisplayName, [string] $Department, [string] $JobTitle) {
    $existing = Get-TestUser $Name
    if ($existing) { Write-Host "  User $($existing.userPrincipalName) exists"; return $existing }
    $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + 'aA1!'
    $user = Invoke-Graph -Method POST -Path '/users' -Body @{
        accountEnabled    = $true
        displayName       = $DisplayName
        mailNickname      = "$Prefix-$Name"
        userPrincipalName = "$Prefix-$Name@$script:Domain"
        department        = $Department
        jobTitle          = $JobTitle
        passwordProfile   = @{ forceChangePasswordNextSignIn = $true; password = $password }
    }
    Write-Host "  Created user $($user.userPrincipalName)"
    return $user
}

function Set-Manager([string] $UserId, [string] $ManagerId) {
    Invoke-Graph -Method PUT -Path "/users/$UserId/manager/`$ref" -Body @{ '@odata.id' = "https://graph.microsoft.com/v1.0/users/$ManagerId" } | Out-Null
}

function Set-RoleAssignment([string] $ObjectId, [string] $Role, [string] $Scope, [string] $Type = 'User') {
    if (Get-AzRoleAssignment -ObjectId $ObjectId -RoleDefinitionName $Role -Scope $Scope -ErrorAction SilentlyContinue | Where-Object { $_.Scope -eq $Scope }) {
        Write-Host "  $Role on $Scope already assigned"
        return
    }
    for ($i = 1; ; $i++) {
        try {
            New-AzRoleAssignment -ObjectId $ObjectId -ObjectType $Type -RoleDefinitionName $Role -Scope $Scope `
                -Description 'Access Review POC test assignment' | Out-Null
            Write-Host "  Assigned $Role on $Scope"
            return
        }
        catch {
            if ($i -ge 6) { throw }
            Start-Sleep -Seconds (10 * $i)
        }
    }
}

if (-not (Get-AzContext)) { throw 'Run Connect-AzAccount -Tenant <tenant> first.' }
Set-AzContext -SubscriptionId $SubscriptionId | Out-Null
$script:Domain = Get-Domain
$rgScope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName"

switch ($Action) {
    'Create' {
        Write-Host "Creating test users in $script:Domain ..."
        $managerA = New-TestUser 'manager-a' 'AR POC Manager A' 'Finance' 'Finance Manager'
        $null = New-TestUser 'manager-b' 'AR POC Manager B' 'IT Operations' 'IT Operations Manager'
        $mover = New-TestUser 'mover' 'AR POC Mover' 'Finance' 'Cloud Engineer'
        $reader = New-TestUser 'reader' 'AR POC Reader' 'Finance' 'Analyst'
        $leaver = New-TestUser 'leaver' 'AR POC Leaver' 'Finance' 'Contractor'
        $pim = New-TestUser 'pim' 'AR POC Pim' 'Finance' 'Platform Engineer'
        foreach ($u in $mover, $reader, $leaver, $pim) { Set-Manager $u.id $managerA.id }

        Write-Host 'Creating test group ...'
        $group = (Invoke-Graph -Method GET -Path "/groups?`$filter=displayName eq 'AR-POC-Operators'").value | Select-Object -First 1
        if (-not $group) {
            $group = Invoke-Graph -Method POST -Path '/groups' -Body @{
                displayName = 'AR-POC-Operators'; mailEnabled = $false; mailNickname = "$Prefix-operators"; securityEnabled = $true
                description = 'Access Review POC test group'
            }
        }
        $members = @((Invoke-Graph -Method GET -Path "/groups/$($group.id)/members?`$select=id").value | ForEach-Object { $_.id })
        foreach ($u in $mover, $reader) {
            if ($members -notcontains $u.id) {
                Invoke-Graph -Method POST -Path "/groups/$($group.id)/members/`$ref" -Body @{ '@odata.id' = "https://graph.microsoft.com/v1.0/directoryObjects/$($u.id)" } | Out-Null
            }
        }

        Write-Host "Creating resource group $ResourceGroupName ..."
        if (-not (Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue)) {
            New-AzResourceGroup -Name $ResourceGroupName -Location $Location -Tag @{ purpose = 'access-review-poc' } | Out-Null
        }

        Write-Host 'Assigning roles ...'
        Set-RoleAssignment $mover.id 'Contributor' $rgScope
        Set-RoleAssignment $reader.id 'Reader' "/subscriptions/$SubscriptionId"
        Set-RoleAssignment $leaver.id 'Reader' $rgScope
        Set-RoleAssignment $group.id 'Virtual Machine Contributor' $rgScope 'Group'

        Write-Host 'Creating PIM eligible assignment (Contributor on the test RG) ...'
        $contributor = "/subscriptions/$SubscriptionId/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c"
        $eligible = Invoke-AzRestMethod -Method GET -Path "$rgScope/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?api-version=2020-10-01&`$filter=principalId eq '$($pim.id)'"
        if (@(($eligible.Content | ConvertFrom-Json).value | Where-Object { $_.properties.scope -eq $rgScope }).Count -eq 0) {
            $body = @{
                properties = @{
                    principalId      = $pim.id
                    roleDefinitionId = $contributor
                    requestType      = 'AdminAssign'
                    justification    = 'POC: platform on-call, approved change CHG-1042'
                    scheduleInfo     = @{ startDateTime = (Get-Date).ToUniversalTime().ToString('o'); expiration = @{ type = 'AfterDuration'; duration = 'P180D' } }
                }
            } | ConvertTo-Json -Depth 6
            for ($i = 1; ; $i++) {
                $r = Invoke-AzRestMethod -Method PUT -Path "$rgScope/providers/Microsoft.Authorization/roleEligibilityScheduleRequests/$([guid]::NewGuid())?api-version=2020-10-01" -Payload $body
                if ($r.StatusCode -lt 300) { Write-Host '  PIM eligible assignment created'; break }
                if ($i -ge 6) { throw "PIM request failed: $($r.Content)" }
                Start-Sleep -Seconds (10 * $i)
            }
        }
        else {
            Write-Host '  PIM eligible assignment exists'
        }
        Write-Host 'Done. Run the first scan in the portal (baseline).'
    }

    'Move' {
        $mover = Get-TestUser 'mover'; $managerB = Get-TestUser 'manager-b'; $leaver = Get-TestUser 'leaver'
        Invoke-Graph -Method PATCH -Path "/users/$($mover.id)" -Body @{ department = 'IT Operations' } | Out-Null
        Set-Manager $mover.id $managerB.id
        Invoke-Graph -Method PATCH -Path "/users/$($leaver.id)" -Body @{ accountEnabled = $false } | Out-Null
        Write-Host "Mover: Department 'IT Operations', Manager 'AR POC Manager B'. Leaver: account disabled."
        Write-Host 'Run the second scan in the portal.'
    }

    'Revert' {
        $mover = Get-TestUser 'mover'; $managerA = Get-TestUser 'manager-a'; $leaver = Get-TestUser 'leaver'
        Invoke-Graph -Method PATCH -Path "/users/$($mover.id)" -Body @{ department = 'Finance' } | Out-Null
        Set-Manager $mover.id $managerA.id
        Invoke-Graph -Method PATCH -Path "/users/$($leaver.id)" -Body @{ accountEnabled = $true } | Out-Null
        Write-Host 'Mover and Leaver restored.'
    }

    'Remove' {
        if (Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue) {
            Remove-AzResourceGroup -Name $ResourceGroupName -Force | Out-Null
        }
        $reader = Get-TestUser 'reader'
        if ($reader) {
            Remove-AzRoleAssignment -ObjectId $reader.id -RoleDefinitionName 'Reader' -Scope "/subscriptions/$SubscriptionId" -ErrorAction SilentlyContinue | Out-Null
        }
        $group = (Invoke-Graph -Method GET -Path "/groups?`$filter=displayName eq 'AR-POC-Operators'").value | Select-Object -First 1
        if ($group) { Invoke-Graph -Method DELETE -Path "/groups/$($group.id)" | Out-Null }
        foreach ($name in 'manager-a', 'manager-b', 'mover', 'reader', 'leaver', 'pim') {
            $u = Get-TestUser $name
            if ($u) { Invoke-Graph -Method DELETE -Path "/users/$($u.id)" | Out-Null; Write-Host "  Deleted $($u.userPrincipalName)" }
        }
        Write-Host 'Test data removed.'
    }
}
