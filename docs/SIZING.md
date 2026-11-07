# Sizing

This page helps you choose the size of the database and of the App Service plan. The numbers come from a test deployment of this repository (`deploy/bicep/appservice.bicep`) and from synthetic snapshots of 10,000, 100,000 and 500,000 rows. The synthetic rows are copies of a real scan with new ids, so their size is close to real data.

Contents

1. [What drives the size](#1-what-drives-the-size)
2. [Estimate the rows before you install](#2-estimate-the-rows-before-you-install)
3. [Database storage](#3-database-storage)
4. [Database compute](#4-database-compute)
5. [App Service plan](#5-app-service-plan)
6. [Scan duration](#6-scan-duration)
7. [Recommended sizes](#7-recommended-sizes)

---

## 1. What drives the size

**Rows per snapshot.** One scan stores one row for each:

- Azure role assignment on a management group, subscription, resource group or resource (active and PIM eligible);
- Microsoft Entra role assignment (active and PIM eligible);
- **member of a group** that has a role. A group with 40 members and one Contributor role adds 41 rows: the row of the group and one row per member.

So the size depends on the role assignments and the group members, not directly on the number of resources. A subscription with 10,000 resources and 30 role assignments adds about 30 rows.

**Snapshots you keep.** With the default schedule the portal creates 16 snapshots per year: 12 monthly snapshots and 4 snapshots of the quarterly campaigns. Each quarterly campaign also stores its Excel report in the database. Delete old snapshots on the page **Snapshots & Scan**.

**Subscriptions and their activity.** The scan reads the Azure Activity Log of every monitored subscription. The number of write operations in the last 90 days decides the scan time (section 6), not the database size.

## 2. Estimate the rows before you install

Run this in PowerShell 7 with Azure CLI, signed in with `Reader` on the management group. It counts the Azure role assignments with Azure Resource Graph and adds the members of every group that has a role.

```powershell
$scope = '<management group id>'   # the id of the Tenant Root Group is the tenant id

$total = az graph query -q "authorizationresources | where type =~ 'microsoft.authorization/roleassignments' | count" `
    --management-groups $scope --query "data[0].Count" -o tsv

$groups = @(); $token = $null
do {
    $cmd = @('graph', 'query', '-q', "authorizationresources | where type =~ 'microsoft.authorization/roleassignments' and properties.principalType =~ 'Group' | summarize assignments = count() by groupId = tostring(properties.principalId) | order by groupId asc", '--management-groups', $scope, '--first', '1000', '-o', 'json')
    if ($token) { $cmd += @('--skip-token', $token) }
    $page = az @cmd | ConvertFrom-Json
    $groups += $page.data
    $token = $page.skip_token
} while ($token)

$memberRows = 0
foreach ($g in $groups) {
    $members = az rest --method get --url "https://graph.microsoft.com/v1.0/groups/$($g.groupId)/transitiveMembers/`$count" --headers 'ConsistencyLevel=eventual' 2>$null
    if ($members) { $memberRows += [int]$members * $g.assignments }
}
"Azure role assignments: $total; groups with roles: $($groups.Count); rows for group members: $memberRows"
"Estimated rows per snapshot: $([int]$total + $memberRows), plus the Microsoft Entra role assignments"
```

Your account must be able to read groups in Microsoft Entra ID (members of the tenant can, by default). Example: in a small tenant the estimate is 190 rows plus 25 Entra and eligible assignments, and the first scan stores 218 rows.

After the first scan, the page **Snapshots & Scan** shows the real number of rows and the size of each table.

## 3. Database storage

Measured on Azure Database for PostgreSQL Flexible Server, PostgreSQL 18:

| Table | Size per row, with indexes |
|---|---|
| `RoleAssignmentSnapshots` (one row per assignment and group member) | 1.3 KB |
| `UserStateSnapshots` (one row per user in the snapshot) | 0.34 KB |
| `GroupSnapshots` (one row per group with a role) | 0.25 KB |
| Excel report of a quarterly campaign (`ReviewCampaigns.ReportContent`) | 0.14 KB per row of the snapshot |

For planning use **1.5 KB per row per snapshot**, plus the Excel reports:

| Rows per snapshot | One snapshot | One year with the default schedule (16 snapshots, 4 reports) |
|---|---|---|
| 10,000 | 15 MB | 0.3 GB |
| 50,000 | 75 MB | 1.3 GB |
| 100,000 | 150 MB | 2.5 GB |
| 250,000 | 375 MB | 6.2 GB |
| 500,000 | 750 MB | 12.3 GB |
| 1,000,000 | 1.5 GB | 25 GB |

Provision at least **twice** the size of the data you keep: PostgreSQL needs free space for updates, vacuum and the write-ahead log. Storage auto-grow is on in the template, so the server grows instead of stopping. On Flexible Server a larger disk also gives more IOPS (32 GB: 120 IOPS baseline, 128 GB: 500, 512 GB: 2,300).

## 4. Database compute

The portal reads one snapshot at a time. Most pages filter by snapshot and sort all its rows (recommendation, name, scope, role). So the time grows with the rows of one snapshot, not with the number of snapshots.

Server time of the queries that the portal runs (`EXPLAIN ANALYZE`, median of 3 runs, data in memory):

| Query | 100,000 rows, Burstable B2s | 500,000 rows, Burstable B2s | 500,000 rows, General Purpose D2ds_v5 |
|---|---|---|---|
| Count of the rows | 0.02 s | 0.09 s | 0.05 s |
| Review page, first 50 rows | 0.15 s | 1.1 s | 0.73 s |
| Review page 101 | 0.39 s | 1.5 s | 0.93 s |
| Free-text search | 0.27 s | 2.1 s | 1.3 s |
| One statistic of the snapshot page (the page runs about 10) | 0.06 s | 0.54 s | 0.31 s |
| All rows in review order (Excel export) | 1.2 s | 9.9 s | 4.4 s |
| Number of pending changes, all snapshots (partial index) | < 1 ms | < 1 ms | < 1 ms |

In the browser, the Review page of the 500,000-row snapshot showed the first page after 3.3 seconds (App Service P1v3, PostgreSQL B2s).

The numbers are for data in memory. After a restart of the server the first query reads the snapshot from disk: the first page of the 500,000-row snapshot then took 12 seconds once. More memory (General Purpose: 4 GB per vCore, Burstable B2s: 4 GB in total) keeps more snapshots in memory.

Burstable servers work with CPU credits. A long scan or many exports can use up the credits, and then the server is slower. Use General Purpose for production with more than about 100,000 rows per snapshot.

## 5. App Service plan

The portal runs as **one instance**. The size of the plan matters for two things:

- **Excel export and the quarterly campaign report.** The export builds the whole workbook in memory. Measured:

  | Rows in the export | P0v3 (4 GB) | P1v3 (8 GB) | P2v3 (16 GB) | File size |
  |---|---|---|---|---|
  | 100,000 | 23 s | | | 14 MB |
  | 500,000 | **fails: out of memory** | 130 s, about 4.4 GB used | 148 s | 70 MB |

  Plan about 9 KB of memory per exported row, plus 0.5 GB for the portal.
- **The request time limit of App Service: 230 seconds.** An interactive export of 500,000 rows took 130 to 150 seconds. Above about 700,000 rows the export can reach the limit on any plan. Then export with a filter from the Review page (for example one subscription). The report of the quarterly campaign is built in the background, so this limit does not apply to it, but the memory rule does.

The scan itself needs little: during a scan of a small tenant the portal uses about 350 MB of memory and at most 28 % CPU of a P0v3. A larger plan does not make the scan faster (section 6).

## 6. Scan duration

The scan spends most of its time waiting for Microsoft APIs. Example for a small tenant (1 subscription, 695 resources, 196 Azure role assignments, 21 Entra role assignments):

| Part of the scan | Time |
|---|---|
| Hierarchy, role assignments, Entra roles, users, groups | 35 s |
| Native access review decisions | 55 s |
| Sign-in logs (30 days) | 1 min 24 s |
| **Azure Activity Log: 188,648 events in 90 days** | **12 min 9 s** |
| Defender for Cloud CSPM, recommendations, save | 2 s |
| **Total** | **15 min** |

The Activity Log is read through the REST API, about 250 events per second. For many busy subscriptions this takes hours: 50 subscriptions with 100,000 events each need about 5 to 6 hours. Options:

- Send the Activity Log and the sign-in logs to a **Log Analytics workspace** (diagnostic settings: `AzureActivity`, `SigninLogs`, `AADNonInteractiveUserSignInLogs`) and set `ReviewSettings__LogAnalyticsWorkspaceId`. The portal then reads the evidence with queries, and gets 90 days of sign-ins instead of 30. The scanner needs read access to the workspace: the `Monitoring Reader` role from `setup-sp.ps1` covers it when the workspace is inside that scope, otherwise give it `Log Analytics Reader` on the workspace.
- Limit the events per subscription: `ReviewSettings__ActivityLogMaxEventsPerSubscription` (default 300,000).
- Monitor only the subscriptions you review: **Settings > Subscriptions > Monitor in Portal**.

The monthly and quarterly scans run in the background. Do not deploy a new version during a scan: a restart stops the scan, and it is marked as failed. By default the scans start on day 1 of the month at 02:00 and 04:00 UTC.

## 7. Recommended sizes

| Rows per snapshot | Example | App Service plan | PostgreSQL compute | PostgreSQL storage |
|---|---|---|---|---|
| up to 20,000 | up to about 50 subscriptions | P0v3 | Burstable B2s | 32 GB |
| 20,000 to 100,000 | 50 to 300 subscriptions | P0v3 | Burstable B2ms, or General Purpose D2ds_v5 | 32 GB |
| 100,000 to 250,000 | 300 to 800 subscriptions | P1v3 | General Purpose D2ds_v5 | 64 GB |
| 250,000 to 500,000 | 800 to 1,500 subscriptions | P1v3 | General Purpose D4ds_v5 | 64 GB |
| more than 500,000 | more than 1,500 subscriptions | P2v3 | General Purpose D4ds_v5 or larger | 128 GB |

The "Example" column assumes about 300 rows per subscription (role assignments on the subscription, its resource groups and resources, and the members of assigned groups). Your number can be very different: use the script in section 2.

Approximate list prices per month (West Europe, pay-as-you-go): App Service P0v3 USD 65, P1v3 130, P2v3 260; PostgreSQL B2s USD 58, B2ms 116, D2ds_v5 155, D4ds_v5 310; storage USD 0.14 per GB.

You can change both sizes later without a new deployment:

```powershell
az appservice plan update --resource-group $resourceGroup --name "asp-$appName" --sku P1V3
az postgres flexible-server update --resource-group $resourceGroup --name $postgresName --tier GeneralPurpose --sku-name Standard_D2ds_v5 --yes
```

The PostgreSQL change restarts the server. This can take about 8 minutes. Do it outside the scan schedule. The plan change can fail with "The pricing tier is not allowed in this resource group" for some tiers (for example memory-optimized P1mv3): the plan cannot move to that tier on its current hardware. Choose another tier, or create a new plan.
