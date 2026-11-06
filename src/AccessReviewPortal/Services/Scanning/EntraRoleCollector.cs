using AccessReviewPortal.Domain;
using AccessReviewPortal.Services.Azure;
using Microsoft.Graph.Models;
using AssignmentType = AccessReviewPortal.Domain.AssignmentType;

namespace AccessReviewPortal.Services.Scanning;

/// <summary>
/// Reads Microsoft Entra ID role assignments: PIM active and eligible schedule instances (with fallback to
/// classic role assignments when PIM is not available) plus PIM requests for justification and activations.
/// </summary>
public sealed class EntraRoleCollector(AzureClients clients)
{
    public async Task CollectAsync(ScanContext ctx, CancellationToken ct)
    {
        var graph = clients.Graph;

        var defsPage = await graph.RoleManagement.Directory.RoleDefinitions.GetAsync(r =>
            r.QueryParameters.Select = ["id", "displayName", "isBuiltIn", "templateId", "rolePermissions"], ct);
        var definitions = await graph.ReadAllAsync<UnifiedRoleDefinition, UnifiedRoleDefinitionCollectionResponse>(defsPage, ct);
        foreach (var d in definitions.Where(d => d.Id is not null))
        {
            var actions = d.RolePermissions?.SelectMany(p => p.AllowedResourceActions ?? []).ToList() ?? [];
            var info = new RoleDefinitionInfo
            {
                Key = d.Id!,
                Id = d.Id!,
                Name = d.DisplayName ?? d.Id!,
                IsBuiltIn = d.IsBuiltIn ?? false,
                HasWritePermissions = PermissionMatcher.EntraRoleHasWrite(actions)
            };
            ctx.EntraRoles[d.Id!] = info;
            if (!string.IsNullOrEmpty(d.TemplateId))
            {
                ctx.EntraRoles.TryAdd(d.TemplateId, info);
            }
        }

        var auNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var active = new List<UnifiedRoleAssignmentScheduleInstance>();
        var eligible = new List<UnifiedRoleEligibilityScheduleInstance>();
        var pimAvailable = true;

        try
        {
            var activePage = await graph.RoleManagement.Directory.RoleAssignmentScheduleInstances.GetAsync(r =>
                r.QueryParameters.Expand = ["principal"], ct);
            active = await graph.ReadAllAsync<UnifiedRoleAssignmentScheduleInstance, UnifiedRoleAssignmentScheduleInstanceCollectionResponse>(activePage, ct);

            var eligiblePage = await graph.RoleManagement.Directory.RoleEligibilityScheduleInstances.GetAsync(r =>
                r.QueryParameters.Expand = ["principal"], ct);
            eligible = await graph.ReadAllAsync<UnifiedRoleEligibilityScheduleInstance, UnifiedRoleEligibilityScheduleInstanceCollectionResponse>(eligiblePage, ct);
        }
        catch (Exception ex) when (ex.StatusCode() is 400 or 403 or 404)
        {
            pimAvailable = false;
            ctx.Warn($"Entra PIM schedule instances are not available ({ex.ShortMessage()}). Falling back to classic role assignments.");
        }

        var justificationBySchedule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (pimAvailable)
        {
            await ReadRequestsAsync(ctx, justificationBySchedule, ct);
        }

        var count = 0;
        if (pimAvailable)
        {
            foreach (var i in active)
            {
                if (!string.Equals(i.MemberType, "Direct", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(i.MemberType))
                {
                    continue;
                }
                if (string.Equals(i.AssignmentType, "Activated", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.PimActivations.Add(new PimActivation(Ids.ObjectId(i.PrincipalId), i.RoleDefinitionId ?? "", i.DirectoryScopeId,
                        i.StartDateTime?.UtcDateTime ?? ctx.NowUtc, null, null, "Active PIM activation (Entra)"));
                    continue;
                }

                var type = i.EndDateTime.HasValue ? AssignmentType.PimActive : AssignmentType.Permanent;
                var justification = i.RoleAssignmentScheduleId is not null && justificationBySchedule.TryGetValue(i.RoleAssignmentScheduleId, out var j) ? j : null;
                ctx.Assignments.Add(await NewEntraAssignmentAsync(ctx, i.RoleDefinitionId, i.DirectoryScopeId, i.Principal, i.PrincipalId, type,
                    i.StartDateTime, i.EndDateTime, justification, i.RoleAssignmentOriginId ?? i.Id, null, auNames, ct));
                count++;
            }

            foreach (var i in eligible)
            {
                if (!string.Equals(i.MemberType, "Direct", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(i.MemberType))
                {
                    continue;
                }
                var justification = i.RoleEligibilityScheduleId is not null && justificationBySchedule.TryGetValue(i.RoleEligibilityScheduleId, out var j) ? j : null;
                ctx.Assignments.Add(await NewEntraAssignmentAsync(ctx, i.RoleDefinitionId, i.DirectoryScopeId, i.Principal, i.PrincipalId,
                    AssignmentType.PimEligible, i.StartDateTime, i.EndDateTime, justification, i.Id, i.RoleEligibilityScheduleId, auNames, ct));
                count++;
            }
        }
        else
        {
            var page = await graph.RoleManagement.Directory.RoleAssignments.GetAsync(r => r.QueryParameters.Expand = ["principal"], ct);
            var assignments = await graph.ReadAllAsync<UnifiedRoleAssignment, UnifiedRoleAssignmentCollectionResponse>(page, ct);
            foreach (var a in assignments)
            {
                ctx.Assignments.Add(await NewEntraAssignmentAsync(ctx, a.RoleDefinitionId, a.DirectoryScopeId, a.Principal, a.PrincipalId,
                    AssignmentType.Permanent, null, null, null, a.Id, null, auNames, ct));
                count++;
            }
        }

        ctx.Info($"Entra ID: {count} role assignments ({(pimAvailable ? "PIM schedules" : "classic assignments")}), {definitions.Count} role definitions.");
    }

    /// <summary>
    /// PIM requests hold the justification and the self-activation history. Listing them needs
    /// RoleAssignmentSchedule.ReadWrite.Directory / RoleEligibilitySchedule.ReadWrite.Directory (Graph requirement).
    /// Without these permissions the directory audit log is used instead (see DirectoryAuditReader).
    /// </summary>
    private async Task ReadRequestsAsync(ScanContext ctx, Dictionary<string, string> justificationBySchedule, CancellationToken ct)
    {
        var graph = clients.Graph;
        try
        {
            var page = await graph.RoleManagement.Directory.RoleAssignmentScheduleRequests.GetAsync(cancellationToken: ct);
            var requests = await graph.ReadAllAsync<UnifiedRoleAssignmentScheduleRequest, UnifiedRoleAssignmentScheduleRequestCollectionResponse>(page, ct);
            foreach (var r in requests.OrderBy(r => r.CreatedDateTime))
            {
                var action = r.Action?.ToString() ?? "";
                if (action.Equals("SelfActivate", StringComparison.OrdinalIgnoreCase) && r.CreatedDateTime.HasValue && IsSuccessful(r.Status))
                {
                    ctx.PimActivations.Add(new PimActivation(Ids.ObjectId(r.PrincipalId), r.RoleDefinitionId ?? "", r.DirectoryScopeId,
                        r.CreatedDateTime.Value.UtcDateTime, null, r.Justification, "PIM activation request (Entra)"));
                }
                else if (action.StartsWith("Admin", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Justification) && r.TargetScheduleId is not null)
                {
                    justificationBySchedule[r.TargetScheduleId] = r.Justification!;
                }
            }

            var ePage = await graph.RoleManagement.Directory.RoleEligibilityScheduleRequests.GetAsync(cancellationToken: ct);
            var eRequests = await graph.ReadAllAsync<UnifiedRoleEligibilityScheduleRequest, UnifiedRoleEligibilityScheduleRequestCollectionResponse>(ePage, ct);
            foreach (var r in eRequests.OrderBy(r => r.CreatedDateTime))
            {
                if (!string.IsNullOrWhiteSpace(r.Justification) && r.TargetScheduleId is not null)
                {
                    justificationBySchedule[r.TargetScheduleId] = r.Justification!;
                }
            }

            ctx.EntraPimRequestsAvailable = true;
            ctx.UsageSources.Add("Entra PIM activations: Graph PIM requests");
        }
        catch (Exception ex) when (ex.StatusCode() is 400 or 403)
        {
            ctx.EntraPimRequestsAvailable = false;
            ctx.Info("Entra PIM requests are not readable with read-only permissions (Graph needs *.ReadWrite.Directory). " +
                     "Activations and justification are taken from the directory audit log instead.");
        }
    }

    private static bool IsSuccessful(string? status) =>
        string.IsNullOrEmpty(status) ||
        !(status.Contains("Denied", StringComparison.OrdinalIgnoreCase) || status.Contains("Failed", StringComparison.OrdinalIgnoreCase)
          || status.Contains("Cancel", StringComparison.OrdinalIgnoreCase) || status.Contains("Revoked", StringComparison.OrdinalIgnoreCase)
          || status.Contains("PendingApproval", StringComparison.OrdinalIgnoreCase));

    private async Task<RawAssignment> NewEntraAssignmentAsync(ScanContext ctx, string? roleDefinitionId, string? directoryScopeId,
        DirectoryObject? principal, string? principalId, AssignmentType type, DateTimeOffset? start, DateTimeOffset? end,
        string? justification, string? sourceId, string? eligibilityScheduleId, Dictionary<string, string> auNames, CancellationToken ct)
    {
        ctx.EntraRoles.TryGetValue(roleDefinitionId ?? "", out var role);
        var scope = string.IsNullOrEmpty(directoryScopeId) ? "/" : directoryScopeId;
        var raw = new RawAssignment
        {
            ScopeLevel = ScopeLevel.EntraRole,
            ScopeId = scope,
            ScopeDisplayName = await DirectoryScopeNameAsync(scope, auNames, ct),
            RoleDefinitionId = roleDefinitionId ?? "",
            RoleKey = roleDefinitionId ?? "",
            RoleName = role?.Name ?? $"Unknown role ({roleDefinitionId})",
            Role = role,
            AssignmentType = type,
            StartUtc = start?.UtcDateTime,
            EndUtc = end?.UtcDateTime,
            Justification = justification,
            PrincipalId = Ids.ObjectId(principalId ?? principal?.Id),
            PrincipalType = PrincipalTypeOf(principal),
            SourceId = sourceId,
            EligibilityScheduleId = eligibilityScheduleId
        };

        switch (principal)
        {
            case User u:
                raw.PrincipalDisplayName = u.DisplayName;
                raw.PrincipalUpn = u.UserPrincipalName;
                break;
            case Group g:
                raw.PrincipalDisplayName = g.DisplayName;
                break;
            case ServicePrincipal sp:
                raw.PrincipalDisplayName = sp.DisplayName;
                break;
        }
        return raw;
    }

    private async Task<string> DirectoryScopeNameAsync(string scope, Dictionary<string, string> auNames, CancellationToken ct)
    {
        if (scope == "/") return "Directory (tenant-wide)";
        const string auPrefix = "/administrativeUnits/";
        if (!scope.StartsWith(auPrefix, StringComparison.OrdinalIgnoreCase)) return $"Application scope {scope.TrimStart('/')}";

        var id = scope[auPrefix.Length..];
        if (auNames.TryGetValue(id, out var cached)) return cached;
        try
        {
            var au = await clients.Graph.Directory.AdministrativeUnits[id].GetAsync(r => r.QueryParameters.Select = ["displayName"], ct);
            cached = $"Administrative unit: {au?.DisplayName ?? id}";
        }
        catch (Exception)
        {
            cached = $"Administrative unit: {id}";
        }
        auNames[id] = cached;
        return cached;
    }

    public static string PrincipalTypeOf(DirectoryObject? o) => o switch
    {
        User => "User",
        Group => "Group",
        ServicePrincipal => "ServicePrincipal",
        null => "Unknown",
        _ => (o.OdataType ?? "Unknown").Replace("#microsoft.graph.", "", StringComparison.OrdinalIgnoreCase)
    };
}
