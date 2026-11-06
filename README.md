# Access Review Automation Portal

A prototype that builds one quarterly review list for **all** role assignments:
Microsoft Entra ID roles and Azure RBAC on management group, subscription, resource group and resource level.
For every assignment it checks whether the role was really used in the last 90 days, compares the user with the
baseline snapshot (department, manager, account status), adds the findings of Microsoft Defender for Cloud CSPM (CIEM),
reads native Access Review decisions and suggests **No Action**, **Review** or **Remove**. Reviewers decide in the
portal (**Keep** / **Approve Removal**), export the review workbook (Excel), and revoke approved removals
**with their own Entra ID token** - the scanner identity itself is read-only.

Stack: .NET 10 (ASP.NET Core Blazor, Interactive Server), PostgreSQL 18, Docker Compose.
The portal works only with live data from Microsoft Graph, Azure Resource Manager and Azure Resource Graph. There is no mock data.

| Guide | Content |
|---|---|
| [docs/DEPLOY-AZURE-APP-SERVICE.md](docs/DEPLOY-AZURE-APP-SERVICE.md) | Step by step: clone this repository and deploy it to Azure App Service with Azure Database for PostgreSQL, by hand or with GitHub Actions |
| [docs/PERMISSIONS.md](docs/PERMISSIONS.md) | Rights of the person who sets up the portal, checks before you start, common errors |
| [docs/SIZING.md](docs/SIZING.md) | Database and App Service size by number of subscriptions, role assignments and snapshots |
| [INSTALL-GUIDE.md](INSTALL-GUIDE.md) | Docker on a VM, app registration details, pipelines, operations, troubleshooting |
| [docs/GITHUB-RUNNER-SETUP.md](docs/GITHUB-RUNNER-SETUP.md) | Self-hosted GitHub runner for the VM pipeline and the App Service workflow |

---

## 1. Components

```
 Browser ──HTTPS──> web (ASP.NET Core / Blazor, Entra ID sign-in)
                     │   scan (read-only app identity)  ──> Microsoft Graph, Azure Resource Manager,
                     │                                       Azure Resource Graph (Defender CSPM / CIEM), Log Analytics (optional)
                     │   revoke (signed-in user's token) ──> Azure Resource Manager, Microsoft Graph
                     └──> db (PostgreSQL 18): snapshots, decisions, groups, campaigns, sign-in keys, token cache
 migrate (EF Core migration bundle) ──> db        runs before the portal starts; idempotent
```

| Folder / file | Purpose |
|---|---|
| `src/AccessReviewPortal/Services/Scanning` | Scan pipeline, ARM and Entra collectors, group expansion, Access Review decisions, `DefenderCspmCollector` |
| `src/AccessReviewPortal/Services/Usage` | `RoleUsageAnalyzerService` and the readers for Activity Log, sign-ins, directory audit, Log Analytics |
| `src/AccessReviewPortal/Services/Review` | `RecommendationEngine`, filters, Excel export, `UserCapabilityChecker`, `RemediationService`, `GroupPermissionsService` |
| `src/AccessReviewPortal/Services/Identity` | Entra ID sign-in (OpenID Connect and device code), delegated tokens, incremental consent, PostgreSQL token cache |
| `src/AccessReviewPortal/Services/Discovery` | Subscription and management group discovery, scanner access checks, onboarding script |
| `src/AccessReviewPortal/Services/Automation` | Monthly snapshots and quarterly campaigns (`ScheduledReviewWorker`) |
| `src/AccessReviewPortal/Services/Reporting` | KPI dashboard and period report: `KpiReportService` (queries), `KpiCalculator` (rules), `KpiReportExcel` |
| `src/AccessReviewPortal/Components/Dashboard` | SVG panels of the KPI dashboard (stat, gauge, chart, bar gauge, donut) and `ChartMath` |
| `src/AccessReviewPortal/Data` | EF Core 10 model and migrations |
| `tests/AccessReviewPortal.Tests` | Unit tests (xUnit) |
| `setup-sp.ps1` | App registration: read-only scanner, sign-in, delegated permissions, app roles |
| `deploy/` | PostgreSQL configuration, Bicep for Azure Database for PostgreSQL, Azure DevOps pipeline, deploy script, runner image |
| `.github/workflows/deploy-access-review.yml` | GitHub Actions pipeline |
| `scripts/repro-test-data.ps1` | Test users and assignments for the movers / leavers repro scenario |

## 2. What the scan collects

1. **Hierarchy (ARM):** management groups, subscriptions, resource groups, resources.
2. **Azure RBAC on all four levels:** active role assignments, PIM active (time-bound) and PIM eligible
   assignments with end date and justification. Roles are resolved to names and permissions.
3. **Entra ID roles:** PIM active and eligible schedule instances (fallback to classic role assignments
   when PIM is not licensed).
4. **Group expansion:** when a role is assigned to a group, every transitive member gets its own row
   (`Assigned via Group: <name>`). The row of the group itself is kept as well.
5. **User profiles:** department, job title, manager, account status, `signInActivity`, on-premises sync flag.
6. **Native Access Reviews:** decisions of Entra ID Access Reviews (Graph) and PIM access reviews for Azure
   resources (ARM). Only reviews of roles and groups that appear in the scan are read.

Every scan is stored as a **snapshot** in PostgreSQL (`ScanSnapshots`, `UserStateSnapshots`,
`RoleAssignmentSnapshots`, `ScanLogEntries`).

## 3. How role usage is determined

| Source | Used for | How |
|---|---|---|
| A. PIM activations | PIM Eligible roles (Azure and Entra) | ARM `roleAssignmentScheduleRequests` (SelfActivate), active activations, Entra PIM events in the directory audit log |
| B. Write / action operations | Azure roles; Entra roles with write permissions | Azure Activity Log; Entra directory audit (admin changes made by the user) |
| C. Sign-ins | Permanent / time-bound active roles | Sign-ins to Azure Portal and Azure Resource Manager (CLI, PowerShell); admin portals for Entra roles |

Status per assignment (`UsageEvidenceStatus`):

* `ConfirmedUsed` – a PIM activation or a write/action operation inside the threshold window.
* `PortalSignInOnlyNoWrite` – no write operation, but the user signed in to Azure / admin portals.
  For eligible roles a sign-in without activation does not count.
* `Unused90Days` – nothing found inside the window.

### Important limits found during the POC

* **The Activity Log REST API does not return `authorization.evidence`.** This field (which role assignment
  allowed the operation) exists only in exported logs, for example in a Log Analytics workspace
  (table `AzureActivity`). Without a workspace the portal *infers* the assignment: the caller's assignment
  with the narrowest scope that contains the operation scope and whose role allows the action.
  When several roles on the same scope allow the action, all of them are marked as used.
* **Graph keeps sign-ins and directory audit for 30 days only.** The portal keeps the last seen values in
  every snapshot and carries them forward, so monthly scans build up a 90-day history. For a complete 90-day
  view configure `ReviewSettings__LogAnalyticsWorkspaceId` with a workspace that receives `SigninLogs`,
  `AADNonInteractiveUserSignInLogs` and `AzureActivity`.
* **Read operations are not logged** in the Activity Log. Read-only roles can only be confirmed by sign-ins;
  service principals with read-only roles get **Review**, not Remove.
* **Listing Entra PIM requests needs `RoleAssignmentSchedule.ReadWrite.Directory`** (Graph requirement,
  also for reading). The read-only scanner takes Entra activations and justifications from the directory
  audit log instead (30 days).
* **The Graph sign-in API is slow and throttled.** The scanner reads it in one-day slices in parallel and
  stops after `ReviewSettings__SignInTimeBudgetMinutes`. Busy subscriptions produce many Activity Log
  events (172,000 events in 90 days in the test subscription, about 6 minutes; the full scan of the test tenant takes
  about 10 minutes). Log Analytics removes both limits.

## 4. Recommendation rules (`RecommendationEngine`)

Evaluated in this order against the selected baseline snapshot:

1. Account disabled or user deleted (or principal not found) → **Remove** – `Left Organization / Account Disabled`
2. Department or manager changed since the baseline → **Remove** – `Department/Manager Changed (<old> -> <new>)`
3. `Deny` in a native Access Review → **Remove** – `Denied in Entra Self-Review`
4. `Unused90Days` → **Remove** – `No role usage > N days` (service principals: **Review**)
5. Used, but Defender for Cloud CSPM reports *Overprovisioned* or *Permanent Privileged* → **Review** –
   `Defender CSPM: Overprovisioned / Permanent Privileged (Right-size or move to PIM)`
6. `PortalSignInOnlyNoWrite` and the role can write → **Review** – `No Write actions in Nd (Consider Reader or PIM Eligible)`
7. Otherwise → **No Action**, the original justification is kept.

The threshold (default 90 days) and the baseline can be changed in the UI; **Recalculate** applies them to an
existing snapshot without a new scan.

### Defender for Cloud CSPM (CIEM)

During the scan `DefenderCspmCollector` reads the unhealthy CIEM assessments from Azure Resource Graph
(`securityresources`, type `microsoft.security/assessments`) for the monitored subscriptions:

| Assessment | Status in the portal |
|---|---|
| Azure overprovisioned identities should have only the necessary permissions | `Overprovisioned` (with the number of unused permissions and examples) |
| Permissions of inactive identities in your Azure subscription should be revoked | `Inactive45d` |
| Privileged roles should not have permanent access at the subscription and resource group level | `PermanentPrivileged` (matched to the exact role and scope) |
| Guest / blocked / deprecated accounts with owner, write or read permissions should be removed | `BlockedOrGuest` |

A row can have several findings (`DefenderCspmFindings`); the badge shows the most important one.
The column and the filter **Defender CSPM (CIEM)** are on the Review page and in the Excel sheets *Access Review* and *Details*.

## 5. Identities and permissions

**Scanner (application permissions, read-only):** Graph `User.Read.All`, `Directory.Read.All`,
`RoleManagement.Read.Directory`, `AuditLog.Read.All`, `AccessReview.Read.All`; Azure `Reader` and `Monitoring Reader`.
The scanner has no write rights. **Settings > Diagnostics** checks this ("Zero-privilege scanner").

**Signed-in reviewer (delegated permissions):** Azure Resource Manager `user_impersonation`; Graph
`RoleAssignmentSchedule.ReadWrite.Directory`, `RoleEligibilitySchedule.ReadWrite.Directory`,
`RoleManagement.ReadWrite.Directory`, `GroupMember.ReadWrite.All`. The portal asks for each permission when it is needed
(incremental consent). A delegated permission never gives more rights than the user has.

**Portal roles:** app roles `AccessReview.Admin` (settings, discovery, automation) and `AccessReview.Reviewer`.

```powershell
Connect-AzAccount -Tenant <tenant-id>
.\setup-sp.ps1 -TenantId <tenant-id> -PortalUrl https://accessreview.contoso.com:8088 -AdminUpn alice@contoso.com -GrantDelegatedConsent -EnvFile .\.env
```

The script also **removes** write rights that an older setup gave to the scanner (for example `User Access Administrator`).
Details: [INSTALL-GUIDE.md, section 3](INSTALL-GUIDE.md#3-app-registration-in-microsoft-entra-id).

## 6. Run with Docker

```bash
cp .env.example .env        # POSTGRES_PASSWORD, AzureAd__*, certificate password (setup-sp.ps1 -EnvFile .env writes AzureAd__*)
cp <your certificate>.pfx certs/portal.pfx
docker compose up -d --build
```

* `db` (PostgreSQL 18, `deploy/postgres/postgresql.conf`) → `migrate` (EF Core migration bundle, exits when the schema is up to date) → `web`.
* The portal listens on HTTPS 8443 in the container, published as `WEB_PORT` (default 8088). Entra ID accepts `http`
  redirect URIs only for `localhost`, so the portal needs a certificate for its DNS name.
* Data Protection keys and the MSAL token cache are stored in PostgreSQL: sign-ins survive container restarts and updates.
* `Authentication__Mode=AccessKey` is a fallback without Entra ID (shared key, review and Excel only, no revoke).
* Unit tests: `dotnet test AccessReviewPortal.slnx` or `docker build --target test .`.

### Local development

```powershell
cd src/AccessReviewPortal
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=accessreview;Username=accessreview;Password=<password>"
dotnet user-secrets set "AzureAd:TenantId" "<tenant>"; dotnet user-secrets set "AzureAd:ClientId" "<app id>"; dotnet user-secrets set "AzureAd:ClientSecret" "<secret>"
dotnet run
```

Add `https://localhost:<port>/signin-oidc` as a redirect URI for local sign-in.

## 7. Using the portal

All pages use a dark theme (Bootstrap colour mode `dark` in `App.razor`, colours in `wwwroot/app.css`, the same
palette as the KPI dashboard). The sign-in pages use the same colours. Printing switches to light colours.

1. **Snapshots & Scan** – **Run Live Scan** with a label. Progress and log are shown live. Widgets: assignments per
   level, recommendations per reason, usage status, Defender CSPM findings, PostgreSQL size. The snapshot list shows
   who started a scan (manual, monthly schedule, quarterly campaign).
2. **Compare Current Snapshot vs Baseline Snapshot** – choose two snapshots and click **Recalculate**. The list
   *Movers and leavers* shows users whose department, manager or account status changed.
3. **Review** – filters: Scope Level, Subscription, Role Name, Assignment Type, Usage Evidence, **Defender CSPM (CIEM)**,
   Recommendation, Reviewer Decision, principal type, free text. Per row: **Accept**, or **Keep** / **Approve Removal**
   with a comment. The decision is saved with the UPN of the reviewer. Removal candidates show whether *you* can revoke them.
   The button **Changes Pending (N) →** opens the next step.
4. **Changes Pending** – every approved change of the snapshot in one list (*Approve Removal* rows and *Remove from Group*
   members): what is removed, from whom, who approved it, and whether *you* are allowed to make the change. One button,
   **Apply**, makes all changes that you are allowed to make, with your own Entra ID token: Azure role assignments,
   PIM `AdminRemove` requests, Entra `adminRemove` requests, cloud group members. **Cancel** puts a decision back to
   Pending. Changes that need a role you do not have stay pending for a colleague. The result is stored per row
   (`RemediatedByUpn`, `RemediatedAtUtc`, `RemediationStatus`) and shown under *Applied*. The navigation shows the number
   of pending changes. A PowerShell script is still available as an option (needed only for groups synced from AD).
5. **Group Permissions** – every group with Entra ID or Azure RBAC assignments: source (cloud / synced from AD), owners,
   roles and scopes, members, active and unused members (90 days), movers / disabled members, Defender flags.
   Click a group: *Assigned Roles & Scopes* (mark the group's assignment for removal when no member uses it) and
   *Group Members Usage Matrix* (**Keep in Group** / **Remove from Group** per member). These decisions also appear on
   *Changes Pending*.
6. **KPI & Reports** – dashboard in a dark, Grafana-like style for a period (this quarter, previous quarter, last 30 or
   90 days, year to date, last 12 months, or custom dates; UTC). The charts are plain SVG drawn on the server: no
   JavaScript library and no internet access needed.
   - **Users with unused roles** – users with at least one assignment without use in the inactivity threshold, from the
     last snapshot of the period, compared with the snapshot at the start of the period (stat with a line over the
     snapshots, and a gauge with the share). Service principals are shown apart.
   - **Users cleaned up** – users with at least one change applied in the period (**Apply** on *Changes Pending*), with
     the number of role assignments and group memberships removed.
   - **Exceptions** – *Keep* or *Keep in Group* on a role that was not used, or on a row with a *Remove* recommendation
     (leaver, mover, denied in self-review). The list shows who kept the access and the comment (justification).
     Exceptions without a comment are flagged. An exception renewed in a later review counts once.
   - **Waiting for Apply**, and a **Review progress** gauge for the last snapshot.
   - Charts: unused access per snapshot, role assignments of users by usage (donut), users with unused roles by
     department (bar gauge, with the number at the start of the period, users cleaned up and users with exceptions),
     review activity per day / week / month (decisions as columns, changes applied as a line).
   - **Reviewers** – per reviewer: decisions, approvals, exceptions, users approved for removal, users cleaned up and
     changes applied. "Cleaned up" counts the person who pressed **Apply**.
   - Lists of exceptions and of removed access, and the numbers of every snapshot in the period.

   **Excel report** downloads the same data (sheets *Summary*, *Reviewers*, *Departments*, *Snapshots*, *Activity*,
   *Exceptions*, *Removals*). **Print / PDF** prints the page in light colours without the menu. The numbers on the tiles
   open the filtered *Review* list. Hover a point or a column for its values.
7. **Settings > Subscriptions** – discovery of management groups and subscriptions (Resource Graph), scanner access (Reader /
   Monitoring Reader / no write rights), Defender CSPM and CIEM state, **Monitor in Portal**, onboarding script generator.
8. **Settings > Automation** – monthly activity snapshot and quarterly campaign (snapshot, baseline, recommendations, Excel archive,
   webhook notification), **Run now**, campaign archive with the Excel reports.
9. **Export Excel** – sheets *Access Review* (review format plus *Defender CSPM (CIEM)*), *Details*, *Group Permissions*, *Summary*.
10. **Settings > Diagnostics** – PostgreSQL, Graph permissions of the scanner, test calls, zero-privilege checks, Defender CIEM per
    subscription, and the delegated access of the signed-in reviewer.

## 8. Repro scenario on a test subscription

```powershell
Connect-AzAccount -Tenant <tenant-id>
.\scripts\repro-test-data.ps1 -Action Create -SubscriptionId <sub>   # test users, group, RG, assignments
# Portal: Run Live Scan "Q1 Baseline"
.\scripts\repro-test-data.ps1 -Action Move -SubscriptionId <sub>     # department + manager change, one user disabled
# Portal: Run Live Scan "Q2 Review" -> rows of "AR POC Mover": Remove (Department/Manager Changed ...),
#         rows of "AR POC Leaver": Remove (Left Organization / Account Disabled)
# Portal: Review -> Approve Removal; Changes Pending -> Apply (as an Owner of the test resource group)
.\scripts\repro-test-data.ps1 -Action Revert -SubscriptionId <sub>   # or -Action Remove to delete the test data
```

## 9. Scale and database size

Measured numbers and sizing tables: [docs/SIZING.md](docs/SIZING.md).

One review row with indexes takes about 2–3 KB. 10,000 assignments per quarter is about 25 MB per snapshot.
For large tenants the schema has composite indexes on `ScanSnapshotId` with `ScopeLevel`, `SubscriptionId`,
`PrincipalId`, `AssignedViaGroupId`, `RecommendedAction` and `ReviewerDecision`, and small partial indexes on
`ReviewedAtUtc` and `RemediatedAtUtc` for the KPI report (only rows with a decision or an applied change); scans are saved in chunks
(`Database__InsertChunkSize`, default 2000 rows). `deploy/postgres/postgresql.conf` and
`deploy/bicep/postgres-flexible.bicep` contain settings for larger installations.

## 10. Configuration reference

| Variable | Default | Meaning |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | – | PostgreSQL connection (set by docker-compose) |
| `AzureAd__TenantId`, `AzureAd__ClientId`, `AzureAd__ClientSecret` | – | App registration (scanner and sign-in) |
| `Authentication__Mode` | `EntraId` | `EntraId` or `AccessKey` (fallback, no revoke) |
| `Authentication__EnableDeviceCodeSignIn` | true | "Sign in with a code from another device" |
| `Authorization__RequireAppRole` | true | Users need `AccessReview.Admin` or `AccessReview.Reviewer` |
| `Portal__PublicBaseUrl` | empty | Public address, used in notification links |
| `Portal__AccessKey` | empty | Shared key, only for `Authentication__Mode=AccessKey` |
| `Database__ApplyMigrationsOnStartup` | true (compose: false) | Migrate at start; false when the `migrate` step runs |
| `Database__InsertChunkSize` | 2000 | Rows per insert batch during a scan |
| `DefenderCspm__Enabled` | true | Read Defender CSPM (CIEM) findings |
| `Discovery__MonitorNewSubscriptionsByDefault` | true | New subscriptions are scanned automatically |
| `Automation__MonthlySnapshotEnabled`, `__MonthlyDayOfMonth`, `__MonthlyTimeUtc` | true, 1, 02:00 | Monthly activity snapshot |
| `Automation__QuarterlyCampaignEnabled`, `__QuarterlyMonths__0..n`, `__QuarterlyDayOfMonth`, `__QuarterlyTimeUtc` | true, 1/4/7/10, 1, 04:00 | Quarterly campaign |
| `Automation__NotificationWebhookUrl` | empty | Webhook for "campaign ready" (Power Automate, Logic Apps) |
| `Automation__CheckIntervalSeconds`, `Automation__RetryAfterMinutes` | 60, 60 | Scheduler check interval, retry of a failed run |
| `ReviewSettings__InactivityThresholdDays` | 90 | Default threshold (UI can change it) |
| `ReviewSettings__LogAnalyticsWorkspaceId` | empty | Workspace id (GUID) for exact evidence and 90-day sign-ins |
| `ReviewSettings__SubscriptionIds__0..n` | all | Limit the scan when discovery has not run |
| `ReviewSettings__ActivityLogLookbackDays` | 90 | Days of Activity Log read through REST (max 90) |
| `ReviewSettings__ActivityLogParallelism` | 12 | Parallel Activity Log requests |
| `ReviewSettings__ActivityLogMaxEventsPerSubscription` | 300000 | Safety cap per subscription |
| `ReviewSettings__SignInTimeBudgetMinutes` | 8 | Time limit for Graph sign-in logs |
| `ReviewSettings__AccessReviewLookbackDays` | 180 | Access Review instances that ended in this window |
| `ReviewSettings__AccessReviewTimeBudgetMinutes` | 5 | Time limit for native Access Review decisions |
| `ReviewSettings__AdminPortalAppIds__0..n` | Azure Portal, Entra blades, Graph CLI, M365 Security | Sign-ins that count for Entra roles |

## 11. Notes

* Library versions: Microsoft.Graph 6.x is used instead of 5.x: 5.x depends on `Microsoft.Kiota.Abstractions`
  1.21.1 with a known high-severity advisory (GHSA-7j59-v9qr-6fq9); 6.x uses the fixed Kiota 2.x.
  The Activity Log and PIM are read through REST because the SDK models drop `authorization.evidence`
  and PIM `expandedProperties`.
* Device code sign-in uses a public client flow; MSAL gives public clients no cache key, so the token cache is
  bound to the home account id of the user.
* A manager approval flow ("Ask Manager") is not part of this version.

## 12. License

MIT, see [LICENSE](LICENSE).
