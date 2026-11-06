using AccessReviewPortal.Data;
using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using Microsoft.EntityFrameworkCore;

namespace AccessReviewPortal.Services.Review;

public sealed class ReviewFilter
{
    public ScopeLevel? ScopeLevel { get; set; }
    public string? SubscriptionId { get; set; }
    public string? RoleName { get; set; }

    /// <summary>All, Direct, Via Group, PIM Eligible, PIM Active, Permanent.</summary>
    public string AssignmentFilter { get; set; } = "All";

    public UsageEvidenceStatus? Usage { get; set; }

    /// <summary>All, Remove, Review, NoAction.</summary>
    public string Recommendation { get; set; } = "All";

    /// <summary>All, Pending, NoAction, Removed.</summary>
    public string Decision { get; set; } = "All";

    public string? PrincipalType { get; set; }
    public string? Search { get; set; }
    public bool OnlyNewSinceBaseline { get; set; }

    /// <summary>All, Any (any finding), None, or a DefenderCspmStatus name.</summary>
    public string Defender { get; set; } = "All";

    public static readonly string[] AssignmentFilters = ["All", "Direct", "Via Group", "PIM Eligible", "PIM Active", "Permanent"];
}

public sealed record CountItem(string Name, int Count);

public sealed class SnapshotStats
{
    public int Total { get; init; }
    public int Users { get; init; }
    public int ViaGroup { get; init; }
    public int NewSinceBaseline { get; init; }
    public Dictionary<ScopeLevel, int> ByLevel { get; init; } = [];
    public Dictionary<AssignmentType, int> ByType { get; init; } = [];
    public Dictionary<UsageEvidenceStatus, int> ByUsage { get; init; } = [];
    public Dictionary<RecommendedAction, int> ByRecommendation { get; init; } = [];
    public Dictionary<ReviewerDecision, int> ByDecision { get; init; } = [];
    public List<CountItem> RemoveReasons { get; init; } = [];
    public List<CountItem> ReviewReasons { get; init; } = [];
    public Dictionary<DefenderCspmStatus, int> ByDefender { get; init; } = [];
}

public sealed class MoverInfo
{
    public required UserStateSnapshot Current { get; init; }
    public UserStateSnapshot? Baseline { get; init; }
    public required string Change { get; init; }
    public int Assignments { get; init; }
}

public sealed class TableSize
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public long Rows { get; set; }
}

public sealed class FilterOptions
{
    public List<(string Id, string Name)> Subscriptions { get; init; } = [];
    public List<string> Roles { get; init; } = [];
    public List<string> PrincipalTypes { get; init; } = [];
}

public sealed class SnapshotService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<ScanSnapshot>> GetSnapshotsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScanSnapshots.AsNoTracking().OrderByDescending(s => s.CreatedAtUtc).ToListAsync(ct);
    }

    public async Task<ScanSnapshot?> GetSnapshotAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScanSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    public async Task<List<ScanLogEntry>> GetLogAsync(int snapshotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ScanLogEntries.AsNoTracking().Where(l => l.ScanSnapshotId == snapshotId).OrderBy(l => l.Id).ToListAsync(ct);
    }

    public async Task<SnapshotStats> GetStatsAsync(int snapshotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.RoleAssignmentSnapshots.AsNoTracking().Where(r => r.ScanSnapshotId == snapshotId);

        var reasons = await q.Where(r => r.RecommendedAction != RecommendedAction.NoAction)
            .Select(r => new { r.RecommendedAction, r.RecommendationReason })
            .ToListAsync(ct);

        return new SnapshotStats
        {
            Total = await q.CountAsync(ct),
            Users = await db.UserStateSnapshots.CountAsync(u => u.ScanSnapshotId == snapshotId, ct),
            ViaGroup = await q.CountAsync(r => r.AssignedViaGroupId != null, ct),
            NewSinceBaseline = await q.CountAsync(r => r.IsNewSinceBaseline, ct),
            ByLevel = await q.GroupBy(r => r.ScopeLevel).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct),
            ByType = await q.GroupBy(r => r.AssignmentType).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct),
            ByUsage = await q.GroupBy(r => r.UsageEvidenceStatus).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct),
            ByRecommendation = await q.GroupBy(r => r.RecommendedAction).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct),
            ByDecision = await q.GroupBy(r => r.ReviewerDecision).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct),
            RemoveReasons = reasons.Where(r => r.RecommendedAction == RecommendedAction.Remove)
                .GroupBy(r => Reasons.GroupOf(r.RecommendationReason)).Select(g => new CountItem(g.Key, g.Count()))
                .OrderByDescending(x => x.Count).ToList(),
            ReviewReasons = reasons.Where(r => r.RecommendedAction == RecommendedAction.Review)
                .GroupBy(r => Reasons.GroupOf(r.RecommendationReason)).Select(g => new CountItem(g.Key, g.Count()))
                .OrderByDescending(x => x.Count).ToList(),
            ByDefender = await q.Where(r => r.DefenderCspmStatus != DefenderCspmStatus.None)
                .GroupBy(r => r.DefenderCspmStatus).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C, ct)
        };
    }

    public async Task<FilterOptions> GetFilterOptionsAsync(int snapshotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.RoleAssignmentSnapshots.AsNoTracking().Where(r => r.ScanSnapshotId == snapshotId);
        var subs = await q.Where(r => r.SubscriptionId != null)
            .Select(r => new { r.SubscriptionId, r.SubscriptionName }).Distinct().ToListAsync(ct);
        return new FilterOptions
        {
            Subscriptions = subs.Select(s => (s.SubscriptionId!, s.SubscriptionName ?? s.SubscriptionId!)).OrderBy(s => s.Item2).ToList(),
            Roles = await q.Select(r => r.RoleName).Distinct().OrderBy(r => r).ToListAsync(ct),
            PrincipalTypes = await q.Select(r => r.PrincipalType).Distinct().OrderBy(r => r).ToListAsync(ct)
        };
    }

    public async Task<(List<RoleAssignmentSnapshot> Rows, int Total)> QueryAsync(int snapshotId, ReviewFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = Apply(db.RoleAssignmentSnapshots.AsNoTracking().Where(r => r.ScanSnapshotId == snapshotId), filter);
        var total = await q.CountAsync(ct);
        var rows = await Order(q).Skip(Math.Max(0, page) * pageSize).Take(pageSize).ToListAsync(ct);
        return (rows, total);
    }

    public async Task<List<RoleAssignmentSnapshot>> GetRowsAsync(int snapshotId, ReviewFilter? filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.RoleAssignmentSnapshots.AsNoTracking().Where(r => r.ScanSnapshotId == snapshotId);
        if (filter is not null) q = Apply(q, filter);
        return await Order(q).ToListAsync(ct);
    }

    public async Task<Dictionary<string, UserStateSnapshot>> GetUsersAsync(int snapshotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var users = await db.UserStateSnapshots.AsNoTracking().Where(u => u.ScanSnapshotId == snapshotId).ToListAsync(ct);
        return users.ToDictionary(u => u.UserId, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetDecisionAsync(long rowId, ReviewerDecision decision, string? comment, string reviewedByUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.RoleAssignmentSnapshots.FirstAsync(r => r.Id == rowId, ct);
        row.ReviewerDecision = decision;
        row.ReviewerComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        row.ReviewedAtUtc = decision == ReviewerDecision.Pending ? null : DateTime.UtcNow;
        row.ReviewedByUpn = decision == ReviewerDecision.Pending ? null : reviewedByUpn;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Remove -> Removed, No Action -> No Action. "Review" rows need a human decision and are skipped.</summary>
    public async Task<int> AcceptRecommendationsAsync(int snapshotId, ReviewFilter filter, string reviewedByUpn, IReadOnlyCollection<long>? rowIds = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = Apply(db.RoleAssignmentSnapshots.Where(r => r.ScanSnapshotId == snapshotId), filter);
        if (rowIds is not null)
        {
            q = q.Where(r => rowIds.Contains(r.Id));
        }
        var now = DateTime.UtcNow;
        var removed = await q.Where(r => r.RecommendedAction == RecommendedAction.Remove)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewerDecision, ReviewerDecision.Removed)
                .SetProperty(r => r.ReviewedAtUtc, now).SetProperty(r => r.ReviewedByUpn, reviewedByUpn), ct);
        var kept = await q.Where(r => r.RecommendedAction == RecommendedAction.NoAction)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewerDecision, ReviewerDecision.NoAction)
                .SetProperty(r => r.ReviewedAtUtc, now).SetProperty(r => r.ReviewedByUpn, reviewedByUpn), ct);
        return removed + kept;
    }

    public async Task<int> ResetDecisionsAsync(int snapshotId, ReviewFilter filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Apply(db.RoleAssignmentSnapshots.Where(r => r.ScanSnapshotId == snapshotId), filter)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewerDecision, ReviewerDecision.Pending)
                .SetProperty(r => r.ReviewedAtUtc, (DateTime?)null)
                .SetProperty(r => r.ReviewedByUpn, (string?)null)
                .SetProperty(r => r.ReviewerComment, (string?)null), ct);
    }

    /// <summary>Stores the result of a revoke. <paramref name="remediatedByUpn"/> is set only when the change was made.</summary>
    public async Task SetRemediationAsync(IReadOnlyCollection<long> rowIds, string status, string? remediatedByUpn, CancellationToken ct = default)
    {
        if (rowIds.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        DateTime? now = remediatedByUpn is null ? null : DateTime.UtcNow;
        await db.RoleAssignmentSnapshots.Where(r => rowIds.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RemediationStatus, status)
                .SetProperty(r => r.RemediatedAtUtc, now)
                .SetProperty(r => r.RemediatedByUpn, remediatedByUpn), ct);
    }

    // ---------------------------------------------------------------- groups

    public async Task<Dictionary<string, GroupSnapshot>> GetGroupsAsync(int snapshotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var groups = await db.GroupSnapshots.AsNoTracking().Where(g => g.ScanSnapshotId == snapshotId).ToListAsync(ct);
        return groups.ToDictionary(g => g.GroupId, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<GroupMemberDecision>> GetMemberDecisionsAsync(int snapshotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.GroupMemberDecisions.AsNoTracking().Where(d => d.ScanSnapshotId == snapshotId).ToListAsync(ct);
    }

    public async Task SetMemberDecisionAsync(int snapshotId, string groupId, string memberId, string? memberName, string? memberUpn,
        GroupMemberDecisionType decision, string? comment, string reviewedByUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.GroupMemberDecisions.FirstOrDefaultAsync(d => d.ScanSnapshotId == snapshotId && d.GroupId == groupId && d.MemberId == memberId, ct);
        if (row is null)
        {
            row = new GroupMemberDecision { ScanSnapshotId = snapshotId, GroupId = groupId, MemberId = memberId };
            db.GroupMemberDecisions.Add(row);
        }
        row.MemberDisplayName = memberName;
        row.MemberUpn = memberUpn;
        row.Decision = decision;
        row.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        row.ReviewedAtUtc = decision == GroupMemberDecisionType.Pending ? null : DateTime.UtcNow;
        row.ReviewedByUpn = decision == GroupMemberDecisionType.Pending ? null : reviewedByUpn;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetMemberRemediationAsync(int snapshotId, string groupId, string memberId, string status, string? remediatedByUpn, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        DateTime? now = remediatedByUpn is null ? null : DateTime.UtcNow;
        await db.GroupMemberDecisions
            .Where(d => d.ScanSnapshotId == snapshotId && d.GroupId == groupId && d.MemberId == memberId && d.Decision == GroupMemberDecisionType.RemoveFromGroup)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RemediationStatus, status)
                .SetProperty(d => d.RemediatedAtUtc, now)
                .SetProperty(d => d.RemediatedByUpn, remediatedByUpn), ct);
    }

    // ---------------------------------------------------------------- pending changes

    /// <summary>Pending changes (approved, not yet applied) per snapshot, counted like AccessChanges.Build.</summary>
    public async Task<Dictionary<int, int>> GetPendingCountsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.RoleAssignmentSnapshots.AsNoTracking()
            .Where(r => r.ReviewerDecision == ReviewerDecision.Removed && (r.RemediationStatus == null || !r.RemediationStatus.StartsWith("Revoked")))
            .Select(r => new { r.ScanSnapshotId, r.AssignedViaGroupId, r.PrincipalId })
            .ToListAsync(ct);
        var members = await db.GroupMemberDecisions.AsNoTracking()
            .Where(d => d.Decision == GroupMemberDecisionType.RemoveFromGroup && (d.RemediationStatus == null || !d.RemediationStatus.StartsWith("Revoked")))
            .Select(d => new { d.ScanSnapshotId, d.GroupId, d.MemberId })
            .ToListAsync(ct);
        return rows.Select(r => r.ScanSnapshotId).Concat(members.Select(m => m.ScanSnapshotId)).Distinct()
            .ToDictionary(id => id, id => AccessChanges.CountChanges(
                rows.Where(r => r.ScanSnapshotId == id).Select(r => (r.AssignedViaGroupId, r.PrincipalId)),
                members.Where(m => m.ScanSnapshotId == id).Select(m => (m.GroupId, m.MemberId))));
    }

    /// <summary>Roles assigned to the groups themselves ("Role on Scope"), keyed by group id.</summary>
    public async Task<Dictionary<string, List<string>>> GetGroupAccessAsync(int snapshotId, IReadOnlyCollection<string> groupIds, CancellationToken ct = default)
    {
        if (groupIds.Count == 0) return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = groupIds.ToList();
        var rows = await db.RoleAssignmentSnapshots.AsNoTracking()
            .Where(r => r.ScanSnapshotId == snapshotId && r.AssignedViaGroupId == null && r.PrincipalType == "Group" && ids.Contains(r.PrincipalId))
            .Select(r => new { r.PrincipalId, r.RoleName, r.ScopeDisplayName, r.ScopeId })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.PrincipalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => $"{r.RoleName} on {r.ScopeDisplayName ?? r.ScopeId}").Distinct().ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Re-evaluates usage status and recommendations with another baseline and/or inactivity threshold.</summary>
    public async Task<int> RecalculateAsync(int snapshotId, int? baselineSnapshotId, int thresholdDays, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var snapshot = await db.ScanSnapshots.FirstAsync(s => s.Id == snapshotId, ct);
        var rows = await db.RoleAssignmentSnapshots.Where(r => r.ScanSnapshotId == snapshotId).ToListAsync(ct);
        var users = await db.UserStateSnapshots.AsNoTracking().Where(u => u.ScanSnapshotId == snapshotId)
            .ToDictionaryAsync(u => u.UserId, StringComparer.OrdinalIgnoreCase, ct);

        Dictionary<string, UserStateSnapshot>? baselineUsers = null;
        HashSet<string>? baselineKeys = null;
        if (baselineSnapshotId is int baselineId && baselineId != snapshotId)
        {
            baselineUsers = await db.UserStateSnapshots.AsNoTracking().Where(u => u.ScanSnapshotId == baselineId)
                .ToDictionaryAsync(u => u.UserId, StringComparer.OrdinalIgnoreCase, ct);
            baselineKeys = (await db.RoleAssignmentSnapshots.AsNoTracking().Where(r => r.ScanSnapshotId == baselineId)
                .Select(r => r.AssignmentKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        }

        foreach (var row in rows)
        {
            users.TryGetValue(row.PrincipalId, out var user);
            UserStateSnapshot? baselineUser = null;
            baselineUsers?.TryGetValue(row.PrincipalId, out baselineUser);
            RecommendationEngine.Evaluate(row, user, baselineUser, baselineKeys, snapshot.CreatedAtUtc, thresholdDays);
        }

        snapshot.BaselineSnapshotId = baselineKeys is null ? null : baselineSnapshotId;
        snapshot.InactivityThresholdDays = thresholdDays;
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<List<MoverInfo>> GetMoversAsync(int snapshotId, int baselineSnapshotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var current = await db.UserStateSnapshots.AsNoTracking().Where(u => u.ScanSnapshotId == snapshotId).ToListAsync(ct);
        var baseline = await db.UserStateSnapshots.AsNoTracking().Where(u => u.ScanSnapshotId == baselineSnapshotId)
            .ToDictionaryAsync(u => u.UserId, StringComparer.OrdinalIgnoreCase, ct);
        var counts = await db.RoleAssignmentSnapshots.AsNoTracking().Where(r => r.ScanSnapshotId == snapshotId)
            .GroupBy(r => r.PrincipalId).Select(g => new { g.Key, C = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.C, StringComparer.OrdinalIgnoreCase, ct);

        var result = new List<MoverInfo>();
        foreach (var u in current)
        {
            baseline.TryGetValue(u.UserId, out var b);
            var changes = new List<string>();
            if (!u.ExistsInDirectory) changes.Add("Deleted from Entra ID");
            else if (!u.AccountEnabled) changes.Add("Account disabled");
            if (b is not null && u.ExistsInDirectory)
            {
                if (!string.Equals((u.Department ?? "").Trim(), (b.Department ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    changes.Add($"Department: {b.Department ?? "-"} -> {u.Department ?? "-"}");
                if (!string.Equals(u.ManagerId ?? "", b.ManagerId ?? "", StringComparison.OrdinalIgnoreCase))
                    changes.Add($"Manager: {b.ManagerDisplayName ?? b.ManagerUpn ?? "-"} -> {u.ManagerDisplayName ?? u.ManagerUpn ?? "-"}");
            }
            if (changes.Count > 0)
            {
                result.Add(new MoverInfo { Current = u, Baseline = b, Change = string.Join("; ", changes), Assignments = counts.GetValueOrDefault(u.UserId) });
            }
        }
        return result.OrderBy(m => m.Current.DisplayName).ToList();
    }

    public async Task DeleteSnapshotAsync(int snapshotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ScanSnapshots.Where(s => s.Id == snapshotId).ExecuteDeleteAsync(ct);
    }

    public async Task<(long DatabaseBytes, List<TableSize> Tables)> GetStorageAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var size = await GetDatabaseSizeAsync(db, ct);
        var tables = await db.Database.SqlQueryRaw<TableSize>(
                "SELECT relname AS \"Name\", pg_total_relation_size(relid) AS \"Bytes\", n_live_tup AS \"Rows\" " +
                "FROM pg_stat_user_tables ORDER BY pg_total_relation_size(relid) DESC")
            .ToListAsync(ct);
        return (size, tables);
    }

    public static async Task<long> GetDatabaseSizeAsync(AppDbContext db, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync(ct);

    private static IQueryable<RoleAssignmentSnapshot> Order(IQueryable<RoleAssignmentSnapshot> q) =>
        q.OrderBy(r => r.RecommendedAction == RecommendedAction.Remove ? 0 : r.RecommendedAction == RecommendedAction.Review ? 1 : 2)
            .ThenBy(r => r.PrincipalDisplayName)
            .ThenBy(r => r.ScopeLevel)
            .ThenBy(r => r.RoleName)
            .ThenBy(r => r.Id);

    public static IQueryable<RoleAssignmentSnapshot> Apply(IQueryable<RoleAssignmentSnapshot> q, ReviewFilter f)
    {
        if (f.ScopeLevel is { } level) q = q.Where(r => r.ScopeLevel == level);
        if (!string.IsNullOrEmpty(f.SubscriptionId)) q = q.Where(r => r.SubscriptionId == f.SubscriptionId);
        if (!string.IsNullOrEmpty(f.RoleName)) q = q.Where(r => r.RoleName == f.RoleName);
        if (!string.IsNullOrEmpty(f.PrincipalType)) q = q.Where(r => r.PrincipalType == f.PrincipalType);
        if (f.Usage is { } usage) q = q.Where(r => r.UsageEvidenceStatus == usage);
        if (f.OnlyNewSinceBaseline) q = q.Where(r => r.IsNewSinceBaseline);

        q = f.AssignmentFilter switch
        {
            "Direct" => q.Where(r => r.AssignedViaGroupId == null),
            "Via Group" => q.Where(r => r.AssignedViaGroupId != null),
            "PIM Eligible" => q.Where(r => r.AssignmentType == AssignmentType.PimEligible),
            "PIM Active" => q.Where(r => r.AssignmentType == AssignmentType.PimActive),
            "Permanent" => q.Where(r => r.AssignmentType == AssignmentType.Permanent),
            _ => q
        };
        q = f.Recommendation switch
        {
            "Remove" => q.Where(r => r.RecommendedAction == RecommendedAction.Remove),
            "Review" => q.Where(r => r.RecommendedAction == RecommendedAction.Review),
            "NoAction" => q.Where(r => r.RecommendedAction == RecommendedAction.NoAction),
            _ => q
        };
        q = f.Decision switch
        {
            "Pending" => q.Where(r => r.ReviewerDecision == ReviewerDecision.Pending),
            "NoAction" => q.Where(r => r.ReviewerDecision == ReviewerDecision.NoAction),
            "Removed" => q.Where(r => r.ReviewerDecision == ReviewerDecision.Removed),
            _ => q
        };

        if (f.Defender == "Any")
        {
            q = q.Where(r => r.DefenderCspmStatus != DefenderCspmStatus.None);
        }
        else if (f.Defender == nameof(DefenderCspmStatus.None))
        {
            q = q.Where(r => r.DefenderCspmStatus == DefenderCspmStatus.None);
        }
        else if (Enum.TryParse<DefenderCspmStatus>(f.Defender, out var defender))
        {
            // A row can have several findings: the filter checks the full list, not only the primary status.
            var token = defender.ToString();
            q = q.Where(r => r.DefenderCspmFindings != null && r.DefenderCspmFindings.Contains(token));
        }

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var pattern = $"%{f.Search.Trim()}%";
            q = q.Where(r => EF.Functions.ILike(r.PrincipalDisplayName ?? "", pattern)
                             || EF.Functions.ILike(r.PrincipalUpn ?? "", pattern)
                             || EF.Functions.ILike(r.ScopeDisplayName ?? "", pattern)
                             || EF.Functions.ILike(r.AssignedViaGroupName ?? "", pattern)
                             || EF.Functions.ILike(r.RoleName, pattern));
        }
        return q;
    }
}
