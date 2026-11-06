using AccessReviewPortal.Data;
using AccessReviewPortal.Domain;
using Microsoft.EntityFrameworkCore;

namespace AccessReviewPortal.Services.Reporting;

/// <summary>
/// Reads the numbers for the KPI dashboard and the period report. Counts per snapshot are made in PostgreSQL;
/// only the decisions that the report lists (removals, exceptions, applied changes) are loaded row by row.
/// </summary>
public sealed class KpiReportService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time)
{
    public async Task<KpiReport> BuildAsync(ReportPeriod period, string? generatedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var fromUtc = period.FromUtc;
        var toUtc = period.ToUtcExclusive;

        var snapshots = await db.ScanSnapshots.AsNoTracking()
            .Where(s => s.Status == ScanStatus.Completed && s.CreatedAtUtc < toUtc)
            .OrderBy(s => s.CreatedAtUtc)
            .Select(s => new SnapshotRef(s.Id, s.Label, s.CreatedAtUtc, s.InactivityThresholdDays))
            .ToListAsync(ct);
        var end = snapshots.LastOrDefault();
        var start = snapshots.LastOrDefault(s => s.CreatedAtUtc < fromUtc) ?? snapshots.FirstOrDefault(s => s.CreatedAtUtc >= fromUtc);
        if (start?.Id == end?.Id) start = null;

        var used = snapshots.Where(s => s.CreatedAtUtc >= fromUtc || s.Id == end?.Id || s.Id == start?.Id).ToList();
        var snapshotKpis = await SnapshotKpisAsync(db, used, ct);
        var endUsers = end is null ? [] : await UserStatesAsync(db, end.Id, ct);
        var startUsers = start is null ? null : await UserStatesAsync(db, start.Id, ct);
        var rowCounts = await ReviewerRowCountsAsync(db, fromUtc, toUtc, ct);
        var dailyCounts = await DailyDecisionCountsAsync(db, fromUtc, toUtc, ct);
        var facts = await RoleAssignmentFactsAsync(db, fromUtc, toUtc, ct);
        facts.AddRange(await GroupMemberFactsAsync(db, fromUtc, toUtc, ct));

        return KpiCalculator.Build(period, time.GetUtcNow().UtcDateTime, generatedBy, snapshotKpis, end?.Id, start?.Id,
            endUsers, startUsers, rowCounts, dailyCounts, facts);
    }

    private static async Task<List<SnapshotKpi>> SnapshotKpisAsync(AppDbContext db, List<SnapshotRef> snapshots, CancellationToken ct)
    {
        if (snapshots.Count == 0) return [];
        var ids = snapshots.Select(s => s.Id).ToList();
        var rows = db.RoleAssignmentSnapshots.AsNoTracking().Where(r => ids.Contains(r.ScanSnapshotId));

        var principals = await rows
            .Where(r => r.PrincipalType == "User" || r.PrincipalType == "ServicePrincipal")
            .GroupBy(r => new { r.ScanSnapshotId, r.PrincipalType })
            .Select(g => new
            {
                g.Key.ScanSnapshotId,
                g.Key.PrincipalType,
                WithRoles = g.Select(r => r.PrincipalId).Distinct().Count(),
                WithUnused = g.Where(r => r.UsageEvidenceStatus == UsageEvidenceStatus.Unused90Days).Select(r => r.PrincipalId).Distinct().Count(),
                Rows = g.Count(),
                UsedRows = g.Count(r => r.UsageEvidenceStatus == UsageEvidenceStatus.ConfirmedUsed),
                SignInRows = g.Count(r => r.UsageEvidenceStatus == UsageEvidenceStatus.PortalSignInOnlyNoWrite),
                UnusedRows = g.Count(r => r.UsageEvidenceStatus == UsageEvidenceStatus.Unused90Days)
            })
            .ToListAsync(ct);

        var progress = await rows
            .Where(r => r.RecommendedAction != RecommendedAction.NoAction)
            .GroupBy(r => r.ScanSnapshotId)
            .Select(g => new { Id = g.Key, ToReview = g.Count(), Reviewed = g.Count(r => r.ReviewerDecision != ReviewerDecision.Pending) })
            .ToListAsync(ct);

        return snapshots.Select(s =>
        {
            var users = principals.FirstOrDefault(p => p.ScanSnapshotId == s.Id && p.PrincipalType == "User");
            var sps = principals.FirstOrDefault(p => p.ScanSnapshotId == s.Id && p.PrincipalType == "ServicePrincipal");
            var p = progress.FirstOrDefault(x => x.Id == s.Id);
            return new SnapshotKpi
            {
                Snapshot = s,
                UsersWithRoles = users?.WithRoles ?? 0,
                UsersWithUnusedRoles = users?.WithUnused ?? 0,
                UnusedUserAssignments = users?.UnusedRows ?? 0,
                UserAssignments = users?.Rows ?? 0,
                UserAssignmentsUsed = users?.UsedRows ?? 0,
                UserAssignmentsSignInOnly = users?.SignInRows ?? 0,
                ServicePrincipalsWithRoles = sps?.WithRoles ?? 0,
                ServicePrincipalsWithUnusedRoles = sps?.WithUnused ?? 0,
                RowsToReview = p?.ToReview ?? 0,
                RowsReviewed = p?.Reviewed ?? 0
            };
        }).ToList();
    }

    private static async Task<List<UserRoleState>> UserStatesAsync(AppDbContext db, int snapshotId, CancellationToken ct)
    {
        var perUser = await db.RoleAssignmentSnapshots.AsNoTracking()
            .Where(r => r.ScanSnapshotId == snapshotId && r.PrincipalType == "User")
            .GroupBy(r => r.PrincipalId)
            .Select(g => new { UserId = g.Key, Assignments = g.Count(), Unused = g.Count(r => r.UsageEvidenceStatus == UsageEvidenceStatus.Unused90Days) })
            .ToListAsync(ct);
        var departments = (await db.UserStateSnapshots.AsNoTracking()
                .Where(u => u.ScanSnapshotId == snapshotId)
                .Select(u => new { u.UserId, u.Department })
                .ToListAsync(ct))
            .GroupBy(u => u.UserId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Department, StringComparer.OrdinalIgnoreCase);
        return perUser.Select(u => new UserRoleState(u.UserId, departments.GetValueOrDefault(u.UserId), u.Assignments, u.Unused)).ToList();
    }

    private static async Task<List<ReviewerRowCounts>> ReviewerRowCountsAsync(AppDbContext db, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var counts = await db.RoleAssignmentSnapshots.AsNoTracking()
            .Where(r => r.ReviewerDecision != ReviewerDecision.Pending && r.ReviewedAtUtc >= fromUtc && r.ReviewedAtUtc < toUtc)
            .GroupBy(r => r.ReviewedByUpn)
            .Select(g => new
            {
                Reviewer = g.Key,
                Removed = g.Count(r => r.ReviewerDecision == ReviewerDecision.Removed),
                Kept = g.Count(r => r.ReviewerDecision == ReviewerDecision.NoAction),
                Last = g.Max(r => r.ReviewedAtUtc)
            })
            .ToListAsync(ct);
        return counts.Select(c => new ReviewerRowCounts(c.Reviewer, c.Removed, c.Kept, c.Last)).ToList();
    }

    /// <summary>Decisions on role assignment rows per UTC day (the database runs with timezone = UTC).</summary>
    private static async Task<List<DailyDecisionCounts>> DailyDecisionCountsAsync(AppDbContext db, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var counts = await db.RoleAssignmentSnapshots.AsNoTracking()
            .Where(r => r.ReviewerDecision != ReviewerDecision.Pending && r.ReviewedAtUtc >= fromUtc && r.ReviewedAtUtc < toUtc)
            .GroupBy(r => r.ReviewedAtUtc!.Value.Date)
            .Select(g => new
            {
                Day = g.Key,
                Removed = g.Count(r => r.ReviewerDecision == ReviewerDecision.Removed),
                Kept = g.Count(r => r.ReviewerDecision == ReviewerDecision.NoAction)
            })
            .ToListAsync(ct);
        return counts.Select(c => new DailyDecisionCounts(DateOnly.FromDateTime(c.Day), c.Removed, c.Kept)).ToList();
    }

    /// <summary>Approved removals and possible exceptions decided in the period, and changes applied in the period.</summary>
    private static async Task<List<DecisionFact>> RoleAssignmentFactsAsync(AppDbContext db, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var rows = await (
                from r in db.RoleAssignmentSnapshots.AsNoTracking()
                where (r.ReviewedAtUtc >= fromUtc && r.ReviewedAtUtc < toUtc
                       && (r.ReviewerDecision == ReviewerDecision.Removed
                           // Keep on an unused role or on a removal recommendation: KpiCalculator.IsException.
                           || (r.ReviewerDecision == ReviewerDecision.NoAction
                               && (r.UsageEvidenceStatus == UsageEvidenceStatus.Unused90Days || r.RecommendedAction == RecommendedAction.Remove))))
                      || (r.RemediatedAtUtc >= fromUtc && r.RemediatedAtUtc < toUtc && r.RemediationStatus != null && r.RemediationStatus.StartsWith("Revoked"))
                join u in db.UserStateSnapshots.AsNoTracking()
                    on new { r.ScanSnapshotId, UserId = r.PrincipalId } equals new { u.ScanSnapshotId, u.UserId } into users
                from u in users.DefaultIfEmpty()
                select new
                {
                    r.Id,
                    r.ScanSnapshotId,
                    r.AssignmentKey,
                    r.PrincipalId,
                    r.PrincipalType,
                    r.PrincipalDisplayName,
                    r.PrincipalUpn,
                    r.AssignedViaGroupId,
                    r.AssignedViaGroupName,
                    r.ScopeLevel,
                    r.ScopeId,
                    r.ScopeDisplayName,
                    r.SubscriptionName,
                    r.RoleName,
                    r.UsageEvidenceStatus,
                    r.LastRoleUsedUtc,
                    r.RecommendedAction,
                    r.RecommendationReason,
                    r.ReviewerDecision,
                    r.ReviewerComment,
                    r.ReviewedAtUtc,
                    r.ReviewedByUpn,
                    r.RemediationStatus,
                    r.RemediatedAtUtc,
                    r.RemediatedByUpn,
                    Department = u == null ? null : u.Department
                })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var scope = r.ScopeLevel is ScopeLevel.ResourceGroup or ScopeLevel.Resource
                ? $"{r.SubscriptionName} / {r.ScopeDisplayName}"
                : r.ScopeDisplayName ?? r.ScopeId;
            var role = $"{r.RoleName} on {scope}";
            var viaGroup = r.AssignedViaGroupId is not null;
            return new DecisionFact
            {
                SnapshotId = r.ScanSnapshotId,
                Kind = ReviewItemKind.RoleAssignment,
                PrincipalId = r.PrincipalId,
                PrincipalType = r.PrincipalType,
                DisplayName = r.PrincipalDisplayName,
                Upn = r.PrincipalUpn,
                Department = r.Department,
                Access = viaGroup ? $"{role} (via group '{r.AssignedViaGroupName}')" : role,
                ItemKey = r.AssignmentKey,
                GroupId = r.AssignedViaGroupId,
                ChangeKey = viaGroup ? DecisionFact.MemberChangeKey(r.ScanSnapshotId, r.AssignedViaGroupId!, r.PrincipalId) : $"{r.ScanSnapshotId}:row:{r.Id}",
                ChangeText = viaGroup ? $"Member of group '{r.AssignedViaGroupName}'" : role,
                Usage = r.UsageEvidenceStatus,
                LastUsedUtc = r.LastRoleUsedUtc,
                Recommendation = r.RecommendedAction,
                Reason = r.RecommendationReason,
                ApprovedRemoval = r.ReviewerDecision == ReviewerDecision.Removed,
                Comment = r.ReviewerComment,
                ReviewedAtUtc = r.ReviewerDecision == ReviewerDecision.Pending ? null : r.ReviewedAtUtc,
                ReviewedBy = r.ReviewedByUpn,
                Applied = IsRevoked(r.RemediationStatus),
                AppliedAtUtc = r.RemediatedAtUtc,
                AppliedBy = r.RemediatedByUpn
            };
        }).ToList();
    }

    /// <summary>"Keep in Group" / "Remove from Group" decisions from the Group Permissions page.</summary>
    private static async Task<List<DecisionFact>> GroupMemberFactsAsync(AppDbContext db, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var decisions = await db.GroupMemberDecisions.AsNoTracking()
            .Where(d => (d.Decision != GroupMemberDecisionType.Pending && d.ReviewedAtUtc >= fromUtc && d.ReviewedAtUtc < toUtc)
                        || (d.RemediatedAtUtc >= fromUtc && d.RemediatedAtUtc < toUtc && d.RemediationStatus != null && d.RemediationStatus.StartsWith("Revoked")))
            .ToListAsync(ct);
        if (decisions.Count == 0) return [];

        var snapshotIds = decisions.Select(d => d.ScanSnapshotId).Distinct().ToList();
        var memberIds = decisions.Select(d => d.MemberId).Distinct().ToList();
        var memberRows = await db.RoleAssignmentSnapshots.AsNoTracking()
            .Where(r => snapshotIds.Contains(r.ScanSnapshotId) && r.AssignedViaGroupId != null && memberIds.Contains(r.PrincipalId))
            .Select(r => new
            {
                r.ScanSnapshotId,
                r.AssignedViaGroupId,
                r.AssignedViaGroupName,
                r.PrincipalId,
                r.PrincipalType,
                r.UsageEvidenceStatus,
                r.RecommendedAction,
                r.RecommendationReason,
                r.LastRoleUsedUtc
            })
            .ToListAsync(ct);
        var groups = await db.GroupSnapshots.AsNoTracking()
            .Where(g => snapshotIds.Contains(g.ScanSnapshotId))
            .Select(g => new { g.ScanSnapshotId, g.GroupId, g.DisplayName })
            .ToListAsync(ct);
        var users = await db.UserStateSnapshots.AsNoTracking()
            .Where(u => snapshotIds.Contains(u.ScanSnapshotId) && memberIds.Contains(u.UserId))
            .Select(u => new { u.ScanSnapshotId, u.UserId, u.Department })
            .ToListAsync(ct);

        return decisions.Select(d =>
        {
            var rows = memberRows.Where(r => r.ScanSnapshotId == d.ScanSnapshotId && Same(r.AssignedViaGroupId, d.GroupId) && Same(r.PrincipalId, d.MemberId)).ToList();
            var user = users.FirstOrDefault(u => u.ScanSnapshotId == d.ScanSnapshotId && Same(u.UserId, d.MemberId));
            var type = rows.FirstOrDefault()?.PrincipalType ?? (user is null ? "ServicePrincipal" : "User");

            // Same rule as the Group Permissions page: the best usage over the roles of the group; no evidence means unused.
            var usage = rows.Count == 0 ? UsageEvidenceStatus.Unused90Days : rows.MinBy(r => UsageRank(r.UsageEvidenceStatus))!.UsageEvidenceStatus;
            var removeRow = rows.FirstOrDefault(r => r.RecommendedAction == RecommendedAction.Remove);
            var (recommendation, reason) = removeRow is not null
                ? (RecommendedAction.Remove, removeRow.RecommendationReason)
                : usage == UsageEvidenceStatus.Unused90Days && type == "User"
                    ? (RecommendedAction.Remove, "No use of the group's roles in the review period")
                    : (RecommendedAction.NoAction, (string?)null);

            var groupName = groups.FirstOrDefault(g => g.ScanSnapshotId == d.ScanSnapshotId && Same(g.GroupId, d.GroupId))?.DisplayName
                            ?? rows.FirstOrDefault()?.AssignedViaGroupName ?? d.GroupId;
            var access = $"Member of group '{groupName}'";
            return new DecisionFact
            {
                SnapshotId = d.ScanSnapshotId,
                Kind = ReviewItemKind.GroupMembership,
                PrincipalId = d.MemberId,
                PrincipalType = type,
                DisplayName = d.MemberDisplayName,
                Upn = d.MemberUpn,
                Department = user?.Department,
                Access = access,
                ItemKey = $"member:{d.GroupId}:{d.MemberId}".ToLowerInvariant(),
                GroupId = d.GroupId,
                ChangeKey = DecisionFact.MemberChangeKey(d.ScanSnapshotId, d.GroupId, d.MemberId),
                ChangeText = access,
                Usage = usage,
                LastUsedUtc = rows.Max(r => r.LastRoleUsedUtc),
                Recommendation = recommendation,
                Reason = reason,
                ApprovedRemoval = d.Decision == GroupMemberDecisionType.RemoveFromGroup,
                Comment = d.Comment,
                ReviewedAtUtc = d.Decision == GroupMemberDecisionType.Pending ? null : d.ReviewedAtUtc,
                ReviewedBy = d.ReviewedByUpn,
                Applied = IsRevoked(d.RemediationStatus),
                AppliedAtUtc = d.RemediatedAtUtc,
                AppliedBy = d.RemediatedByUpn
            };
        }).ToList();
    }

    private static bool IsRevoked(string? status) => status?.StartsWith("Revoked", StringComparison.Ordinal) == true;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static int UsageRank(UsageEvidenceStatus s) => s switch
    {
        UsageEvidenceStatus.ConfirmedUsed => 0,
        UsageEvidenceStatus.PortalSignInOnlyNoWrite => 1,
        _ => 2
    };
}
