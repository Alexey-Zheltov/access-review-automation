using AccessReviewPortal.Data;
using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using Microsoft.EntityFrameworkCore;

namespace AccessReviewPortal.Services.Review;

/// <summary>One role assignment of a group and how its members used it.</summary>
public sealed class GroupAssignmentInfo
{
    public required RoleAssignmentSnapshot GroupRow { get; init; }
    public int Members { get; init; }
    public int ActiveMembers { get; init; }
    public int SignInOnlyMembers { get; init; }
    public int UnusedMembers { get; init; }

    /// <summary>Share of members that used the assignment: write evidence / PIM activation, or sign-ins for read-only roles.</summary>
    public int UsagePercent { get; init; }
}

public sealed class GroupMemberInfo
{
    public required string MemberId { get; init; }
    public string? DisplayName { get; init; }
    public string? Upn { get; init; }
    public string PrincipalType { get; init; } = "User";
    public string? Department { get; init; }
    public string? Manager { get; init; }
    public string MoverStatus { get; init; } = "No change";
    public bool IsMoverOrDisabled { get; init; }
    public UsageEvidenceStatus BestUsage { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public string? DefenderFlags { get; init; }
    public GroupMemberDecisionType RecommendedDecision { get; init; }
    public string RecommendationReason { get; init; } = "";
    public GroupMemberDecision? Decision { get; init; }
    public List<RoleAssignmentSnapshot> Rows { get; init; } = [];
}

public sealed class GroupSummary
{
    public required GroupSnapshot Group { get; init; }
    public string Source => Group.OnPremisesSyncEnabled ? "On-Premises AD Synced" : "Cloud-Only";
    public List<GroupAssignmentInfo> Assignments { get; init; } = [];
    public List<GroupMemberInfo> Members { get; init; } = [];
    public int TotalMembers => Math.Max(Group.TotalTransitiveMembers, Members.Count);
    public int ActiveMembers => Members.Count(m => m.BestUsage == UsageEvidenceStatus.ConfirmedUsed);
    public int SignInOnlyMembers => Members.Count(m => m.BestUsage == UsageEvidenceStatus.PortalSignInOnlyNoWrite);
    public int UnusedMembers => Members.Count(m => m.BestUsage == UsageEvidenceStatus.Unused90Days);
    public int MoversOrDisabled => Members.Count(m => m.IsMoverOrDisabled);
    public int DefenderFlaggedMembers => Members.Count(m => m.DefenderFlags is not null);
    public string? GroupDefenderFlags => string.Join(", ", Assignments.Select(a => a.GroupRow.DefenderCspmFindings).Where(f => !string.IsNullOrEmpty(f)).Distinct()) is { Length: > 0 } s ? s : null;
}

/// <summary>Group Permissions view: every group with Entra ID or Azure RBAC assignments, its roles and how its members use them.</summary>
public sealed class GroupPermissionsService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<GroupSummary>> GetGroupsAsync(int snapshotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var snapshot = await db.ScanSnapshots.AsNoTracking().FirstAsync(s => s.Id == snapshotId, ct);

        var rows = await db.RoleAssignmentSnapshots.AsNoTracking()
            .Where(r => r.ScanSnapshotId == snapshotId && (r.AssignedViaGroupId != null || r.PrincipalType == "Group"))
            .ToListAsync(ct);
        var groups = await db.GroupSnapshots.AsNoTracking().Where(g => g.ScanSnapshotId == snapshotId).ToListAsync(ct);
        var decisions = await db.GroupMemberDecisions.AsNoTracking().Where(d => d.ScanSnapshotId == snapshotId).ToListAsync(ct);
        var users = await db.UserStateSnapshots.AsNoTracking().Where(u => u.ScanSnapshotId == snapshotId)
            .ToDictionaryAsync(u => u.UserId, StringComparer.OrdinalIgnoreCase, ct);
        var baselineUsers = snapshot.BaselineSnapshotId is int b
            ? await db.UserStateSnapshots.AsNoTracking().Where(u => u.ScanSnapshotId == b).ToDictionaryAsync(u => u.UserId, StringComparer.OrdinalIgnoreCase, ct)
            : new Dictionary<string, UserStateSnapshot>(StringComparer.OrdinalIgnoreCase);

        return Build(rows, groups, decisions, users, baselineUsers);
    }

    /// <summary>Pure calculation, separated for unit tests.</summary>
    public static List<GroupSummary> Build(
        IReadOnlyList<RoleAssignmentSnapshot> rows,
        IReadOnlyList<GroupSnapshot> groups,
        IReadOnlyList<GroupMemberDecision> decisions,
        IReadOnlyDictionary<string, UserStateSnapshot> users,
        IReadOnlyDictionary<string, UserStateSnapshot> baselineUsers)
    {
        var groupRows = rows.Where(r => r.AssignedViaGroupId is null && r.PrincipalType == "Group").ToList();
        var memberRows = rows.Where(r => r.AssignedViaGroupId is not null).ToList();
        var groupsById = groups.ToDictionary(g => g.GroupId, StringComparer.OrdinalIgnoreCase);
        var decisionsByKey = decisions.ToDictionary(d => UserCapabilityChecker.MemberKey(d.GroupId, d.MemberId), StringComparer.OrdinalIgnoreCase);

        var groupIds = groupRows.Select(r => r.PrincipalId).Concat(memberRows.Select(r => r.AssignedViaGroupId!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var result = new List<GroupSummary>();
        foreach (var groupId in groupIds)
        {
            var own = groupRows.Where(r => string.Equals(r.PrincipalId, groupId, StringComparison.OrdinalIgnoreCase)).ToList();
            var viaGroup = memberRows.Where(r => string.Equals(r.AssignedViaGroupId, groupId, StringComparison.OrdinalIgnoreCase)).ToList();
            var group = groupsById.GetValueOrDefault(groupId) ?? new GroupSnapshot
            {
                GroupId = groupId,
                DisplayName = own.FirstOrDefault()?.PrincipalDisplayName ?? viaGroup.FirstOrDefault()?.AssignedViaGroupName ?? groupId,
                OnPremisesSyncEnabled = viaGroup.Any(r => r.AssignedViaGroupOnPremises),
                ExpansionError = own.All(r => !r.PrincipalExists) && own.Count > 0 ? "Group not found in Entra ID" : null
            };

            var assignments = own.Select(g =>
            {
                var members = viaGroup.Where(m => SameAssignment(g, m)).ToList();
                var active = members.Count(m => m.UsageEvidenceStatus == UsageEvidenceStatus.ConfirmedUsed);
                var signInOnly = members.Count(m => m.UsageEvidenceStatus == UsageEvidenceStatus.PortalSignInOnlyNoWrite);
                var used = active + (g.RoleHasWritePermissions ? 0 : signInOnly);
                return new GroupAssignmentInfo
                {
                    GroupRow = g,
                    Members = members.Count,
                    ActiveMembers = active,
                    SignInOnlyMembers = signInOnly,
                    UnusedMembers = members.Count(m => m.UsageEvidenceStatus == UsageEvidenceStatus.Unused90Days),
                    UsagePercent = members.Count == 0 ? 0 : (int)Math.Round(100.0 * used / members.Count)
                };
            }).OrderBy(a => a.GroupRow.ScopeLevel).ThenBy(a => a.GroupRow.RoleName).ToList();

            var members = viaGroup.GroupBy(r => r.PrincipalId, StringComparer.OrdinalIgnoreCase).Select(m =>
            {
                var memberRowsOfUser = m.ToList();
                var first = memberRowsOfUser[0];
                users.TryGetValue(m.Key, out var user);
                baselineUsers.TryGetValue(m.Key, out var baseline);
                var (moverStatus, isMover) = Mover(user, baseline, first);
                var best = memberRowsOfUser.Min(r => UsageRank(r.UsageEvidenceStatus));
                var bestUsage = memberRowsOfUser.First(r => UsageRank(r.UsageEvidenceStatus) == best).UsageEvidenceStatus;
                var flags = memberRowsOfUser.Select(r => r.DefenderCspmFindings).Where(f => !string.IsNullOrEmpty(f))
                    .SelectMany(f => f!.Split(',')).Distinct().ToList();

                var (recommended, reason) = isMover
                    ? (GroupMemberDecisionType.RemoveFromGroup, moverStatus)
                    : bestUsage == UsageEvidenceStatus.Unused90Days && first.PrincipalType == "User"
                        ? (GroupMemberDecisionType.RemoveFromGroup, "No use of the group's roles in the review period")
                        : (GroupMemberDecisionType.KeepInGroup, bestUsage == UsageEvidenceStatus.ConfirmedUsed ? "Uses the group's roles" : "Signs in; no write activity");

                return new GroupMemberInfo
                {
                    MemberId = m.Key,
                    DisplayName = first.PrincipalDisplayName ?? user?.DisplayName,
                    Upn = first.PrincipalUpn ?? user?.Upn,
                    PrincipalType = first.PrincipalType,
                    Department = user?.Department,
                    Manager = user?.ManagerDisplayName ?? user?.ManagerUpn,
                    MoverStatus = moverStatus,
                    IsMoverOrDisabled = isMover,
                    BestUsage = bestUsage,
                    LastUsedUtc = memberRowsOfUser.Max(r => r.LastRoleUsedUtc),
                    DefenderFlags = flags.Count == 0 ? null : string.Join(", ", flags),
                    RecommendedDecision = recommended,
                    RecommendationReason = reason,
                    Decision = decisionsByKey.GetValueOrDefault(UserCapabilityChecker.MemberKey(groupId, m.Key)),
                    Rows = memberRowsOfUser
                };
            }).OrderBy(m => m.DisplayName).ToList();

            result.Add(new GroupSummary { Group = group, Assignments = assignments, Members = members });
        }
        return result.OrderBy(g => g.Group.DisplayName).ToList();
    }

    private static bool SameAssignment(RoleAssignmentSnapshot groupRow, RoleAssignmentSnapshot memberRow) =>
        string.Equals(groupRow.ScopeId, memberRow.ScopeId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(groupRow.RoleDefinitionId, memberRow.RoleDefinitionId, StringComparison.OrdinalIgnoreCase)
        && groupRow.AssignmentType == memberRow.AssignmentType;

    private static int UsageRank(UsageEvidenceStatus s) => s switch
    {
        UsageEvidenceStatus.ConfirmedUsed => 0,
        UsageEvidenceStatus.PortalSignInOnlyNoWrite => 1,
        _ => 2
    };

    private static (string Status, bool IsMover) Mover(UserStateSnapshot? user, UserStateSnapshot? baseline, RoleAssignmentSnapshot row)
    {
        if (user is not null && !user.ExistsInDirectory) return ("Deleted from Entra ID", true);
        if (user is not null && !user.AccountEnabled) return ("Account disabled", true);
        if (!row.PrincipalExists) return ("Not found in Entra ID", true);
        if (user is not null && baseline is not null && baseline.ExistsInDirectory)
        {
            var changes = new List<string>();
            if (!string.Equals((user.Department ?? "").Trim(), (baseline.Department ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                changes.Add($"Department {baseline.Department ?? "-"} -> {user.Department ?? "-"}");
            if (!string.Equals(user.ManagerId ?? "", baseline.ManagerId ?? "", StringComparison.OrdinalIgnoreCase))
                changes.Add($"Manager {baseline.ManagerDisplayName ?? baseline.ManagerUpn ?? "-"} -> {user.ManagerDisplayName ?? user.ManagerUpn ?? "-"}");
            if (changes.Count > 0) return ("Mover: " + string.Join("; ", changes), true);
        }
        return (row.IsNewSinceBaseline ? "New since baseline" : "No change", false);
    }
}
