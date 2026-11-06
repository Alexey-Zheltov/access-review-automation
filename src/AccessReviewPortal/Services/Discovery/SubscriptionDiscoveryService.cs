using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AccessReviewPortal.Data;
using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using AccessReviewPortal.Services.Identity;
using AccessReviewPortal.Services.Scanning;
using global::Azure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Services.Discovery;

public sealed record ManagementGroupItem(string Name, string DisplayName, string? ParentName, bool VisibleToScanner);

public sealed class DiscoveryResult
{
    public List<MonitoredSubscription> Subscriptions { get; init; } = [];
    public List<ManagementGroupItem> ManagementGroups { get; init; } = [];
    public string? ScannerObjectId { get; init; }
    public string? ScannerAppId { get; init; }
    public List<string> Messages { get; } = [];
}

/// <summary>
/// Finds management groups and subscriptions with Azure Resource Graph (ResourceContainers), checks the read-only
/// access of the scanner (Reader, Monitoring Reader, Activity Log, no write roles) and Defender CSPM / CIEM state,
/// and keeps the "Monitor in Portal" switch in the MonitoredSubscriptions table. With a signed-in Entra ID user it
/// also lists subscriptions that the user can see but the scanner cannot (candidates for onboarding).
/// </summary>
public sealed class SubscriptionDiscoveryService(
    IDbContextFactory<AppDbContext> dbFactory,
    AzureClients clients,
    ArmRestClient arm,
    UserTokenService tokens,
    IOptions<DiscoveryOptions> options,
    ILogger<SubscriptionDiscoveryService> logger)
{
    private const string SubscriptionsQuery = """
        resourcecontainers
        | where type =~ 'microsoft.resources/subscriptions'
        | project id, subscriptionId, name, tenantId, state = tostring(properties.state), mgChain = properties.managementGroupAncestorsChain
        """;

    private const string ManagementGroupsQuery = """
        resourcecontainers
        | where type =~ 'microsoft.management/managementgroups'
        | project id, name, displayName = tostring(properties.displayName), parent = tostring(properties.details.parent.name)
        """;

    private const string ResourceCountQuery = """
        resources
        | summarize resources = count() by subscriptionId
        | project id = subscriptionId, subscriptionId, resources
        """;

    public async Task<DiscoveryResult> RefreshAsync(ClaimsPrincipal? user, CancellationToken ct)
    {
        var token = AzureClients.DescribeToken((await clients.GetTokenAsync(AzureClients.ArmScope, ct)).Token);
        var now = DateTime.UtcNow;
        var found = new Dictionary<string, MonitoredSubscription>(StringComparer.OrdinalIgnoreCase);
        var managementGroups = new Dictionary<string, ManagementGroupItem>(StringComparer.OrdinalIgnoreCase);
        var result = new DiscoveryResult { ScannerObjectId = token.ObjectId, ScannerAppId = token.AppId };

        // 1. What the scanner sees (Azure Resource Graph).
        try
        {
            foreach (var mg in await ResourceGraph.QueryAsync(clients, ManagementGroupsQuery, null, ct))
            {
                var name = mg.Str("name") ?? "";
                managementGroups[name] = new ManagementGroupItem(name, mg.Str("displayName") ?? name, NullIfEmpty(mg.Str("parent")), true);
            }

            var counts = (await ResourceGraph.QueryAsync(clients, ResourceCountQuery, null, ct))
                .GroupBy(r => r.Str("subscriptionId") ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(r => int.TryParse(r.Str("resources"), out var n) ? n : 0), StringComparer.OrdinalIgnoreCase);

            foreach (var s in await ResourceGraph.QueryAsync(clients, SubscriptionsQuery, null, ct))
            {
                var id = s.Str("subscriptionId") ?? "";
                var chain = s.Obj("mgChain") is { ValueKind: JsonValueKind.Array } c
                    ? c.EnumerateArray().Select(m => (Name: m.Str("name") ?? "", DisplayName: m.Str("displayName") ?? m.Str("name") ?? "")).ToList()
                    : [];
                found[id] = new MonitoredSubscription
                {
                    SubscriptionId = id,
                    DisplayName = s.Str("name") ?? id,
                    TenantId = s.Str("tenantId"),
                    State = s.Str("state") ?? "",
                    ManagementGroupId = chain.FirstOrDefault().Name,
                    ManagementGroupName = chain.FirstOrDefault().DisplayName,
                    ManagementGroupPath = string.Join(" / ", Enumerable.Reverse(chain).Select(m => m.DisplayName)),
                    VisibleToScanner = true,
                    ResourceCount = counts.GetValueOrDefault(id),
                    LastDiscoveredUtc = now
                };
            }
        }
        catch (RequestFailedException ex)
        {
            result.Messages.Add($"Azure Resource Graph query failed: {ex.Message.Split('\n')[0]}");
        }

        // 2. Read-only access of the scanner and Defender CSPM state per subscription.
        await Parallel.ForEachAsync(found.Values, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (sub, token2) =>
        {
            await CheckScannerAccessAsync(sub, token.ObjectId, token2);
            sub.DefenderCspmEnabled = await DefenderCspmCollector.IsDefenderCspmEnabledAsync(arm, sub.SubscriptionId, token2) ?? false;
            sub.CiemEnabled = sub.DefenderCspmEnabled && (await DefenderCspmCollector.IsCiemEnabledAsync(arm, sub.SubscriptionId, token2) ?? false);
        });

        // 3. What the signed-in user sees (delegated): subscriptions and management groups not yet onboarded.
        if (user?.CanUseDelegatedTokens() == true)
        {
            try
            {
                var userArm = arm.WithUserToken(t => tokens.GetAccessTokenAsync(user, UserTokenService.ArmScopes, t));
                foreach (var s in await userArm.GetAllAsync("/subscriptions?api-version=2022-12-01", ct))
                {
                    var id = s.Str("subscriptionId") ?? "";
                    if (found.ContainsKey(id)) continue;
                    found[id] = new MonitoredSubscription
                    {
                        SubscriptionId = id,
                        DisplayName = s.Str("displayName") ?? id,
                        TenantId = s.Str("tenantId"),
                        State = s.Str("state") ?? "",
                        VisibleToScanner = false,
                        LastDiscoveredUtc = now,
                        DiscoveryError = "Not visible to the scanner: onboard it (Reader + Monitoring Reader)."
                    };
                }
                foreach (var mg in await userArm.GetAllAsync("/providers/Microsoft.Management/managementGroups?api-version=2021-04-01", ct))
                {
                    var name = mg.Str("name") ?? "";
                    managementGroups.TryAdd(name, new ManagementGroupItem(name, mg.Str("properties", "displayName") ?? name, null, false));
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The user's own view is optional: the scanner's view above is already complete.
                result.Messages.Add($"Your own subscriptions were not listed: {ex.Message.Split('\n')[0]}");
            }
        }

        // 4. Save. The "Monitor in Portal" switch of known subscriptions is kept.
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.MonitoredSubscriptions.ToDictionaryAsync(s => s.SubscriptionId, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var sub in found.Values)
        {
            if (existing.TryGetValue(sub.SubscriptionId, out var row))
            {
                var monitored = row.IsMonitored;
                var lastScanned = row.LastScannedUtc;
                db.Entry(row).CurrentValues.SetValues(sub);
                row.IsMonitored = monitored && sub.VisibleToScanner;
                row.LastScannedUtc = lastScanned;
            }
            else
            {
                sub.IsMonitored = sub.VisibleToScanner && options.Value.MonitorNewSubscriptionsByDefault;
                db.MonitoredSubscriptions.Add(sub);
            }
        }
        foreach (var gone in existing.Values.Where(e => !found.ContainsKey(e.SubscriptionId)))
        {
            gone.VisibleToScanner = false;
            gone.IsMonitored = false;
            gone.DiscoveryError = $"Not found in the last discovery ({now:yyyy-MM-dd HH:mm} UTC).";
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Discovery: {Count} subscriptions, {Visible} visible to the scanner.", found.Count, found.Values.Count(s => s.VisibleToScanner));
        return new DiscoveryResult
        {
            Subscriptions = await db.MonitoredSubscriptions.AsNoTracking().OrderBy(s => s.DisplayName).ToListAsync(ct),
            ManagementGroups = managementGroups.Values.OrderBy(m => m.DisplayName).ToList(),
            ScannerObjectId = token.ObjectId,
            ScannerAppId = token.AppId
        }.WithMessages(result.Messages);
    }

    public async Task<List<MonitoredSubscription>> GetAllAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MonitoredSubscriptions.AsNoTracking().OrderBy(s => s.DisplayName).ToListAsync(ct);
    }

    /// <summary>Subscriptions to scan. Null when discovery never ran (the scanner then uses the configuration / all subscriptions).</summary>
    public async Task<HashSet<string>?> GetMonitoredSubscriptionIdsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.MonitoredSubscriptions.AnyAsync(ct)) return null;
        var ids = await db.MonitoredSubscriptions.Where(s => s.IsMonitored && s.VisibleToScanner).Select(s => s.SubscriptionId).ToListAsync(ct);
        return ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetMonitoredAsync(string subscriptionId, bool monitored, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.MonitoredSubscriptions.Where(s => s.SubscriptionId == subscriptionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsMonitored, x => monitored && x.VisibleToScanner), ct);
    }

    public async Task MarkScannedAsync(IEnumerable<string> subscriptionIds, DateTime scannedUtc, CancellationToken ct)
    {
        var ids = subscriptionIds.ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.MonitoredSubscriptions.Where(s => ids.Contains(s.SubscriptionId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastScannedUtc, scannedUtc), ct);
    }

    private async Task CheckScannerAccessAsync(MonitoredSubscription sub, string? scannerObjectId, CancellationToken ct)
    {
        try
        {
            var blocks = await arm.GetAllAsync($"/subscriptions/{sub.SubscriptionId}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01", ct);
            var actions = blocks.SelectMany(b => Strings(b, "actions")).ToList();
            var notActions = blocks.SelectMany(b => Strings(b, "notActions")).ToList();
            bool Allows(string action) => actions.Any(a => PermissionMatcher.Matches(a, action)) && !notActions.Any(n => PermissionMatcher.Matches(n, action));

            sub.ScannerReaderOk = Allows("Microsoft.Authorization/roleAssignments/read") && Allows("Microsoft.Resources/subscriptions/resourceGroups/read");
            sub.ActivityLogReadable = Allows("Microsoft.Insights/eventtypes/values/read");
            sub.ScannerHasPrivilegedRoles = actions.Any(a => !PermissionMatcher.IsReadOnlyAction(a));
        }
        catch (ArmRequestException ex)
        {
            sub.DiscoveryError = ex.Message;
        }

        if (scannerObjectId is null) return;
        try
        {
            var filter = Uri.EscapeDataString($"principalId eq '{scannerObjectId}'");
            var assignments = await arm.GetAllAsync($"/subscriptions/{sub.SubscriptionId}/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01&$filter={filter}", ct);
            var roles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in assignments)
            {
                var scope = a.Str("properties", "scope") ?? "";
                // Only assignments on the subscription or above it apply to the whole subscription.
                if (!(scope == "/" || scope.StartsWith("/providers/Microsoft.Management/", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(scope, $"/subscriptions/{sub.SubscriptionId}", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var roleKey = Ids.RoleKeyFromArmId(a.Str("properties", "roleDefinitionId"));
                roles.Add(KnownRoleName(roleKey) ?? roleKey);
                if (roleKey == Ids.GuidKey(WellKnownIds.MonitoringReaderRole)) sub.ScannerMonitoringReaderOk = true;
            }
            sub.ScannerRoles = string.Join(", ", roles);
        }
        catch (ArmRequestException ex)
        {
            sub.DiscoveryError ??= ex.Message;
        }
    }

    private static string? KnownRoleName(string roleKey) => roleKey switch
    {
        _ when roleKey == Ids.GuidKey(WellKnownIds.ReaderRole) => "Reader",
        _ when roleKey == Ids.GuidKey(WellKnownIds.MonitoringReaderRole) => "Monitoring Reader",
        _ when roleKey == Ids.GuidKey(WellKnownIds.OwnerRole) => "Owner",
        _ when roleKey == Ids.GuidKey(WellKnownIds.ContributorRole) => "Contributor",
        _ when roleKey == Ids.GuidKey(WellKnownIds.UserAccessAdministratorRole) => "User Access Administrator",
        _ when roleKey == Ids.GuidKey(WellKnownIds.RbacAdministratorRole) => "Role Based Access Control Administrator",
        _ => null
    };

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(x => x.GetString() ?? "")
            : [];

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ---------------------------------------------------------------- onboarding script generator

    public enum ScriptFormat
    {
        PowerShell,
        AzureCli
    }

    /// <summary>Script that gives the scanner read-only access (Reader + Monitoring Reader) on the selected scopes.</summary>
    public static string BuildOnboardingScript(ScriptFormat format, string scannerObjectId, string? scannerAppId, string? tenantId,
        IReadOnlyCollection<string> managementGroupNames, IReadOnlyCollection<string> subscriptionIds)
    {
        var scopes = managementGroupNames.Select(m => $"/providers/Microsoft.Management/managementGroups/{m}")
            .Concat(subscriptionIds.Select(s => $"/subscriptions/{s}"))
            .ToList();
        string[] roles = ["Reader", "Monitoring Reader"];
        var sb = new StringBuilder();

        if (format == ScriptFormat.PowerShell)
        {
            sb.AppendLine("# Gives the Access Review scanner read-only access (Reader + Monitoring Reader).");
            sb.AppendLine("# Run as Owner, User Access Administrator or RBAC Administrator on the scopes. Module: Az.Resources.");
            sb.AppendLine($"# Scanner: object id {scannerObjectId}" + (scannerAppId is null ? "" : $", app id {scannerAppId}"));
            sb.AppendLine(tenantId is null ? "Connect-AzAccount" : $"Connect-AzAccount -Tenant '{tenantId}'");
            sb.AppendLine($"$scannerObjectId = '{scannerObjectId}'");
            sb.AppendLine("$scopes = @(");
            sb.AppendLine(string.Join(",\n", scopes.Select(s => $"    '{s}'")));
            sb.AppendLine(")");
            sb.AppendLine("foreach ($scope in $scopes) {");
            sb.AppendLine($"    foreach ($role in @({string.Join(", ", roles.Select(r => $"'{r}'"))})) {{");
            sb.AppendLine("        $existing = Get-AzRoleAssignment -ObjectId $scannerObjectId -RoleDefinitionName $role -Scope $scope -ErrorAction SilentlyContinue |");
            sb.AppendLine("            Where-Object { $_.Scope -eq $scope }");
            sb.AppendLine("        if ($existing) { Write-Host \"$role on $scope already assigned\"; continue }");
            sb.AppendLine("        New-AzRoleAssignment -ObjectId $scannerObjectId -ObjectType ServicePrincipal -RoleDefinitionName $role -Scope $scope | Out-Null");
            sb.AppendLine("        Write-Host \"Assigned $role on $scope\"");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine("#!/usr/bin/env bash");
            sb.AppendLine("# Gives the Access Review scanner read-only access (Reader + Monitoring Reader).");
            sb.AppendLine("# Run as Owner, User Access Administrator or RBAC Administrator on the scopes (Azure CLI).");
            sb.AppendLine("set -euo pipefail");
            if (tenantId is not null) sb.AppendLine($"az login --tenant {tenantId} --use-device-code");
            sb.AppendLine($"SCANNER_OBJECT_ID={scannerObjectId}");
            foreach (var scope in scopes)
            {
                foreach (var role in roles)
                {
                    sb.AppendLine($"az role assignment create --assignee-object-id \"$SCANNER_OBJECT_ID\" --assignee-principal-type ServicePrincipal " +
                                  $"--role \"{role}\" --scope \"{scope}\" --output none");
                }
            }
            sb.AppendLine("echo \"Done.\"");
        }
        return sb.ToString().Replace("\r\n", "\n");
    }
}

internal static class DiscoveryResultExtensions
{
    public static DiscoveryResult WithMessages(this DiscoveryResult result, IEnumerable<string> messages)
    {
        result.Messages.AddRange(messages);
        return result;
    }
}
