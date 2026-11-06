using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;

namespace AccessReviewPortal.Services.Review;

public enum AccessChangeKind
{
    AzureRoleAssignment,
    AzurePimEligible,
    AzurePimActive,
    EntraRoleAssignment,
    EntraPimEligible,
    EntraPimActive,
    CloudGroupMembership,
    OnPremisesGroupMembership
}

/// <summary>
/// One change in the tenant that takes access away: a role assignment, or the membership of a user in a group.
/// A membership change can come from several review rows ("via group") and from a group member decision.
/// </summary>
public sealed class AccessChange
{
    public required string Key { get; init; }
    public required AccessChangeKind Kind { get; init; }
    public required string Principal { get; init; }
    public string? PrincipalUpn { get; init; }

    /// <summary>Role name, or "Member of '&lt;group&gt;'".</summary>
    public required string Access { get; init; }

    public string? Scope { get; init; }
    public ScopeLevel? ScopeLevel { get; init; }
    public string? GroupId { get; init; }
    public string? MemberId { get; init; }
    public List<long> RowIds { get; init; } = [];
    public bool HasMemberDecision { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }
    public string? Reason { get; init; }

    /// <summary>Result of the last Apply: "Revoked ..." when done, otherwise the error of a failed attempt.</summary>
    public string? Status { get; init; }

    public string? AppliedBy { get; init; }
    public DateTime? AppliedAtUtc { get; init; }

    /// <summary>For group memberships: the roles that the group gives.</summary>
    public List<string> GroupAccess { get; set; } = [];

    public bool IsApplied => AccessChanges.IsDone(Status);

    /// <summary>Groups synced from on-premises AD can be changed only in AD, not through Microsoft Graph.</summary>
    public bool PortalCanApply => Kind != AccessChangeKind.OnPremisesGroupMembership;

    public string ChangeText => Kind switch
    {
        AccessChangeKind.AzureRoleAssignment => "Remove Azure role assignment",
        AccessChangeKind.AzurePimEligible => "Remove Azure PIM eligible assignment",
        AccessChangeKind.AzurePimActive => "Remove Azure PIM active assignment",
        AccessChangeKind.EntraRoleAssignment => "Remove Entra ID role assignment",
        AccessChangeKind.EntraPimEligible => "Remove Entra ID PIM eligible assignment",
        AccessChangeKind.EntraPimActive => "Remove Entra ID PIM active assignment",
        AccessChangeKind.CloudGroupMembership => "Remove from group",
        _ => "Remove from on-premises AD group"
    };
}

public static class AccessChanges
{
    public static bool IsDone(string? status) => status?.StartsWith("Revoked", StringComparison.Ordinal) == true;

    public static AccessChangeKind KindOf(RoleAssignmentSnapshot row) => UserCapabilityChecker.KindOf(row) switch
    {
        RevokeTargetKind.AzureRoleAssignment => row.AssignmentType switch
        {
            AssignmentType.PimEligible => AccessChangeKind.AzurePimEligible,
            AssignmentType.PimActive => AccessChangeKind.AzurePimActive,
            _ => AccessChangeKind.AzureRoleAssignment
        },
        RevokeTargetKind.EntraRoleAssignment => row.AssignmentType switch
        {
            AssignmentType.PimEligible => AccessChangeKind.EntraPimEligible,
            AssignmentType.PimActive => AccessChangeKind.EntraPimActive,
            _ => AccessChangeKind.EntraRoleAssignment
        },
        RevokeTargetKind.CloudGroupMembership => AccessChangeKind.CloudGroupMembership,
        _ => AccessChangeKind.OnPremisesGroupMembership
    };

    /// <summary>
    /// Changes from review rows with the decision "Approve Removal" and group member decisions "Remove from Group".
    /// Access through a group is removed by removing the member from the group: all rows and the member decision of the
    /// same (group, member) become one change, the same way <see cref="RemediationService"/> applies them.
    /// </summary>
    public static List<AccessChange> Build(IEnumerable<RoleAssignmentSnapshot> rows, IEnumerable<GroupMemberDecision> memberDecisions,
        IReadOnlyDictionary<string, GroupSnapshot> groups)
    {
        var rowList = rows.ToList();
        var changes = rowList.Where(r => r.AssignedViaGroupId is null).Select(r => new AccessChange
        {
            Key = "row:" + r.Id,
            Kind = KindOf(r),
            Principal = r.PrincipalDisplayName ?? r.PrincipalId,
            PrincipalUpn = r.PrincipalUpn,
            Access = r.RoleName,
            Scope = r.ScopeDisplayName ?? r.ScopeId,
            ScopeLevel = r.ScopeLevel,
            RowIds = [r.Id],
            ApprovedBy = r.ReviewedByUpn,
            ApprovedAtUtc = r.ReviewedAtUtc,
            Reason = r.ReviewerComment ?? r.RecommendationReason,
            Status = r.RemediationStatus,
            AppliedBy = r.RemediatedByUpn,
            AppliedAtUtc = r.RemediatedAtUtc
        }).ToList();

        var viaGroup = rowList.Where(r => r.AssignedViaGroupId is not null)
            .GroupBy(r => UserCapabilityChecker.MemberKey(r.AssignedViaGroupId!, r.PrincipalId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ReviewedAtUtc).ToList());
        var decisions = memberDecisions
            .GroupBy(d => UserCapabilityChecker.MemberKey(d.GroupId, d.MemberId))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var key in viaGroup.Keys.Union(decisions.Keys))
        {
            var memberRows = viaGroup.GetValueOrDefault(key) ?? [];
            decisions.TryGetValue(key, out var decision);
            var row = memberRows.FirstOrDefault();
            var groupId = row?.AssignedViaGroupId ?? decision!.GroupId;
            var memberId = row?.PrincipalId ?? decision!.MemberId;
            groups.TryGetValue(groupId, out var group);
            var onPremises = group?.OnPremisesSyncEnabled ?? row?.AssignedViaGroupOnPremises ?? false;
            var groupName = group?.DisplayName ?? row?.AssignedViaGroupName ?? groupId;
            var decisionIsLatest = decision is not null && (row is null || decision.ReviewedAtUtc >= row.ReviewedAtUtc);

            changes.Add(new AccessChange
            {
                Key = "member:" + key,
                Kind = onPremises ? AccessChangeKind.OnPremisesGroupMembership : AccessChangeKind.CloudGroupMembership,
                Principal = row?.PrincipalDisplayName ?? decision?.MemberDisplayName ?? memberId,
                PrincipalUpn = row?.PrincipalUpn ?? decision?.MemberUpn,
                Access = $"Member of '{groupName}'",
                Scope = onPremises ? "Group synced from on-premises AD" : "Microsoft Entra ID group",
                GroupId = groupId,
                MemberId = memberId,
                RowIds = memberRows.Select(r => r.Id).ToList(),
                HasMemberDecision = decision is not null,
                ApprovedBy = decisionIsLatest ? decision!.ReviewedByUpn : row?.ReviewedByUpn,
                ApprovedAtUtc = decisionIsLatest ? decision!.ReviewedAtUtc : row?.ReviewedAtUtc,
                Reason = decision?.Comment ?? row?.ReviewerComment ?? row?.RecommendationReason,
                Status = decision?.RemediationStatus ?? row?.RemediationStatus,
                AppliedBy = decision?.RemediatedByUpn ?? row?.RemediatedByUpn,
                AppliedAtUtc = decision?.RemediatedAtUtc ?? row?.RemediatedAtUtc,
                GroupAccess = memberRows.Select(r => $"{r.RoleName} on {r.ScopeDisplayName ?? r.ScopeId}").Distinct().ToList()
            });
        }

        return changes.OrderBy(c => c.Principal, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.Access).ToList();
    }

    /// <summary>Number of changes, counted like <see cref="Build"/>: direct rows, plus one per (group, member).</summary>
    public static int CountChanges(IEnumerable<(string? ViaGroupId, string PrincipalId)> rows, IEnumerable<(string GroupId, string MemberId)> memberDecisions)
    {
        var direct = 0;
        var memberships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (viaGroupId, principalId) in rows)
        {
            if (viaGroupId is null) direct++;
            else memberships.Add(UserCapabilityChecker.MemberKey(viaGroupId, principalId));
        }
        foreach (var (groupId, memberId) in memberDecisions)
        {
            memberships.Add(UserCapabilityChecker.MemberKey(groupId, memberId));
        }
        return direct + memberships.Count;
    }

    /// <summary>Rights of the signed-in user for this change (from the capability report of the pending rows and decisions).</summary>
    public static RevokeCapability? CapabilityOf(AccessChange change, CapabilityReport? report)
    {
        if (report is null) return null;
        foreach (var id in change.RowIds)
        {
            if (report.Rows.TryGetValue(id, out var capability)) return capability;
        }
        return change.GroupId is not null && change.MemberId is not null
            ? report.Members.GetValueOrDefault(UserCapabilityChecker.MemberKey(change.GroupId, change.MemberId))
            : null;
    }
}

/// <summary>Per sign-in (Blazor circuit) signal: decisions or applied changes changed the number of pending changes.</summary>
public sealed class PendingChangesNotifier
{
    public event Action? Changed;

    public void Notify() => Changed?.Invoke();
}
