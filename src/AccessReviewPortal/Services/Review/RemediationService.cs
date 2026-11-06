using System.Security.Claims;
using System.Text;
using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Services.Azure;
using AccessReviewPortal.Services.Identity;
using AccessReviewPortal.Services.Scanning;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Authentication;
using AssignmentType = AccessReviewPortal.Domain.AssignmentType;

namespace AccessReviewPortal.Services.Review;

public sealed record RevokeResult(string Target, string Principal, string Role, bool Success, string Message);

/// <summary>
/// Removes approved assignments ONLY with the delegated token of the signed-in reviewer. The scanner identity has
/// read-only rights and is never used for changes. Before each change the rights of the user are checked again
/// on the server (UserCapabilityChecker). Every result is stored with RemediatedByUpn / RemediatedAtUtc.
/// Also builds a PowerShell script for reviewers who prefer to run the changes themselves.
/// </summary>
public sealed class RemediationService(
    ArmRestClient arm,
    UserTokenService tokens,
    UserCapabilityChecker capabilities,
    SnapshotService snapshots,
    IHttpClientFactory httpClientFactory,
    ILogger<RemediationService> logger)
{
    private const string Justification = "Quarterly access review";

    /// <summary>Revokes every approved removal of the snapshot that the user is allowed to change.</summary>
    public async Task<List<RevokeResult>> ExecuteAsync(ClaimsPrincipal user, int snapshotId, CancellationToken ct)
    {
        if (!user.CanUseDelegatedTokens())
        {
            return [new RevokeResult("-", "-", "-", false, "Revoke needs Microsoft Entra ID sign-in. Use the PowerShell script.")];
        }

        var upn = user.GetUpn();
        var rows = (await snapshots.GetRowsAsync(snapshotId, new ReviewFilter { Decision = "Removed" }, ct))
            .Where(r => !IsDone(r.RemediationStatus)).ToList();
        var memberDecisions = (await snapshots.GetMemberDecisionsAsync(snapshotId, ct))
            .Where(d => d.Decision == GroupMemberDecisionType.RemoveFromGroup && !IsDone(d.RemediationStatus)).ToList();
        var groups = await snapshots.GetGroupsAsync(snapshotId, ct);

        var report = await capabilities.CheckAsync(user, rows, memberDecisions, groups, ct);
        var results = new List<RevokeResult>();

        foreach (var row in rows.Where(r => UserCapabilityChecker.KindOf(r) is RevokeTargetKind.AzureRoleAssignment or RevokeTargetKind.EntraRoleAssignment))
        {
            var capability = report.Rows.GetValueOrDefault(row.Id) ?? new RevokeCapability(false, "Not checked.");
            if (!capability.CanRevoke)
            {
                results.Add(new RevokeResult(Describe(row), row.PrincipalDisplayName ?? row.PrincipalId, row.RoleName, false, "Skipped: " + capability.Reason));
                continue;
            }

            var (success, message) = await RunAsync(() => UserCapabilityChecker.KindOf(row) == RevokeTargetKind.AzureRoleAssignment
                ? RevokeAzureAsync(user, row, ct)
                : RevokeEntraAsync(user, row, ct));
            await snapshots.SetRemediationAsync([row.Id], Status(success, message), success ? upn : null, ct);
            results.Add(new RevokeResult(Describe(row), row.PrincipalDisplayName ?? row.PrincipalId, row.RoleName, success, message));
        }

        // Group memberships: one removal per (group, member), from review rows ("via group") and group member decisions.
        var memberships = rows.Where(r => UserCapabilityChecker.KindOf(r) is RevokeTargetKind.CloudGroupMembership or RevokeTargetKind.OnPremisesGroupMembership)
            .Select(r => (GroupId: r.AssignedViaGroupId!, MemberId: r.PrincipalId, Name: r.PrincipalDisplayName ?? r.PrincipalId, Group: r.AssignedViaGroupName, Capability: report.Rows.GetValueOrDefault(r.Id)))
            .Concat(memberDecisions.Select(d => (d.GroupId, d.MemberId, Name: d.MemberDisplayName ?? d.MemberId,
                Group: groups.GetValueOrDefault(d.GroupId)?.DisplayName, Capability: report.Members.GetValueOrDefault(UserCapabilityChecker.MemberKey(d.GroupId, d.MemberId)))))
            .GroupBy(m => UserCapabilityChecker.MemberKey(m.GroupId, m.MemberId))
            .Select(g => g.First());

        foreach (var m in memberships)
        {
            var target = $"Group '{m.Group}' member {m.Name}";
            var capability = m.Capability ?? new RevokeCapability(false, "Not checked.");
            if (!capability.CanRevoke)
            {
                results.Add(new RevokeResult(target, m.Name, "group membership", false, "Skipped: " + capability.Reason));
                continue;
            }

            var (success, message) = await RunAsync(() => RemoveGroupMemberAsync(user, m.GroupId, m.MemberId, m.Group, ct));
            var status = Status(success, message);
            var rowIds = rows.Where(r => string.Equals(r.AssignedViaGroupId, m.GroupId, StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(r.PrincipalId, m.MemberId, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).ToList();
            await snapshots.SetRemediationAsync(rowIds, status, success ? upn : null, ct);
            await snapshots.SetMemberRemediationAsync(snapshotId, m.GroupId, m.MemberId, status, success ? upn : null, ct);
            results.Add(new RevokeResult(target, m.Name, "group membership", success, message));
        }

        logger.LogInformation("Revoke by {Upn} on snapshot {Snapshot}: {Ok} done, {Failed} not done.",
            upn, snapshotId, results.Count(r => r.Success), results.Count(r => !r.Success));
        return results;
    }

    private async Task<string> RevokeAzureAsync(ClaimsPrincipal user, RoleAssignmentSnapshot r, CancellationToken ct)
    {
        var userArm = arm.WithUserToken(token => tokens.GetAccessTokenAsync(user, UserTokenService.ArmScopes, token));
        switch (r.AssignmentType)
        {
            case AssignmentType.PimEligible:
            case AssignmentType.PimActive:
                var type = r.AssignmentType == AssignmentType.PimEligible ? "roleEligibilityScheduleRequests" : "roleAssignmentScheduleRequests";
                await userArm.PutJsonAsync($"{r.ScopeId}/providers/Microsoft.Authorization/{type}/{Guid.NewGuid()}?api-version=2020-10-01", new
                {
                    properties = new
                    {
                        principalId = r.PrincipalId,
                        roleDefinitionId = r.RoleDefinitionId,
                        requestType = "AdminRemove",
                        justification = Justification
                    }
                }, ct);
                return "PIM AdminRemove request accepted.";
            default:
                await userArm.DeleteAsync($"{r.SourceAssignmentId}?api-version=2022-04-01", ct);
                return "Role assignment deleted.";
        }
    }

    private async Task<string> RevokeEntraAsync(ClaimsPrincipal user, RoleAssignmentSnapshot r, CancellationToken ct)
    {
        var graph = DelegatedGraph(user, UserCapabilityChecker.GraphScopesFor(r));
        if (r.AssignmentType == AssignmentType.PimEligible)
        {
            await graph.RoleManagement.Directory.RoleEligibilityScheduleRequests.PostAsync(new UnifiedRoleEligibilityScheduleRequest
            {
                Action = UnifiedRoleScheduleRequestActions.AdminRemove,
                PrincipalId = r.PrincipalId,
                RoleDefinitionId = r.RoleDefinitionId,
                DirectoryScopeId = r.ScopeId,
                Justification = Justification
            }, cancellationToken: ct);
        }
        else
        {
            await graph.RoleManagement.Directory.RoleAssignmentScheduleRequests.PostAsync(new UnifiedRoleAssignmentScheduleRequest
            {
                Action = UnifiedRoleScheduleRequestActions.AdminRemove,
                PrincipalId = r.PrincipalId,
                RoleDefinitionId = r.RoleDefinitionId,
                DirectoryScopeId = r.ScopeId,
                Justification = Justification
            }, cancellationToken: ct);
        }
        return "Entra role removal request accepted.";
    }

    private async Task<string> RemoveGroupMemberAsync(ClaimsPrincipal user, string groupId, string memberId, string? groupName, CancellationToken ct)
    {
        var graph = DelegatedGraph(user, UserTokenService.GraphGroupMemberScopes);
        try
        {
            await graph.Groups[groupId].Members[memberId].Ref.DeleteAsync(cancellationToken: ct);
            return $"Removed from group '{groupName}'.";
        }
        catch (Exception ex) when (ex.StatusCode() == 404)
        {
            return $"Already not a member of group '{groupName}'.";
        }
    }

    private GraphServiceClient DelegatedGraph(ClaimsPrincipal user, string[] scopes) =>
        new(httpClientFactory.CreateClient("graph"),
            new BaseBearerTokenAuthenticationProvider(new DelegatedTokenProvider(ct => tokens.GetAccessTokenAsync(user, scopes, ct))));

    private static async Task<(bool Success, string Message)> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return (true, await action());
        }
        catch (ArmRequestException ex)
        {
            return (false, $"Failed: {ex.Message}");
        }
        catch (UserConsentRequiredException ex)
        {
            return (false, $"Failed: {ex.Message}");
        }
        catch (Exception ex) when (ex.StatusCode() > 0)
        {
            return (false, $"Failed: {ex.ShortMessage()}");
        }
    }

    private static string Status(bool success, string message) =>
        success ? $"Revoked {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC: {message}" : message;

    private static bool IsDone(string? status) => AccessChanges.IsDone(status);

    private sealed class DelegatedTokenProvider(Func<CancellationToken, Task<string>> token) : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new(["graph.microsoft.com"]);

        public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default) => token(cancellationToken);
    }

    // ---------------------------------------------------------------- PowerShell script

    public static string BuildScript(ScanSnapshot snapshot, IReadOnlyList<RoleAssignmentSnapshot> rows,
        IReadOnlyList<GroupMemberDecision> memberDecisions, IReadOnlyDictionary<string, GroupSnapshot> groups, string? tenantId)
    {
        var removed = rows.Where(r => r.ReviewerDecision == ReviewerDecision.Removed && !IsDone(r.RemediationStatus)).ToList();
        var azure = removed.Where(r => UserCapabilityChecker.KindOf(r) == RevokeTargetKind.AzureRoleAssignment).ToList();
        var entra = removed.Where(r => UserCapabilityChecker.KindOf(r) == RevokeTargetKind.EntraRoleAssignment).ToList();

        var members = removed.Where(r => r.AssignedViaGroupId is not null)
            .Select(r => new ScriptMember(r.AssignedViaGroupId!, r.AssignedViaGroupName, r.AssignedViaGroupOnPremises, r.AssignedViaGroupSamAccountName,
                r.PrincipalId, r.PrincipalDisplayName, r.PrincipalUpn, $"{r.RoleName} on {r.ScopeDisplayName}. {r.ReviewerComment ?? r.RecommendationReason}"))
            .Concat(memberDecisions.Where(d => d.Decision == GroupMemberDecisionType.RemoveFromGroup && !IsDone(d.RemediationStatus)).Select(d =>
            {
                groups.TryGetValue(d.GroupId, out var g);
                return new ScriptMember(d.GroupId, g?.DisplayName, g?.OnPremisesSyncEnabled ?? false, g?.OnPremisesSamAccountName,
                    d.MemberId, d.MemberDisplayName, d.MemberUpn, "Group member review: Remove from Group. " + d.Comment);
            }))
            .DistinctBy(m => (m.GroupId.ToLowerInvariant(), m.MemberId.ToLowerInvariant()))
            .ToList();
        var cloudGroups = members.Where(m => !m.OnPremises).ToList();
        var adGroups = members.Where(m => m.OnPremises).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<#");
        sb.AppendLine("  Access Review remediation script (generated by the Access Review Automation Portal)");
        sb.AppendLine($"  Snapshot      : #{snapshot.Id} '{C(snapshot.Label)}', scanned {snapshot.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"  Generated     : {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"  Open removals : Azure RBAC {azure.Count}, Entra roles {entra.Count}, group memberships {members.Count}");
        sb.AppendLine();
        sb.AppendLine("  Run it with YOUR account (the portal itself has read-only rights). Test first with -WhatIf:");
        sb.AppendLine("    .\\remediation.ps1 -WhatIf");
        sb.AppendLine("  Azure RBAC  : Owner, User Access Administrator or RBAC Administrator on the scope (module Az.Resources).");
        sb.AppendLine("  Entra roles : Privileged Role Administrator (module Microsoft.Graph.Identity.Governance).");
        sb.AppendLine("  Groups      : cloud groups through Microsoft Graph; groups synced from AD must be changed in on-premises AD.");
        sb.AppendLine("#>");
        sb.AppendLine("[CmdletBinding(SupportsShouldProcess = $true)]");
        sb.AppendLine("param()");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        var tenant = Ps(tenantId ?? snapshot.TenantId ?? "");
        sb.AppendLine();

        if (azure.Count > 0)
        {
            sb.AppendLine("#region Azure RBAC");
            sb.AppendLine($"if (-not (Get-AzContext)) {{ Connect-AzAccount -Tenant {tenant} | Out-Null }}");
            var i = 0;
            foreach (var r in azure)
            {
                sb.AppendLine();
                sb.AppendLine($"# {++i}. {C(Describe(r))}");
                var target = Ps($"{r.PrincipalDisplayName} / {r.RoleName} / {r.ScopeId}");
                var command = r.AssignmentType switch
                {
                    AssignmentType.PimEligible =>
                        $"New-AzRoleEligibilityScheduleRequest -Name (New-Guid).Guid -Scope {Ps(r.ScopeId)} -PrincipalId {Ps(r.PrincipalId)} " +
                        $"-RoleDefinitionId {Ps(r.RoleDefinitionId)} -RequestType AdminRemove -Justification {Ps(Justification)} | Out-Null",
                    AssignmentType.PimActive =>
                        $"New-AzRoleAssignmentScheduleRequest -Name (New-Guid).Guid -Scope {Ps(r.ScopeId)} -PrincipalId {Ps(r.PrincipalId)} " +
                        $"-RoleDefinitionId {Ps(r.RoleDefinitionId)} -RequestType AdminRemove -Justification {Ps(Justification)} | Out-Null",
                    _ => $"Remove-AzRoleAssignment -ObjectId {Ps(r.PrincipalId)} -RoleDefinitionId {Ps(RoleGuid(r.RoleDefinitionId))} -Scope {Ps(r.ScopeId)}"
                };
                sb.AppendLine($"if ($PSCmdlet.ShouldProcess({target}, 'Remove Azure role assignment')) {{");
                sb.AppendLine($"    {command}");
                sb.AppendLine("}");
            }
            sb.AppendLine("#endregion");
            sb.AppendLine();
        }

        if (entra.Count > 0)
        {
            sb.AppendLine("#region Microsoft Entra ID roles");
            sb.AppendLine($"Connect-MgGraph -TenantId {tenant} -Scopes 'RoleAssignmentSchedule.ReadWrite.Directory','RoleEligibilitySchedule.ReadWrite.Directory' -NoWelcome");
            var i = 0;
            foreach (var r in entra)
            {
                sb.AppendLine();
                sb.AppendLine($"# {++i}. {C(Describe(r))}");
                var cmdlet = r.AssignmentType == AssignmentType.PimEligible
                    ? "New-MgRoleManagementDirectoryRoleEligibilityScheduleRequest"
                    : "New-MgRoleManagementDirectoryRoleAssignmentScheduleRequest";
                sb.AppendLine($"if ($PSCmdlet.ShouldProcess({Ps($"{r.PrincipalDisplayName} / {r.RoleName}")}, 'Remove Entra role')) {{");
                sb.AppendLine($"    {cmdlet} -BodyParameter @{{ action = 'adminRemove'; principalId = {Ps(r.PrincipalId)}; " +
                              $"roleDefinitionId = {Ps(r.RoleDefinitionId)}; directoryScopeId = {Ps(r.ScopeId)}; justification = {Ps(Justification)} }} | Out-Null");
                sb.AppendLine("}");
            }
            sb.AppendLine("#endregion");
            sb.AppendLine();
        }

        if (cloudGroups.Count > 0)
        {
            sb.AppendLine("#region Cloud group memberships (access inherited through a group)");
            sb.AppendLine($"if (-not (Get-MgContext)) {{ Connect-MgGraph -TenantId {tenant} -Scopes 'GroupMember.ReadWrite.All' -NoWelcome }}");
            foreach (var m in cloudGroups)
            {
                sb.AppendLine("# " + C($"{m.MemberName} ({m.MemberUpn}) in group '{m.GroupName}'. {m.Reason}"));
                sb.AppendLine($"if ($PSCmdlet.ShouldProcess({Ps($"{m.MemberName} in {m.GroupName}")}, 'Remove group member')) {{");
                sb.AppendLine($"    Remove-MgGroupMemberByRef -GroupId {Ps(m.GroupId)} -DirectoryObjectId {Ps(m.MemberId)}");
                sb.AppendLine("}");
            }
            sb.AppendLine("#endregion");
            sb.AppendLine();
        }

        if (adGroups.Count > 0)
        {
            sb.AppendLine("#region Groups synced from on-premises AD (run on a domain-joined machine with the ActiveDirectory module)");
            sb.AppendLine("# These groups are mastered on-premises. Change membership in AD (or in the identity management system);");
            sb.AppendLine("# the change reaches Entra ID with the next sync cycle.");
            foreach (var m in adGroups)
            {
                sb.AppendLine("# " + C($"{m.MemberName} ({m.MemberUpn}) in AD group '{m.GroupName}'. {m.Reason}"));
                sb.AppendLine("# " + C($"Remove-ADGroupMember -Identity {Ps(m.SamAccountName ?? m.GroupName ?? m.GroupId)} -Members {Ps(m.MemberUpn ?? m.MemberId)} -Confirm:$false"));
            }
            sb.AppendLine("#endregion");
        }

        if (azure.Count + entra.Count + members.Count == 0)
        {
            sb.AppendLine("Write-Host 'No open removals in this snapshot.'");
        }
        return sb.ToString();
    }

    private sealed record ScriptMember(string GroupId, string? GroupName, bool OnPremises, string? SamAccountName,
        string MemberId, string? MemberName, string? MemberUpn, string? Reason);

    private static string Describe(RoleAssignmentSnapshot r) =>
        $"{r.PrincipalDisplayName} ({r.PrincipalUpn ?? r.PrincipalType}) - {r.RoleName} - {DisplayText.Of(r.ScopeLevel)} '{r.ScopeDisplayName}' - " +
        $"{DisplayText.Of(r.AssignmentType)} - Reason: {r.ReviewerComment ?? r.RecommendationReason}";

    private static string RoleGuid(string roleDefinitionId)
    {
        var last = roleDefinitionId.TrimEnd('/').Split('/').Last();
        return Guid.TryParse(last, out var g) ? g.ToString() : last;
    }

    /// <summary>PowerShell single-quoted string literal.</summary>
    private static string Ps(string value) => "'" + value.Replace("'", "''").Replace("\r", " ").Replace("\n", " ") + "'";

    /// <summary>Text that is safe inside a PowerShell comment (no line breaks, no block comment markers).</summary>
    private static string C(string? value) =>
        (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("#>", "# >").Replace("<#", "< #");
}
