using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using AccessReviewPortal.Data;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using AccessReviewPortal.Services.Identity;
using AccessReviewPortal.Services.Review;
using AccessReviewPortal.Services.Scanning;
using global::Azure.Monitor.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Services;

public sealed record DiagnosticCheck(string Area, string Name, string Status, string Details);

/// <summary>
/// Checks PostgreSQL, the read-only scanner identity (Graph application permissions, Azure RBAC, zero-privilege rule)
/// and the delegated access of the signed-in reviewer.
/// </summary>
public sealed class DiagnosticsService(
    IDbContextFactory<AppDbContext> dbFactory,
    AzureClients clients,
    ArmRestClient arm,
    UserTokenService userTokens,
    IHttpClientFactory httpClientFactory,
    IOptions<ReviewSettingsOptions> reviewOptions,
    IOptions<PortalAuthOptions> authOptions,
    IOptions<PortalAuthorizationOptions> authorizationOptions,
    SettingsService settings)
{
    public const string Ok = "OK", Warning = "Warning", Failed = "Failed", Info = "Info";

    public static readonly (string Permission, string UsedFor)[] RequiredGraphPermissions =
    [
        ("User.Read.All", "User profiles: department, manager, account status"),
        ("Directory.Read.All", "Groups, owners, transitive members, principal lookup"),
        ("RoleManagement.Read.Directory", "Entra role definitions, assignments, PIM schedules"),
        ("AuditLog.Read.All", "Sign-in logs, directory audit, signInActivity"),
        ("AccessReview.Read.All", "Native Entra ID Access Review decisions")
    ];

    /// <summary>Application permissions that would break the zero-privilege rule for the scanner.</summary>
    public static readonly string[] ForbiddenGraphPermissions =
    [
        "RoleManagement.ReadWrite.Directory", "RoleAssignmentSchedule.ReadWrite.Directory", "RoleEligibilitySchedule.ReadWrite.Directory",
        "GroupMember.ReadWrite.All", "Group.ReadWrite.All", "Directory.ReadWrite.All", "User.ReadWrite.All", "Application.ReadWrite.All",
        "AppRoleAssignment.ReadWrite.All"
    ];

    public async Task<List<DiagnosticCheck>> RunAsync(ClaimsPrincipal? user, CancellationToken ct)
    {
        var checks = new List<DiagnosticCheck>();
        await CheckConfigurationAsync(checks, ct);
        await CheckDatabaseAsync(checks, ct);
        await CheckGraphAsync(checks, ct);
        await CheckArmAsync(checks, ct);
        await CheckLogAnalyticsAsync(checks, ct);
        if (user is not null)
        {
            await CheckSignedInUserAsync(checks, user, ct);
        }
        return checks;
    }

    private async Task CheckConfigurationAsync(List<DiagnosticCheck> checks, CancellationToken ct)
    {
        var o = reviewOptions.Value;
        var auth = authOptions.Value;
        checks.Add(new("Configuration", "Scanner credential", clients.AuthMode.StartsWith("Service principal") ? Ok : Warning, clients.AuthMode));
        checks.Add(new("Configuration", "Tenant", clients.TenantId is null ? Warning : Ok, clients.TenantId ?? "AzureAd__TenantId is not set"));
        checks.Add(new("Configuration", "Portal sign-in", auth.IsEntraId ? Ok : Warning, auth.IsEntraId
            ? $"Microsoft Entra ID (OpenID Connect){(auth.EnableDeviceCodeSignIn ? " + sign-in with a code from another device" : "")}. " +
              $"App roles required: {(authorizationOptions.Value.RequireAppRole ? $"{authorizationOptions.Value.AdminRole} / {authorizationOptions.Value.ReviewerRole}" : "no")}."
            : "Access key (fallback mode): review only, revoke is not possible without Entra ID sign-in."));
        checks.Add(new("Configuration", "Inactivity threshold", Info, $"{await settings.GetInactivityThresholdDaysAsync(ct)} days (default from configuration: {o.InactivityThresholdDays})"));
        checks.Add(new("Configuration", "Log Analytics workspace", Info, string.IsNullOrWhiteSpace(o.LogAnalyticsWorkspaceId)
            ? "Not configured. Activity Log is read through REST (inferred attribution, 90 days); sign-ins and directory audit through Graph (30 days)."
            : o.LogAnalyticsWorkspaceId));
        checks.Add(new("Configuration", "Revoke", Info, "Only with the delegated token of the signed-in reviewer. The scanner never changes anything."));
    }

    private async Task CheckDatabaseAsync(List<DiagnosticCheck> checks, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var version = await db.Database.SqlQueryRaw<string>("SELECT version() AS \"Value\"").SingleAsync(ct);
            var size = await SnapshotService.GetDatabaseSizeAsync(db, ct);
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            checks.Add(new("PostgreSQL", "Connection", Ok, version.Split(',')[0]));
            checks.Add(new("PostgreSQL", "Database size", Info, $"{size / 1024.0 / 1024.0:F2} MB (pg_database_size)"));
            checks.Add(new("PostgreSQL", "Migrations", pending.Count == 0 ? Ok : Warning, pending.Count == 0 ? "Schema is up to date" : "Pending: " + string.Join(", ", pending)));
        }
        catch (Exception ex)
        {
            checks.Add(new("PostgreSQL", "Connection", Failed, ex.Message));
        }
    }

    private async Task CheckGraphAsync(List<DiagnosticCheck> checks, CancellationToken ct)
    {
        string token;
        try
        {
            token = (await clients.GetTokenAsync(AzureClients.GraphScope, ct)).Token;
        }
        catch (Exception ex)
        {
            checks.Add(new("Microsoft Graph", "Token", Failed, ex.Message.Split('\n')[0]));
            return;
        }

        var info = AzureClients.DescribeToken(token);
        checks.Add(new("Microsoft Graph", "Token", Ok, info.IsAppOnly
            ? $"App-only token for appId {info.AppId}, tenant {info.TenantId}"
            : $"Delegated token for {info.Upn} (scopes: {string.Join(' ', info.Scopes)})"));

        if (info.IsAppOnly)
        {
            foreach (var (permission, usedFor) in RequiredGraphPermissions)
            {
                var granted = info.Roles.Contains(permission, StringComparer.OrdinalIgnoreCase);
                checks.Add(new("Graph permission", permission, granted ? Ok : Failed, granted ? usedFor : $"Not granted. Needed for: {usedFor}"));
            }
            var forbidden = info.Roles.Where(r => ForbiddenGraphPermissions.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            checks.Add(new("Zero-privilege scanner", "Graph application permissions", forbidden.Count == 0 ? Ok : Failed, forbidden.Count == 0
                ? "Read-only: no write application permissions."
                : $"Write permissions granted to the scanner: {string.Join(", ", forbidden)}. Remove them (revoke runs with the user's delegated token)."));
        }

        (string Name, string Url)[] probes =
        [
            ("Organization", "https://graph.microsoft.com/v1.0/organization?$select=id,displayName"),
            ("Users with signInActivity (P1/P2)", "https://graph.microsoft.com/v1.0/users?$top=1&$select=id,signInActivity"),
            ("Entra PIM eligible schedules (P2)", "https://graph.microsoft.com/v1.0/roleManagement/directory/roleEligibilityScheduleInstances?$top=1"),
            ("Access Review definitions", "https://graph.microsoft.com/v1.0/identityGovernance/accessReviews/definitions?$top=1"),
            ("Sign-in logs", "https://graph.microsoft.com/v1.0/auditLogs/signIns?$top=1"),
            ("Directory audit log", "https://graph.microsoft.com/v1.0/auditLogs/directoryAudits?$top=1")
        ];
        var http = httpClientFactory.CreateClient("graph");
        foreach (var (name, url) in probes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try
            {
                using var response = await http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                checks.Add(response.IsSuccessStatusCode
                    ? new("Graph API", name, Ok, $"HTTP {(int)response.StatusCode}")
                    : new("Graph API", name, Failed, $"HTTP {(int)response.StatusCode}: {GraphError(body)}"));
            }
            catch (Exception ex)
            {
                checks.Add(new("Graph API", name, Failed, ex.Message));
            }
        }
    }

    private async Task CheckArmAsync(List<DiagnosticCheck> checks, CancellationToken ct)
    {
        string token;
        try
        {
            token = (await clients.GetTokenAsync(AzureClients.ArmScope, ct)).Token;
        }
        catch (Exception ex)
        {
            checks.Add(new("Azure Resource Manager", "Token", Failed, ex.Message.Split('\n')[0]));
            return;
        }
        var info = AzureClients.DescribeToken(token);
        checks.Add(new("Azure Resource Manager", "Token", Ok, $"Object id {info.ObjectId}"));

        List<JsonElement> subscriptions;
        try
        {
            subscriptions = await arm.GetAllAsync("/subscriptions?api-version=2022-12-01", ct);
            checks.Add(new("Azure Resource Manager", "Subscriptions", subscriptions.Count > 0 ? Ok : Warning,
                subscriptions.Count == 0 ? "No subscriptions visible. Assign Reader on a management group or subscription."
                    : string.Join(", ", subscriptions.Select(s => s.Str("displayName")))));
        }
        catch (ArmRequestException ex)
        {
            checks.Add(new("Azure Resource Manager", "Subscriptions", Failed, ex.Message));
            return;
        }

        try
        {
            var mgs = await arm.GetAllAsync("/providers/Microsoft.Management/managementGroups?api-version=2021-04-01", ct);
            checks.Add(new("Azure Resource Manager", "Management groups", mgs.Count > 0 ? Ok : Warning,
                mgs.Count == 0 ? "No management groups visible (MG-level assignments will be seen only as inherited)."
                    : string.Join(", ", mgs.Select(m => m.Str("properties", "displayName") ?? m.Str("name")))));
        }
        catch (ArmRequestException ex)
        {
            checks.Add(new("Azure Resource Manager", "Management groups", Warning, ex.Message));
        }

        foreach (var sub in subscriptions.Take(10))
        {
            var id = sub.Str("subscriptionId") ?? "";
            var name = sub.Str("displayName") ?? id;
            try
            {
                var permissions = await arm.GetAllAsync($"/subscriptions/{id}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01", ct);
                var actions = permissions.SelectMany(p => p.Obj("actions")?.EnumerateArray().Select(a => a.GetString() ?? "") ?? []).ToList();
                var notActions = permissions.SelectMany(p => p.Obj("notActions")?.EnumerateArray().Select(a => a.GetString() ?? "") ?? []).ToList();
                bool Has(string action) => actions.Any(a => PermissionMatcher.Matches(a, action)) && !notActions.Any(n => PermissionMatcher.Matches(n, action));

                checks.Add(new("Azure RBAC", $"{name}: read (Reader)", Has("Microsoft.Authorization/roleAssignments/read") ? Ok : Failed, "Microsoft.Authorization/roleAssignments/read, */read"));
                checks.Add(new("Azure RBAC", $"{name}: Activity Log", Has("Microsoft.Insights/eventtypes/values/read") ? Ok : Failed, "Microsoft.Insights/eventtypes/values/read (Reader / Monitoring Reader)"));
                var writeActions = actions.Where(a => !PermissionMatcher.IsReadOnlyAction(a)).Distinct().ToList();
                checks.Add(new("Zero-privilege scanner", $"{name}: Azure RBAC", writeActions.Count == 0 ? Ok : Failed, writeActions.Count == 0
                    ? "Read-only: Reader / Monitoring Reader only."
                    : $"The scanner can change resources ({string.Join(", ", writeActions.Take(5))}). Remove Owner / Contributor / User Access Administrator from it."));
                var ciem = await DefenderCspmCollector.IsCiemEnabledAsync(arm, id, ct);
                checks.Add(new("Defender CSPM", $"{name}: CIEM", ciem switch { true => Ok, false => Warning, _ => Info }, ciem switch
                {
                    true => "Defender CSPM with Permissions management (CIEM) is enabled: overprovisioned / inactive identity findings are available.",
                    false => "Defender CSPM or its CIEM extension is not enabled: the Defender CSPM column stays empty for this subscription.",
                    _ => "Pricing state could not be read."
                }));
            }
            catch (ArmRequestException ex)
            {
                checks.Add(new("Azure RBAC", $"{name}: permissions", Failed, ex.Message));
            }

            try
            {
                var from = DateTime.UtcNow.AddHours(-1);
                var filter = Uri.EscapeDataString($"eventTimestamp ge '{from:yyyy-MM-ddTHH:mm:ssZ}' and eventTimestamp le '{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}'");
                var page = await arm.GetJsonAsync($"/subscriptions/{id}/providers/Microsoft.Insights/eventtypes/management/values?api-version=2015-04-01&$filter={filter}", ct);
                var count = page.TryGetProperty("value", out var v) ? v.GetArrayLength() : 0;
                checks.Add(new("Azure Activity Log", name, Ok, $"Readable ({count} events in the last hour on the first page)"));
            }
            catch (ArmRequestException ex)
            {
                checks.Add(new("Azure Activity Log", name, Failed, ex.Message));
            }

            if (info.ObjectId is not null)
            {
                try
                {
                    var filter = Uri.EscapeDataString($"principalId eq '{info.ObjectId}'");
                    var own = await arm.GetAllAsync($"/subscriptions/{id}/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01&$filter={filter}", ct);
                    var roleNames = new List<string>();
                    foreach (var ra in own)
                    {
                        var roleDefId = ra.Str("properties", "roleDefinitionId");
                        var scope = ra.Str("properties", "scope");
                        string roleName;
                        try
                        {
                            roleName = (await arm.GetJsonAsync($"{roleDefId}?api-version=2022-04-01", ct)).Str("properties", "roleName") ?? roleDefId ?? "";
                        }
                        catch (ArmRequestException)
                        {
                            roleName = roleDefId ?? "";
                        }
                        roleNames.Add($"{roleName} @ {scope}");
                    }
                    checks.Add(new("Azure RBAC", $"{name}: roles of this identity", roleNames.Count > 0 ? Ok : Warning,
                        roleNames.Count > 0 ? string.Join("; ", roleNames.Distinct()) : "No role assignments found for this identity"));
                }
                catch (ArmRequestException ex)
                {
                    checks.Add(new("Azure RBAC", $"{name}: roles of this identity", Warning, ex.Message));
                }
            }
        }
    }

    private async Task CheckSignedInUserAsync(List<DiagnosticCheck> checks, ClaimsPrincipal user, CancellationToken ct)
    {
        var authz = authorizationOptions.Value;
        checks.Add(new("Signed-in reviewer", "Identity", Info,
            $"{user.GetDisplayName()} ({user.GetUpn()}), sign-in: {user.GetLoginMethod()}, " +
            $"portal role: {(user.IsPortalAdmin(authz) ? "Admin" : user.IsPortalReviewer(authz) ? "Reviewer" : "none")}"));
        if (!user.CanUseDelegatedTokens())
        {
            checks.Add(new("Signed-in reviewer", "Delegated access", Info, "Access key mode: no delegated token, revoke is not possible."));
            return;
        }

        try
        {
            var token = await userTokens.GetAccessTokenAsync(user, UserTokenService.ArmScopes, ct);
            var info = AzureClients.DescribeToken(token);
            checks.Add(new("Signed-in reviewer", "Azure Resource Manager (delegated)", Ok, $"Token for {info.Upn ?? user.GetUpn()}, scopes: {string.Join(' ', info.Scopes)}"));

            var userArm = arm.WithUserToken(t => userTokens.GetAccessTokenAsync(user, UserTokenService.ArmScopes, t));
            foreach (var sub in (await userArm.GetAllAsync("/subscriptions?api-version=2022-12-01", ct)).Take(10))
            {
                var id = sub.Str("subscriptionId");
                var blocks = await userArm.GetAllAsync($"/subscriptions/{id}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01", ct);
                var actions = blocks.SelectMany(p => p.Obj("actions")?.EnumerateArray().Select(a => a.GetString() ?? "") ?? []).ToList();
                var notActions = blocks.SelectMany(p => p.Obj("notActions")?.EnumerateArray().Select(a => a.GetString() ?? "") ?? []).ToList();
                bool Has(string action) => actions.Any(a => PermissionMatcher.Matches(a, action)) && !notActions.Any(n => PermissionMatcher.Matches(n, action));
                var canDelete = Has("Microsoft.Authorization/roleAssignments/delete");
                checks.Add(new("Signed-in reviewer", $"{sub.Str("displayName")}: your rights", canDelete ? Ok : Info, canDelete
                    ? "You can remove role assignments here (roleAssignments/delete). Apply on the Changes Pending page works for this subscription."
                    : "You cannot remove role assignments here: you can record decisions only."));
            }
        }
        catch (UserConsentRequiredException ex)
        {
            checks.Add(new("Signed-in reviewer", "Azure Resource Manager (delegated)", Warning, $"Consent or a new sign-in is needed: {ex.Message}"));
        }
        catch (ArmRequestException ex)
        {
            checks.Add(new("Signed-in reviewer", "Azure Resource Manager (delegated)", Failed, ex.Message));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            checks.Add(new("Signed-in reviewer", "Azure Resource Manager (delegated)", Failed, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private async Task CheckLogAnalyticsAsync(List<DiagnosticCheck> checks, CancellationToken ct)
    {
        var workspace = reviewOptions.Value.LogAnalyticsWorkspaceId;
        if (string.IsNullOrWhiteSpace(workspace)) return;

        var client = new LogsQueryClient(clients.Credential);
        foreach (var table in new[] { "AzureActivity", "SigninLogs" })
        {
            try
            {
                var result = await client.QueryWorkspaceAsync(workspace, $"{table} | where TimeGenerated > ago(90d) | summarize Rows = count(), Oldest = min(TimeGenerated)",
                    new QueryTimeRange(TimeSpan.FromDays(90)), cancellationToken: ct);
                var row = result.Value.Table.Rows.FirstOrDefault();
                var rows = row?.GetInt64("Rows") ?? 0;
                checks.Add(new("Log Analytics", table, rows > 0 ? Ok : Warning,
                    rows > 0 ? $"{rows} rows, oldest {row?.GetDateTimeOffset("Oldest"):yyyy-MM-dd}" : "Table is empty"));
            }
            catch (Exception ex)
            {
                checks.Add(new("Log Analytics", table, Failed, ex.Message.Split('\n')[0]));
            }
        }
    }

    private static string GraphError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body;
            if (message.StartsWith('{'))
            {
                using var inner = JsonDocument.Parse(message);
                if (inner.RootElement.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
            }
            return message.Length > 300 ? message[..300] + "..." : message;
        }
        catch (Exception)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }
}
