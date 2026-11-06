using System.Text.Json;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using global::Azure;
using global::Azure.Core;
using global::Azure.ResourceManager.Authorization;
using global::Azure.ResourceManager.Authorization.Models;
using global::Azure.ResourceManager.ManagementGroups;
using global::Azure.ResourceManager.Resources;
using Microsoft.Extensions.Options;

namespace AccessReviewPortal.Services.Scanning;

/// <summary>
/// Reads the Azure hierarchy (management groups, subscriptions, resource groups, resources), role definitions,
/// role assignments on all four levels and PIM (eligible / time-bound active / activations) for Azure resources.
/// </summary>
public sealed class ArmCollector(AzureClients clients, ArmRestClient rest, IOptions<ReviewSettingsOptions> options)
{
    private const string PimApi = "2020-10-01";

    public async Task CollectHierarchyAsync(ScanContext ctx, CancellationToken ct)
    {
        try
        {
            await foreach (var mg in clients.Arm.GetManagementGroups().GetAllAsync(cancellationToken: ct))
            {
                ctx.ManagementGroups.Add(new ManagementGroupInfo
                {
                    Name = mg.Data.Name,
                    DisplayName = mg.Data.DisplayName ?? mg.Data.Name,
                    Id = mg.Id.ToString()
                });
            }
        }
        catch (RequestFailedException ex)
        {
            ctx.Warn($"Management groups are not readable: {ex.Message.Split('\n')[0]}");
        }

        foreach (var mg in ctx.ManagementGroups)
        {
            try
            {
                await foreach (var d in rest.GetPagedAsync($"{mg.Id}/descendants?api-version=2020-05-01", ct))
                {
                    if ((d.Str("type") ?? "").EndsWith("/subscriptions", StringComparison.OrdinalIgnoreCase))
                    {
                        mg.DescendantSubscriptionIds.Add(d.Str("name") ?? "");
                    }
                }
            }
            catch (ArmRequestException ex)
            {
                ctx.Warn($"Descendants of management group '{mg.DisplayName}' are not readable: {ex.Message}");
            }
        }

        // Subscriptions switched on in Subscription Discovery win over the static configuration list.
        var filter = ctx.MonitoredSubscriptionIds
                     ?? options.Value.SubscriptionIds.Where(s => !string.IsNullOrWhiteSpace(s)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await foreach (var sub in clients.Arm.GetSubscriptions().GetAllAsync(ct))
        {
            var id = sub.Data.SubscriptionId;
            if (filter.Count > 0 && !filter.Contains(id))
            {
                continue;
            }

            ctx.Subscriptions.Add(new SubscriptionInfo(id, sub.Data.DisplayName ?? id, sub.Data.State?.ToString() ?? ""));
            try
            {
                await foreach (var _ in sub.GetResourceGroups().GetAllAsync(cancellationToken: ct))
                {
                    ctx.ResourceGroupCount++;
                }
                await foreach (var res in sub.GetGenericResourcesAsync(cancellationToken: ct))
                {
                    ctx.ResourceCount++;
                    ctx.ResourceTypes[res.Id.ToString()] = res.Data.ResourceType.ToString();
                }
            }
            catch (RequestFailedException ex)
            {
                ctx.Warn($"Inventory of subscription '{sub.Data.DisplayName}' failed: {ex.Message.Split('\n')[0]}");
            }
        }
    }

    public async Task CollectRoleDefinitionsAsync(ScanContext ctx, CancellationToken ct)
    {
        var scopes = ctx.Subscriptions.Select(s => "/subscriptions/" + s.SubscriptionId)
            .Concat(ctx.ManagementGroups.Select(m => m.Id))
            .ToList();

        var builtInsLoaded = false;
        foreach (var scope in scopes)
        {
            try
            {
                // Built-in roles are the same everywhere; after the first scope only custom roles are read.
                var collection = clients.Arm.GetAuthorizationRoleDefinitions(new ResourceIdentifier(scope));
                await foreach (var rd in collection.GetAllAsync(filter: builtInsLoaded ? "type eq 'CustomRole'" : null, cancellationToken: ct))
                {
                    var key = Ids.GuidKey(rd.Data.Name);
                    if (ctx.AzureRoles.ContainsKey(key))
                    {
                        continue;
                    }

                    var blocks = rd.Data.Permissions.Select(p => new PermissionBlock
                    {
                        Actions = p.Actions.ToList(),
                        NotActions = p.NotActions.ToList(),
                        DataActions = p.DataActions.ToList(),
                        NotDataActions = p.NotDataActions.ToList()
                    }).ToList();

                    ctx.AzureRoles[key] = new RoleDefinitionInfo
                    {
                        Key = key,
                        Id = rd.Id.ToString(),
                        Name = rd.Data.RoleName ?? key,
                        IsBuiltIn = rd.Data.RoleType == AuthorizationRoleType.BuiltInRole,
                        Permissions = blocks,
                        HasWritePermissions = PermissionMatcher.AzureRoleHasWrite(blocks)
                    };
                }
                builtInsLoaded = true;
            }
            catch (RequestFailedException ex)
            {
                ctx.Warn($"Role definitions at {scope} are not readable: {ex.Message.Split('\n')[0]}");
            }
        }
        ctx.Info($"Azure role definitions loaded: {ctx.AzureRoles.Count}.");
    }

    public async Task CollectAssignmentsAsync(ScanContext ctx, CancellationToken ct)
    {
        // 1. Active role assignments (Azure.ResourceManager.Authorization SDK).
        var roleAssignments = new Dictionary<string, RoleAssignmentRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var mg in ctx.ManagementGroups)
        {
            await ReadRoleAssignmentsAsync(ctx, mg.Id, "atScope()", roleAssignments,
                keep: s => s == "/" || string.Equals(s, mg.Id, StringComparison.OrdinalIgnoreCase), ct);
        }
        foreach (var sub in ctx.Subscriptions)
        {
            await ReadRoleAssignmentsAsync(ctx, "/subscriptions/" + sub.SubscriptionId, null, roleAssignments, keep: _ => true, ct);
        }

        // 2. PIM schedules and requests (REST: keeps expandedProperties and justification).
        var activeInstances = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var eligibleInstances = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var assignmentRequests = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var eligibilityRequests = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var pimScopes = ctx.ManagementGroups.Select(m => (Scope: m.Id, AtScope: true))
            .Concat(ctx.Subscriptions.Select(s => (Scope: "/subscriptions/" + s.SubscriptionId, AtScope: false)));
        foreach (var (scope, atScope) in pimScopes)
        {
            await ReadPimAsync(ctx, scope, "roleAssignmentScheduleInstances", atScope, activeInstances, ct);
            await ReadPimAsync(ctx, scope, "roleEligibilityScheduleInstances", atScope, eligibleInstances, ct);
            await ReadPimAsync(ctx, scope, "roleAssignmentScheduleRequests", atScope, assignmentRequests, ct);
            await ReadPimAsync(ctx, scope, "roleEligibilityScheduleRequests", atScope, eligibilityRequests, ct);
        }

        var activeByOrigin = activeInstances.Values
            .Where(i => i.Str("properties", "originRoleAssignmentId") is not null)
            .GroupBy(i => i.Str("properties", "originRoleAssignmentId")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var assignmentJustifications = LatestJustificationByTarget(assignmentRequests.Values, "targetRoleAssignmentScheduleId");
        var eligibilityJustifications = LatestJustificationByTarget(eligibilityRequests.Values, "targetRoleEligibilityScheduleId");

        // 3. PIM activation history (Source A for Azure resources).
        foreach (var req in assignmentRequests.Values)
        {
            if (!string.Equals(req.Str("properties", "requestType"), "SelfActivate", StringComparison.OrdinalIgnoreCase)
                || !IsSuccessfulRequest(req.Str("properties", "status")))
            {
                continue;
            }
            var created = req.Date("properties", "createdOn");
            if (created is null) continue;
            ctx.PimActivations.Add(new PimActivation(
                Ids.ObjectId(req.Str("properties", "principalId")),
                Ids.RoleKeyFromArmId(req.Str("properties", "roleDefinitionId")),
                req.Str("properties", "scope"),
                created.Value,
                req.Str("properties", "linkedRoleEligibilityScheduleId"),
                req.Str("properties", "justification"),
                "PIM activation request (ARM)"));
        }

        // 4. Active assignments -> Permanent / PIM Active. Activated eligible roles become activation evidence.
        var activeCount = 0;
        foreach (var ra in roleAssignments.Values)
        {
            activeByOrigin.TryGetValue(ra.Id, out var inst);
            var hasInstance = inst.ValueKind == JsonValueKind.Object;
            var instAssignmentType = hasInstance ? inst.Str("properties", "assignmentType") : null;

            if (string.Equals(instAssignmentType, "Activated", StringComparison.OrdinalIgnoreCase))
            {
                var started = inst.Date("properties", "startDateTime") ?? ctx.NowUtc;
                ctx.PimActivations.Add(new PimActivation(
                    Ids.ObjectId(ra.PrincipalId), Ids.RoleKeyFromArmId(ra.RoleDefinitionId), ra.Scope, started,
                    inst.Str("properties", "linkedRoleEligibilityScheduleId"), null, "Active PIM activation"));
                continue;
            }

            var end = hasInstance ? inst.Date("properties", "endDateTime") : null;
            var scheduleId = hasInstance ? inst.Str("properties", "roleAssignmentScheduleId") : null;
            var justification = !string.IsNullOrWhiteSpace(ra.Description)
                ? ra.Description
                : scheduleId is not null && assignmentJustifications.TryGetValue(scheduleId, out var j) ? j : null;

            ctx.Assignments.Add(NewArmAssignment(ctx, ra.Scope, ra.RoleDefinitionId, ra.PrincipalId, ra.PrincipalType,
                end.HasValue ? AssignmentType.PimActive : AssignmentType.Permanent,
                hasInstance ? inst.Date("properties", "startDateTime") : ra.CreatedOn, end, justification, ra.Id, null,
                hasInstance ? inst.Obj("properties", "expandedProperties") : null));
            activeCount++;
        }

        // 5. Eligible assignments.
        var eligibleKeys = new HashSet<string>(StringComparer.Ordinal);
        var eligibleCount = 0;
        foreach (var inst in eligibleInstances.Values)
        {
            var p = inst.Obj("properties");
            if (p is null) continue;
            var props = p.Value;
            if (string.Equals(props.Str("memberType"), "Group", StringComparison.OrdinalIgnoreCase))
            {
                continue; // members of eligible groups are expanded from the group assignment
            }

            var scope = props.Str("scope") ?? "";
            var roleDefinitionId = props.Str("roleDefinitionId") ?? "";
            var principalId = props.Str("principalId") ?? "";
            var dedupe = Ids.StableKey(Ids.NormalizeScope(scope), Ids.RoleKeyFromArmId(roleDefinitionId), Ids.GuidKey(principalId));
            if (!eligibleKeys.Add(dedupe))
            {
                continue;
            }

            var scheduleId = props.Str("roleEligibilityScheduleId");
            ctx.Assignments.Add(NewArmAssignment(ctx, scope, roleDefinitionId, principalId, props.Str("principalType") ?? "Unknown",
                AssignmentType.PimEligible, props.Date("startDateTime"), props.Date("endDateTime"),
                scheduleId is not null && eligibilityJustifications.TryGetValue(scheduleId, out var ej) ? ej : null,
                inst.Str("id"), scheduleId, props.Obj("expandedProperties")));
            eligibleCount++;
        }

        ctx.Info($"Azure RBAC: {activeCount} active and {eligibleCount} eligible assignments, " +
                 $"{ctx.PimActivations.Count} PIM activations in history.");
    }

    private async Task ReadRoleAssignmentsAsync(ScanContext ctx, string scope, string? filter,
        Dictionary<string, RoleAssignmentRecord> target, Func<string, bool> keep, CancellationToken ct)
    {
        try
        {
            var collection = clients.Arm.GetRoleAssignments(new ResourceIdentifier(scope));
            await foreach (var ra in collection.GetAllAsync(filter: filter, cancellationToken: ct))
            {
                var data = ra.Data;
                var raScope = data.Scope ?? "";
                if (!keep(raScope) || target.ContainsKey(data.Id.ToString()))
                {
                    continue;
                }
                target[data.Id.ToString()] = new RoleAssignmentRecord(
                    data.Id.ToString(), raScope, data.RoleDefinitionId?.ToString() ?? "",
                    data.PrincipalId?.ToString() ?? "", data.PrincipalType?.ToString() ?? "Unknown",
                    data.Description, data.CreatedOn?.UtcDateTime);
            }
        }
        catch (RequestFailedException ex)
        {
            ctx.Warn($"Role assignments at {scope} are not readable: {ex.Message.Split('\n')[0]}");
        }
    }

    private async Task ReadPimAsync(ScanContext ctx, string scope, string type, bool atScope,
        Dictionary<string, JsonElement> target, CancellationToken ct)
    {
        var url = $"{scope}/providers/Microsoft.Authorization/{type}?api-version={PimApi}" + (atScope ? "&$filter=atScope()" : "");
        try
        {
            await foreach (var item in rest.GetPagedAsync(url, ct))
            {
                var id = item.Str("id");
                if (id is not null)
                {
                    target.TryAdd(id, item);
                }
            }
        }
        catch (ArmRequestException ex)
        {
            ctx.Warn($"PIM {type} at {scope}: {ex.Message}");
        }
    }

    private static Dictionary<string, string> LatestJustificationByTarget(IEnumerable<JsonElement> requests, string targetProperty) =>
        requests
            .Where(r => !string.IsNullOrWhiteSpace(r.Str("properties", "justification")) && r.Str("properties", targetProperty) is not null)
            .GroupBy(r => r.Str("properties", targetProperty)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.Date("properties", "createdOn")).First().Str("properties", "justification")!,
                StringComparer.OrdinalIgnoreCase);

    private static bool IsSuccessfulRequest(string? status)
    {
        if (string.IsNullOrEmpty(status)) return true;
        string[] failed = ["Denied", "Failed", "Cancel", "Revoked", "Invalid", "PendingApproval", "TimedOut", "Rejected"];
        return !failed.Any(f => status.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static RawAssignment NewArmAssignment(ScanContext ctx, string scope, string roleDefinitionId, string principalId,
        string principalType, AssignmentType type, DateTime? start, DateTime? end, string? justification,
        string? sourceId, string? eligibilityScheduleId, JsonElement? expanded)
    {
        var level = ClassifyScope(scope);
        var subscriptionId = SubscriptionOf(scope);
        var roleKey = Ids.RoleKeyFromArmId(roleDefinitionId);
        ctx.AzureRoles.TryGetValue(roleKey, out var role);

        var raw = new RawAssignment
        {
            ScopeLevel = level,
            ScopeId = scope,
            ScopeDisplayName = ScopeDisplayName(ctx, scope, level),
            SubscriptionId = subscriptionId,
            SubscriptionName = subscriptionId is null
                ? null
                : ctx.Subscriptions.FirstOrDefault(s => s.SubscriptionId.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? subscriptionId,
            RoleDefinitionId = roleDefinitionId,
            RoleKey = roleKey,
            RoleName = role?.Name ?? expanded?.Str("roleDefinition", "displayName") ?? $"Unknown role ({roleKey})",
            Role = role,
            AssignmentType = type,
            StartUtc = start,
            EndUtc = end,
            Justification = justification,
            PrincipalId = Ids.ObjectId(principalId),
            PrincipalType = NormalizePrincipalType(principalType),
            PrincipalDisplayName = expanded?.Str("principal", "displayName"),
            PrincipalUpn = expanded?.Str("principal", "userPrincipalName"),
            SourceId = sourceId,
            EligibilityScheduleId = eligibilityScheduleId
        };
        return raw;
    }

    public static string NormalizePrincipalType(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "user" => "User",
        "group" => "Group",
        "serviceprincipal" => "ServicePrincipal",
        "foreigngroup" => "ForeignGroup",
        "device" => "Device",
        _ => string.IsNullOrEmpty(type) ? "Unknown" : type
    };

    public static ScopeLevel ClassifyScope(string scope)
    {
        if (scope == "/" || scope.StartsWith("/providers/Microsoft.Management/managementGroups/", StringComparison.OrdinalIgnoreCase))
        {
            return ScopeLevel.ManagementGroup;
        }
        var parts = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2) return ScopeLevel.Subscription;
        if (parts.Length == 4 && parts[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase)) return ScopeLevel.ResourceGroup;
        return ScopeLevel.Resource;
    }

    public static string? SubscriptionOf(string scope)
    {
        var parts = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    private static string ScopeDisplayName(ScanContext ctx, string scope, ScopeLevel level)
    {
        if (scope == "/") return "Root (/)";
        var parts = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (level)
        {
            case ScopeLevel.ManagementGroup:
                return ctx.ManagementGroups.FirstOrDefault(m => m.Id.Equals(scope, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? parts[^1];
            case ScopeLevel.Subscription:
                return ctx.Subscriptions.FirstOrDefault(s => s.SubscriptionId.Equals(parts[1], StringComparison.OrdinalIgnoreCase))?.DisplayName ?? parts[1];
            case ScopeLevel.ResourceGroup:
                return parts[3];
            default:
                var type = ctx.ResourceTypes.TryGetValue(scope, out var t) ? t : ResourceTypeFromPath(parts);
                return $"{parts[^1]} ({type})";
        }
    }

    private static string ResourceTypeFromPath(string[] parts)
    {
        var idx = Array.FindLastIndex(parts, p => p.Equals("providers", StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx + 2 >= parts.Length) return "resource";
        var segments = new List<string> { parts[idx + 1] };
        for (var i = idx + 2; i < parts.Length - 1; i += 2)
        {
            segments.Add(parts[i]);
        }
        return string.Join('/', segments);
    }

    private sealed record RoleAssignmentRecord(
        string Id, string Scope, string RoleDefinitionId, string PrincipalId, string PrincipalType, string? Description, DateTime? CreatedOn);
}
