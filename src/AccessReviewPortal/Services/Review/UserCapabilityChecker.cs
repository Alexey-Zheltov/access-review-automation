using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Azure;
using AccessReviewPortal.Services.Identity;
using AccessReviewPortal.Services.Scanning;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Graph.Models;
using AssignmentType = AccessReviewPortal.Domain.AssignmentType;

namespace AccessReviewPortal.Services.Review;

/// <summary>Can the signed-in reviewer remove this assignment himself, and if not, which role is missing.</summary>
public sealed record RevokeCapability(bool CanRevoke, string Reason, string[]? ConsentScopes = null)
{
    public bool NeedsConsent => ConsentScopes is { Length: > 0 };
}

/// <summary>What has to be removed to take away the access of one review row or group member decision.</summary>
public enum RevokeTargetKind
{
    AzureRoleAssignment,
    EntraRoleAssignment,
    CloudGroupMembership,
    OnPremisesGroupMembership
}

/// <summary>
/// Checks the rights of the signed-in reviewer "on the fly", with his own delegated token:
/// - Azure RBAC / PIM for Azure resources: ARM permissions API on the scope of the assignment
///   (Microsoft.Authorization/roleAssignments/delete, roleAssignmentScheduleRequests/write, roleEligibilityScheduleRequests/write);
/// - Entra ID roles: active Privileged Role Administrator or Global Administrator;
/// - group membership: group owner, Groups Administrator / User Administrator, or Privileged Role Administrator
///   for role-assignable groups. Groups synced from on-premises AD are changed in AD only.
/// </summary>
public sealed class UserCapabilityChecker(UserTokenService tokens, ArmRestClient arm, AzureClients clients, IMemoryCache cache, ILogger<UserCapabilityChecker> logger)
{
    private const string AzureRoleHint = "Owner, User Access Administrator or Role Based Access Control Administrator (active) on the scope";
    private const string EntraRoleHint = "Privileged Role Administrator or Global Administrator (active) in Entra ID";

    public static RevokeTargetKind KindOf(RoleAssignmentSnapshot row) =>
        row.AssignedViaGroupId is not null
            ? row.AssignedViaGroupOnPremises ? RevokeTargetKind.OnPremisesGroupMembership : RevokeTargetKind.CloudGroupMembership
            : row.ScopeLevel == ScopeLevel.EntraRole ? RevokeTargetKind.EntraRoleAssignment : RevokeTargetKind.AzureRoleAssignment;

    public static string RequiredAzureAction(AssignmentType type) => type switch
    {
        AssignmentType.PimEligible => "Microsoft.Authorization/roleEligibilityScheduleRequests/write",
        AssignmentType.PimActive => "Microsoft.Authorization/roleAssignmentScheduleRequests/write",
        _ => "Microsoft.Authorization/roleAssignments/delete"
    };

    public static string[] GraphScopesFor(RoleAssignmentSnapshot row) => KindOf(row) switch
    {
        RevokeTargetKind.EntraRoleAssignment => row.AssignmentType == AssignmentType.PimEligible
            ? UserTokenService.GraphEligibilityScheduleScopes
            : UserTokenService.GraphAssignmentScheduleScopes,
        RevokeTargetKind.CloudGroupMembership => UserTokenService.GraphGroupMemberScopes,
        _ => []
    };

    /// <summary>Capabilities for review rows (keyed by row id) and group member decisions (keyed by "group|member").</summary>
    public async Task<CapabilityReport> CheckAsync(
        ClaimsPrincipal user,
        IReadOnlyCollection<RoleAssignmentSnapshot> rows,
        IReadOnlyCollection<GroupMemberDecision> memberDecisions,
        IReadOnlyDictionary<string, GroupSnapshot> groups,
        CancellationToken ct)
    {
        var report = new CapabilityReport();
        if (!user.CanUseDelegatedTokens())
        {
            var reason = "Sign in with Microsoft Entra ID to revoke. The access key mode allows review only (use the PowerShell script).";
            foreach (var r in rows) report.Rows[r.Id] = new RevokeCapability(false, reason);
            foreach (var d in memberDecisions) report.Members[MemberKey(d.GroupId, d.MemberId)] = new RevokeCapability(false, reason);
            return report;
        }

        var oid = user.GetUserObjectId()!;
        report.UserUpn = user.GetUpn();

        // Azure RBAC: one permissions call per distinct scope, with the user's own ARM token.
        var azureRows = rows.Where(r => KindOf(r) == RevokeTargetKind.AzureRoleAssignment).ToList();
        if (azureRows.Count > 0)
        {
            var permissions = await GetScopePermissionsAsync(user, oid, azureRows.Select(r => r.ScopeId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), report, ct);
            foreach (var row in azureRows)
            {
                report.Rows[row.Id] = EvaluateAzure(row, permissions);
            }
        }

        // Entra ID roles and group membership need the user's Entra roles (read with the scanner's read-only permission).
        var needsEntra = rows.Any(r => KindOf(r) is RevokeTargetKind.EntraRoleAssignment or RevokeTargetKind.CloudGroupMembership)
                         || memberDecisions.Count > 0;
        if (!needsEntra)
        {
            return report;
        }

        var entraRoles = await GetActiveEntraRolesAsync(oid, ct);
        report.UserEntraRoles = entraRoles.Names;

        var consentCache = new Dictionary<string, string[]?>();
        async Task<string[]?> ConsentNeeded(string[] scopes)
        {
            var key = string.Join(' ', scopes);
            if (!consentCache.TryGetValue(key, out var result))
            {
                try
                {
                    await tokens.GetAccessTokenAsync(user, scopes, ct);
                    result = null;
                }
                catch (UserConsentRequiredException)
                {
                    result = scopes;
                }
                consentCache[key] = result;
            }
            return result;
        }

        foreach (var row in rows.Where(r => KindOf(r) == RevokeTargetKind.EntraRoleAssignment))
        {
            report.Rows[row.Id] = await WithConsentAsync(EvaluateEntraRole(row, entraRoles), GraphScopesFor(row), ConsentNeeded);
        }

        foreach (var row in rows.Where(r => KindOf(r) is RevokeTargetKind.CloudGroupMembership or RevokeTargetKind.OnPremisesGroupMembership))
        {
            groups.TryGetValue(row.AssignedViaGroupId!, out var group);
            report.Rows[row.Id] = await WithConsentAsync(EvaluateGroup(group, row.AssignedViaGroupOnPremises, row.AssignedViaGroupName, oid, entraRoles),
                UserTokenService.GraphGroupMemberScopes, ConsentNeeded);
        }

        foreach (var decision in memberDecisions)
        {
            groups.TryGetValue(decision.GroupId, out var group);
            report.Members[MemberKey(decision.GroupId, decision.MemberId)] = await WithConsentAsync(
                EvaluateGroup(group, group?.OnPremisesSyncEnabled ?? false, group?.DisplayName, oid, entraRoles),
                UserTokenService.GraphGroupMemberScopes, ConsentNeeded);
        }
        return report;
    }

    public static string MemberKey(string groupId, string memberId) => $"{groupId}|{memberId}".ToLowerInvariant();

    private static async Task<RevokeCapability> WithConsentAsync(RevokeCapability capability, string[] scopes, Func<string[], Task<string[]?>> consentNeeded)
    {
        if (!capability.CanRevoke || scopes.Length == 0) return capability;
        var missing = await consentNeeded(scopes);
        return missing is null
            ? capability
            : capability with { CanRevoke = false, Reason = "Consent is needed for " + string.Join(", ", missing.Select(s => s.Split('/').Last())), ConsentScopes = missing };
    }

    private static RevokeCapability EvaluateAzure(RoleAssignmentSnapshot row, IReadOnlyDictionary<string, ScopePermissions> permissions)
    {
        if (row.ScopeId == "/")
        {
            return new RevokeCapability(false, "Assignments at the root scope (/) need elevated Global Administrator access. Use the script.");
        }
        if (row.AssignmentType == AssignmentType.Permanent && string.IsNullOrEmpty(row.SourceAssignmentId))
        {
            return new RevokeCapability(false, "The role assignment id is unknown. Use the script.");
        }
        if (!permissions.TryGetValue(Ids.NormalizeScope(row.ScopeId), out var p))
        {
            return new RevokeCapability(false, "Permissions on this scope could not be read.");
        }
        if (p.ConsentScopes is not null)
        {
            return new RevokeCapability(false, "Consent for Azure Resource Manager (user_impersonation) is needed.", p.ConsentScopes);
        }
        if (p.Error is not null)
        {
            return new RevokeCapability(false, $"Cannot check your rights on this scope: {p.Error}");
        }

        var action = RequiredAzureAction(row.AssignmentType);
        return p.Allows(action)
            ? new RevokeCapability(true, $"Allowed: you have {action} on this scope.")
            : new RevokeCapability(false, $"Requires {AzureRoleHint} ({action}).");
    }

    private static RevokeCapability EvaluateEntraRole(RoleAssignmentSnapshot row, EntraRoleSet roles)
    {
        if (roles.Error is not null)
        {
            return new RevokeCapability(false, $"Your Entra ID roles could not be read: {roles.Error}");
        }
        if (row.ScopeId != "/" && !row.ScopeId.StartsWith("/administrativeUnits/", StringComparison.OrdinalIgnoreCase))
        {
            return new RevokeCapability(false, "Application-scoped Entra role assignments are not supported. Use the script.");
        }
        return roles.Has(WellKnownIds.PrivilegedRoleAdministrator) || roles.Has(WellKnownIds.GlobalAdministrator)
            ? new RevokeCapability(true, "Allowed: you have an active Privileged Role Administrator / Global Administrator role.")
            : new RevokeCapability(false, $"Requires {EntraRoleHint}.");
    }

    private static RevokeCapability EvaluateGroup(GroupSnapshot? group, bool onPremises, string? groupName, string userObjectId, EntraRoleSet roles)
    {
        if (onPremises)
        {
            return new RevokeCapability(false, $"Group '{groupName}' is synced from on-premises AD. Change the membership in AD (see the script).");
        }
        if (group?.OwnerIds is { } owners && owners.Split(',').Contains(userObjectId, StringComparer.OrdinalIgnoreCase))
        {
            return new RevokeCapability(true, $"Allowed: you are an owner of group '{groupName}'.");
        }
        if (roles.Error is not null)
        {
            return new RevokeCapability(false, $"Your Entra ID roles could not be read: {roles.Error}");
        }
        if (roles.Has(WellKnownIds.GlobalAdministrator) || roles.Has(WellKnownIds.PrivilegedRoleAdministrator))
        {
            return new RevokeCapability(true, "Allowed: you have an active Privileged Role Administrator / Global Administrator role.");
        }
        if (group is { IsAssignableToRole: true })
        {
            return new RevokeCapability(false, $"Group '{groupName}' is role-assignable: requires group owner or {EntraRoleHint}.");
        }
        return roles.Has(WellKnownIds.GroupsAdministrator) || roles.Has(WellKnownIds.UserAdministrator)
            ? new RevokeCapability(true, "Allowed: you have an active Groups Administrator / User Administrator role.")
            : new RevokeCapability(false, $"Requires group owner, Groups Administrator or User Administrator (active) for group '{groupName}'.");
    }

    private async Task<Dictionary<string, ScopePermissions>> GetScopePermissionsAsync(
        ClaimsPrincipal user, string oid, List<string> scopes, CapabilityReport report, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<string, ScopePermissions>(StringComparer.Ordinal);
        try
        {
            await tokens.GetAccessTokenAsync(user, UserTokenService.ArmScopes, ct);
        }
        catch (UserConsentRequiredException ex)
        {
            report.ConsentException ??= ex;
            foreach (var s in scopes) result[Ids.NormalizeScope(s)] = new ScopePermissions { ConsentScopes = UserTokenService.ArmScopes };
            return new Dictionary<string, ScopePermissions>(result);
        }

        var userArm = arm.WithUserToken(token => tokens.GetAccessTokenAsync(user, UserTokenService.ArmScopes, token));
        await Parallel.ForEachAsync(scopes, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (scope, token) =>
        {
            var key = Ids.NormalizeScope(scope);
            if (key == "/")
            {
                result[key] = new ScopePermissions();
                return;
            }

            var cacheKey = $"perm:{oid}:{key}";
            if (cache.TryGetValue(cacheKey, out ScopePermissions? cached) && cached is not null)
            {
                result[key] = cached;
                return;
            }

            var permissions = new ScopePermissions();
            try
            {
                foreach (var block in await userArm.GetAllAsync($"{scope}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01", token))
                {
                    permissions.Blocks.Add(new PermissionBlock
                    {
                        Actions = Strings(block, "actions"),
                        NotActions = Strings(block, "notActions"),
                        DataActions = Strings(block, "dataActions"),
                        NotDataActions = Strings(block, "notDataActions")
                    });
                }
            }
            catch (ArmRequestException ex)
            {
                permissions.Error = ex.Status == System.Net.HttpStatusCode.NotFound ? "the scope no longer exists" : ex.Message;
            }
            catch (UserConsentRequiredException ex)
            {
                report.ConsentException ??= ex;
                permissions.ConsentScopes = UserTokenService.ArmScopes;
            }

            if (permissions.Error is null && permissions.ConsentScopes is null)
            {
                cache.Set(cacheKey, permissions, TimeSpan.FromMinutes(5));
            }
            result[key] = permissions;
        });
        return new Dictionary<string, ScopePermissions>(result);
    }

    /// <summary>Active Entra ID roles of the user at directory scope, directly or through role-assignable groups.</summary>
    private async Task<EntraRoleSet> GetActiveEntraRolesAsync(string oid, CancellationToken ct)
    {
        var cacheKey = $"entra-roles:{oid}";
        if (cache.TryGetValue(cacheKey, out EntraRoleSet? cached) && cached is not null) return cached;

        var set = new EntraRoleSet();
        try
        {
            var principals = new List<string> { oid };
            var groupsPage = await clients.Graph.Users[oid].TransitiveMemberOf.GraphGroup.GetAsync(r =>
            {
                r.QueryParameters.Select = ["id", "isAssignableToRole"];
                r.QueryParameters.Top = 999;
            }, ct);
            var groups = await clients.Graph.ReadAllAsync<Group, GroupCollectionResponse>(groupsPage, ct);
            principals.AddRange(groups.Where(g => g.IsAssignableToRole == true && g.Id is not null).Select(g => g.Id!));

            var definitions = await clients.Graph.RoleManagement.Directory.RoleDefinitions.GetAsync(r => r.QueryParameters.Select = ["id", "templateId", "displayName"], cancellationToken: ct);
            var names = (definitions?.Value ?? []).Where(d => d.Id is not null)
                .ToDictionary(d => d.Id!, d => (d.TemplateId ?? d.Id!, d.DisplayName ?? d.Id!), StringComparer.OrdinalIgnoreCase);

            foreach (var principal in principals)
            {
                IEnumerable<(string RoleId, string? Scope)> active;
                try
                {
                    var page = await clients.Graph.RoleManagement.Directory.RoleAssignmentScheduleInstances.GetAsync(r =>
                        r.QueryParameters.Filter = $"principalId eq '{principal}'", ct);
                    active = (page?.Value ?? []).Select(i => (i.RoleDefinitionId ?? "", i.DirectoryScopeId));
                }
                catch (Exception ex) when (ex.StatusCode() is 400 or 403 or 404)
                {
                    // Tenants without PIM: classic role assignments.
                    var page = await clients.Graph.RoleManagement.Directory.RoleAssignments.GetAsync(r =>
                        r.QueryParameters.Filter = $"principalId eq '{principal}'", ct);
                    active = (page?.Value ?? []).Select(i => (i.RoleDefinitionId ?? "", i.DirectoryScopeId));
                }

                foreach (var (roleId, scope) in active.Where(a => a.Scope is null or "/"))
                {
                    var (template, name) = names.TryGetValue(roleId, out var n) ? n : (roleId, roleId);
                    set.TemplateIds.Add(template);
                    set.Names.Add(name);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Cannot read Entra roles of {Oid}", oid);
            set.Error = ex.ShortMessage();
        }

        cache.Set(cacheKey, set, TimeSpan.FromMinutes(set.Error is null ? 5 : 1));
        return set;
    }

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];

    private sealed class ScopePermissions
    {
        public List<PermissionBlock> Blocks { get; } = [];
        public string? Error { get; set; }
        public string[]? ConsentScopes { get; set; }

        public bool Allows(string action) =>
            Blocks.Any(b => b.Actions.Any(a => PermissionMatcher.Matches(a, action)) && !b.NotActions.Any(n => PermissionMatcher.Matches(n, action)));
    }

    private sealed class EntraRoleSet
    {
        public HashSet<string> TemplateIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Error { get; set; }
        public bool Has(string templateId) => TemplateIds.Contains(templateId);
    }
}

public sealed class CapabilityReport
{
    public Dictionary<long, RevokeCapability> Rows { get; } = [];
    public Dictionary<string, RevokeCapability> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? UserUpn { get; set; }
    public IReadOnlyCollection<string> UserEntraRoles { get; set; } = [];

    /// <summary>First consent exception, for MicrosoftIdentityConsentAndConditionalAccessHandler.</summary>
    public UserConsentRequiredException? ConsentException { get; set; }

    public string[] ConsentScopes =>
        Rows.Values.Concat(Members.Values).Where(c => c.NeedsConsent).SelectMany(c => c.ConsentScopes!).Distinct().ToArray();
}
