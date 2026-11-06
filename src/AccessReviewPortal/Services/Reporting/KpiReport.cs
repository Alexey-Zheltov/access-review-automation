using System.Globalization;
using AccessReviewPortal.Domain;

namespace AccessReviewPortal.Services.Reporting;

/// <summary>Report period in whole UTC days. Both days are included.</summary>
public sealed record ReportPeriod(DateOnly From, DateOnly To)
{
    public const string Custom = "custom";

    public static readonly (string Key, string Name)[] Presets =
    [
        ("quarter", "This quarter"),
        ("prev-quarter", "Previous quarter"),
        ("30d", "Last 30 days"),
        ("90d", "Last 90 days"),
        ("ytd", "Year to date"),
        ("12m", "Last 12 months")
    ];

    public DateTime FromUtc => From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>First moment after the period.</summary>
    public DateTime ToUtcExclusive => To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    public bool Contains(DateTime? utc) => utc is { } t && t >= FromUtc && t < ToUtcExclusive;

    public override string ToString() => $"{Iso(From)} to {Iso(To)}";

    /// <summary>Query string of the period for links and the Excel download.</summary>
    public string Query => $"from={Iso(From)}&to={Iso(To)}";

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Unknown keys give the current quarter.</summary>
    public static ReportPeriod FromPreset(string? key, DateOnly today)
    {
        var quarterStart = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        return key switch
        {
            "prev-quarter" => new(quarterStart.AddMonths(-3), quarterStart.AddDays(-1)),
            "30d" => new(today.AddDays(-29), today),
            "90d" => new(today.AddDays(-89), today),
            "ytd" => new(new DateOnly(today.Year, 1, 1), today),
            "12m" => new(today.AddYears(-1).AddDays(1), today),
            _ => new(quarterStart, quarterStart.AddMonths(3).AddDays(-1))
        };
    }

    /// <summary>Reads "from" and "to" (yyyy-MM-dd). Missing or invalid values give the current quarter.</summary>
    public static ReportPeriod Parse(string? from, string? to, DateOnly today)
    {
        if (TryParseDate(from, out var f) && TryParseDate(to, out var t))
        {
            return f <= t ? new(f, t) : new(t, f);
        }
        return FromPreset(null, today);
    }

    public static bool TryParseDate(string? s, out DateOnly date) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}

public sealed record SnapshotRef(int Id, string Label, DateTime CreatedAtUtc, int ThresholdDays)
{
    public string Text => $"#{Id} {Label} ({CreatedAtUtc:yyyy-MM-dd HH:mm} UTC)";
}

/// <summary>Numbers of one snapshot (state at the time of the scan).</summary>
public sealed class SnapshotKpi
{
    public required SnapshotRef Snapshot { get; init; }
    public int UsersWithRoles { get; init; }

    /// <summary>Users with at least one assignment without use in the inactivity threshold.</summary>
    public int UsersWithUnusedRoles { get; init; }

    public int UnusedUserAssignments { get; init; }

    /// <summary>Role assignments of users (rows), and how many of them have usage evidence or only sign-ins.</summary>
    public int UserAssignments { get; init; }
    public int UserAssignmentsUsed { get; init; }
    public int UserAssignmentsSignInOnly { get; init; }

    public int ServicePrincipalsWithRoles { get; init; }
    public int ServicePrincipalsWithUnusedRoles { get; init; }

    /// <summary>Rows with a Remove or Review recommendation.</summary>
    public int RowsToReview { get; init; }

    /// <summary>Rows with a Remove or Review recommendation that have a reviewer decision.</summary>
    public int RowsReviewed { get; init; }

    /// <summary>Changes from this snapshot applied in the report period.</summary>
    public int ChangesApplied { get; set; }

    public int UnusedPercent => KpiCalculator.Percent(UsersWithUnusedRoles, UsersWithRoles);
    public int ReviewedPercent => KpiCalculator.Percent(RowsReviewed, RowsToReview);
}

/// <summary>Role assignments of one user in one snapshot.</summary>
public sealed record UserRoleState(string UserId, string? Department, int Assignments, int UnusedAssignments);

/// <summary>Reviewer decisions on role assignment rows in the period, counted in the database.</summary>
public sealed record ReviewerRowCounts(string? Reviewer, int ApprovedRemoval, int Kept, DateTime? LastDecisionUtc);

/// <summary>Reviewer decisions on role assignment rows per UTC day, counted in the database.</summary>
public sealed record DailyDecisionCounts(DateOnly Day, int ApprovedRemoval, int Kept);

/// <summary>Review activity in one day, week or month of the period.</summary>
public sealed class ActivityBucket
{
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
    public int ApprovedRemoval { get; set; }

    /// <summary>Keep and Keep in Group decisions, exceptions included.</summary>
    public int Kept { get; set; }

    /// <summary>Keep decisions that are exceptions (see <see cref="KpiCalculator.IsException"/>).</summary>
    public int Exceptions { get; set; }

    public int ChangesApplied { get; set; }

    /// <summary>Distinct users cleaned up from the start of the period to the end of this bucket.</summary>
    public int UsersCleanedUpToDate { get; set; }

    /// <summary>Exception decisions from the start of the period to the end of this bucket.</summary>
    public int ExceptionsToDate { get; set; }

    public int Decisions => ApprovedRemoval + Kept;
}

public enum ReviewItemKind
{
    RoleAssignment,
    GroupMembership
}

/// <summary>A reviewer decision on a role assignment row or on a group member, with the result of Apply.</summary>
public sealed class DecisionFact
{
    public required int SnapshotId { get; init; }
    public required ReviewItemKind Kind { get; init; }
    public required string PrincipalId { get; init; }
    public required string PrincipalType { get; init; }
    public string? DisplayName { get; init; }
    public string? Upn { get; init; }

    /// <summary>Department of the user in the snapshot of the decision.</summary>
    public string? Department { get; init; }

    /// <summary>"Role on scope" for a role assignment, "Member of group ..." for a group member.</summary>
    public required string Access { get; init; }

    /// <summary>The same in every snapshot (assignment key, or group and member). An exception is counted once per item.</summary>
    public required string ItemKey { get; init; }

    /// <summary>Group of the membership that Apply removes. Null for a direct role assignment.</summary>
    public string? GroupId { get; init; }

    /// <summary>The change that Apply makes: one per direct assignment, one per group and member (like Changes Pending).</summary>
    public required string ChangeKey { get; init; }

    public required string ChangeText { get; init; }

    public UsageEvidenceStatus Usage { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public RecommendedAction Recommendation { get; init; }
    public string? Reason { get; init; }

    /// <summary>True for "Approve Removal" / "Remove from Group", false for "Keep" / "Keep in Group".</summary>
    public bool ApprovedRemoval { get; init; }

    public string? Comment { get; init; }
    public DateTime? ReviewedAtUtc { get; init; }
    public string? ReviewedBy { get; init; }

    public bool Applied { get; init; }
    public DateTime? AppliedAtUtc { get; init; }
    public string? AppliedBy { get; init; }

    public bool IsUser => PrincipalType == "User";
    public bool IsMembershipChange => GroupId is not null;

    public static string MemberChangeKey(int snapshotId, string groupId, string memberId) =>
        $"{snapshotId}:member:{groupId}:{memberId}".ToLowerInvariant();
}

public sealed class ReviewerKpi
{
    public required string Reviewer { get; init; }
    public int Decisions { get; set; }
    public int ApprovedRemoval { get; set; }
    public int Kept { get; set; }
    public int Exceptions { get; set; }

    /// <summary>Users with at least one "Approve Removal" / "Remove from Group" by this reviewer.</summary>
    public int UsersApprovedForRemoval { get; set; }

    /// <summary>Users with at least one change applied by this reviewer (Apply on Changes Pending).</summary>
    public int UsersCleanedUp { get; set; }

    public int ChangesApplied { get; set; }
    public DateTime? LastActivityUtc { get; set; }
}

public sealed class DepartmentKpi
{
    public required string Department { get; init; }
    public int UsersWithRoles { get; set; }
    public int UsersWithUnusedRoles { get; set; }
    public int UnusedAssignments { get; set; }

    /// <summary>Null when there is no snapshot at the start of the period.</summary>
    public int? UsersWithUnusedRolesAtStart { get; set; }

    public int UsersCleanedUp { get; set; }
    public int UsersWithExceptions { get; set; }
    public int UnusedPercent => KpiCalculator.Percent(UsersWithUnusedRoles, UsersWithRoles);
}

public sealed class KpiReport
{
    public required ReportPeriod Period { get; init; }
    public DateTime GeneratedAtUtc { get; init; }
    public string? GeneratedBy { get; init; }

    /// <summary>Last completed snapshot up to the end of the period. The state numbers come from it.</summary>
    public SnapshotKpi? End { get; init; }

    /// <summary>Last snapshot before the period, or the first one in it. Null when it is the same as <see cref="End"/>.</summary>
    public SnapshotKpi? Start { get; init; }

    /// <summary>Snapshots taken in the period, oldest first.</summary>
    public List<SnapshotKpi> Snapshots { get; init; } = [];

    public int Decisions { get; init; }
    public int UsersCleanedUp { get; init; }
    public int ServicePrincipalsCleanedUp { get; init; }
    public int ChangesApplied { get; init; }
    public int RoleAssignmentsRemoved { get; init; }
    public int GroupMembershipsRemoved { get; init; }

    /// <summary>Changes approved in the period that are not applied yet.</summary>
    public int ChangesWaitingForApply { get; init; }

    public int UsersWithExceptions { get; init; }
    public int ServicePrincipalsWithExceptions { get; init; }
    public int ExceptionsWithoutJustification { get; init; }

    public List<ReviewerKpi> Reviewers { get; init; } = [];
    public List<DepartmentKpi> Departments { get; init; } = [];

    /// <summary>Review activity from the start of the period to today (or to the end of a past period).</summary>
    public List<ActivityBucket> Activity { get; init; } = [];

    /// <summary>"day", "week" or "month": the size of the activity buckets.</summary>
    public string ActivityUnit { get; init; } = "day";

    /// <summary>One line per item, newest first.</summary>
    public List<DecisionFact> Exceptions { get; init; } = [];

    /// <summary>One line per applied change, newest first.</summary>
    public List<DecisionFact> Removals { get; init; } = [];
}

/// <summary>Pure KPI calculation, separated from the database queries for unit tests.</summary>
public static class KpiCalculator
{
    public const string NoDepartment = "(no department)";
    public const string NotRecorded = "(not recorded)";

    /// <summary>
    /// Exception: the reviewer kept the access although the role was not used in the inactivity threshold,
    /// or the portal recommended removal (leaver, mover, denied in self-review).
    /// </summary>
    public static bool IsException(DecisionFact f) =>
        !f.ApprovedRemoval && (f.Usage == UsageEvidenceStatus.Unused90Days || f.Recommendation == RecommendedAction.Remove);

    public static int Percent(int part, int whole) => whole == 0 ? 0 : (int)Math.Round(100.0 * part / whole);

    public static KpiReport Build(
        ReportPeriod period,
        DateTime generatedAtUtc,
        string? generatedBy,
        IReadOnlyList<SnapshotKpi> snapshots,
        int? endSnapshotId,
        int? startSnapshotId,
        IReadOnlyList<UserRoleState> endUsers,
        IReadOnlyList<UserRoleState>? startUsers,
        IReadOnlyList<ReviewerRowCounts> rowCounts,
        IReadOnlyList<DailyDecisionCounts> dailyCounts,
        IReadOnlyList<DecisionFact> facts)
    {
        var decided = facts.Where(f => period.Contains(f.ReviewedAtUtc)).ToList();
        var approved = decided.Where(f => f.ApprovedRemoval).ToList();

        // The latest decision on an item in the period counts: an exception later replaced by a removal is not an exception.
        var exceptions = decided
            .GroupBy(f => f.ItemKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.ReviewedAtUtc).First())
            .Where(IsException)
            .OrderByDescending(f => f.ReviewedAtUtc)
            .ToList();

        var removals = facts
            .Where(f => f.Applied && period.Contains(f.AppliedAtUtc))
            .GroupBy(f => f.ChangeKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.AppliedAtUtc).First())
            .OrderByDescending(f => f.AppliedAtUtc)
            .ToList();

        var waiting = approved
            .GroupBy(f => f.ChangeKey, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Any(f => !f.Applied));

        var reviewers = BuildReviewers(rowCounts, decided, approved, exceptions, removals);
        var departments = BuildDepartments(endUsers, startUsers, exceptions, removals);
        var (activity, activityUnit) = BuildActivity(period, DateOnly.FromDateTime(generatedAtUtc), dailyCounts, decided, removals);

        var byId = snapshots.ToDictionary(s => s.Snapshot.Id);
        foreach (var g in removals.GroupBy(f => f.SnapshotId))
        {
            if (byId.TryGetValue(g.Key, out var s)) s.ChangesApplied = g.Count();
        }

        return new KpiReport
        {
            Period = period,
            GeneratedAtUtc = generatedAtUtc,
            GeneratedBy = generatedBy,
            End = endSnapshotId is int e ? byId.GetValueOrDefault(e) : null,
            Start = startSnapshotId is int s0 && s0 != endSnapshotId ? byId.GetValueOrDefault(s0) : null,
            Snapshots = snapshots.Where(s => period.Contains(s.Snapshot.CreatedAtUtc)).OrderBy(s => s.Snapshot.CreatedAtUtc).ToList(),
            Decisions = reviewers.Sum(r => r.Decisions),
            UsersCleanedUp = DistinctPrincipals(removals.Where(f => f.IsUser)),
            ServicePrincipalsCleanedUp = DistinctPrincipals(removals.Where(f => f.PrincipalType == "ServicePrincipal")),
            ChangesApplied = removals.Count,
            RoleAssignmentsRemoved = removals.Count(f => !f.IsMembershipChange),
            GroupMembershipsRemoved = removals.Count(f => f.IsMembershipChange),
            ChangesWaitingForApply = waiting,
            UsersWithExceptions = DistinctPrincipals(exceptions.Where(f => f.IsUser)),
            ServicePrincipalsWithExceptions = DistinctPrincipals(exceptions.Where(f => f.PrincipalType == "ServicePrincipal")),
            ExceptionsWithoutJustification = exceptions.Count(f => string.IsNullOrWhiteSpace(f.Comment)),
            Reviewers = reviewers,
            Departments = departments,
            Activity = activity,
            ActivityUnit = activityUnit,
            Exceptions = exceptions,
            Removals = removals
        };
    }

    /// <summary>
    /// Buckets from the start of the period to today (or the end of a past period): days up to 62 days, weeks up to
    /// 371 days, months above. Role assignment decisions come counted per day; group member decisions, exceptions
    /// and applied changes come from the facts.
    /// </summary>
    private static (List<ActivityBucket> Buckets, string Unit) BuildActivity(ReportPeriod period, DateOnly today,
        IReadOnlyList<DailyDecisionCounts> dailyCounts, List<DecisionFact> decided, List<DecisionFact> removals)
    {
        var last = period.To < today ? period.To : today;
        if (last < period.From) last = period.From;
        var days = last.DayNumber - period.From.DayNumber + 1;
        var unit = days <= 62 ? "day" : days <= 371 ? "week" : "month";

        var buckets = new List<ActivityBucket>();
        var start = unit == "month" ? new DateOnly(period.From.Year, period.From.Month, 1) : period.From;
        while (start <= last)
        {
            var next = unit switch { "day" => start.AddDays(1), "week" => start.AddDays(7), _ => start.AddMonths(1) };
            var end = next.AddDays(-1);
            buckets.Add(new ActivityBucket { Start = start < period.From ? period.From : start, End = end > last ? last : end });
            start = next;
        }

        ActivityBucket? Of(DateOnly day) => day < period.From || day > last ? null : buckets.FindLast(b => b.Start <= day);
        ActivityBucket? OfTime(DateTime? utc) => utc is { } t ? Of(DateOnly.FromDateTime(t)) : null;

        foreach (var c in dailyCounts)
        {
            if (Of(c.Day) is not { } b) continue;
            b.ApprovedRemoval += c.ApprovedRemoval;
            b.Kept += c.Kept;
        }
        foreach (var f in decided.Where(f => f.Kind == ReviewItemKind.GroupMembership))
        {
            if (OfTime(f.ReviewedAtUtc) is not { } b) continue;
            if (f.ApprovedRemoval) b.ApprovedRemoval++;
            else b.Kept++;
        }
        foreach (var f in decided.Where(IsException))
        {
            if (OfTime(f.ReviewedAtUtc) is { } b) b.Exceptions++;
        }
        foreach (var f in removals)
        {
            if (OfTime(f.AppliedAtUtc) is { } b) b.ChangesApplied++;
        }

        var cleaned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userRemovals = removals.Where(f => f.IsUser && f.AppliedAtUtc is not null).OrderBy(f => f.AppliedAtUtc).ToList();
        var nextRemoval = 0;
        var exceptionsToDate = 0;
        foreach (var b in buckets)
        {
            while (nextRemoval < userRemovals.Count && DateOnly.FromDateTime(userRemovals[nextRemoval].AppliedAtUtc!.Value) <= b.End)
            {
                cleaned.Add(userRemovals[nextRemoval++].PrincipalId);
            }
            exceptionsToDate += b.Exceptions;
            b.UsersCleanedUpToDate = cleaned.Count;
            b.ExceptionsToDate = exceptionsToDate;
        }
        return (buckets, unit);
    }

    private static List<ReviewerKpi> BuildReviewers(IReadOnlyList<ReviewerRowCounts> rowCounts, List<DecisionFact> decided,
        List<DecisionFact> approved, List<DecisionFact> exceptions, List<DecisionFact> removals)
    {
        var result = new Dictionary<string, ReviewerKpi>(StringComparer.OrdinalIgnoreCase);
        ReviewerKpi Of(string? upn)
        {
            var key = string.IsNullOrWhiteSpace(upn) ? NotRecorded : upn.Trim();
            if (!result.TryGetValue(key, out var r)) result[key] = r = new ReviewerKpi { Reviewer = key };
            return r;
        }

        foreach (var c in rowCounts)
        {
            var r = Of(c.Reviewer);
            r.ApprovedRemoval += c.ApprovedRemoval;
            r.Kept += c.Kept;
            r.Decisions += c.ApprovedRemoval + c.Kept;
            r.LastActivityUtc = Latest(r.LastActivityUtc, c.LastDecisionUtc);
        }

        // Role assignment rows are counted in the database; group member decisions come with the facts.
        foreach (var f in decided.Where(f => f.Kind == ReviewItemKind.GroupMembership))
        {
            var r = Of(f.ReviewedBy);
            r.Decisions++;
            if (f.ApprovedRemoval) r.ApprovedRemoval++;
            else r.Kept++;
            r.LastActivityUtc = Latest(r.LastActivityUtc, f.ReviewedAtUtc);
        }

        foreach (var g in approved.Where(f => f.IsUser).GroupBy(f => Of(f.ReviewedBy)))
        {
            g.Key.UsersApprovedForRemoval = DistinctPrincipals(g);
        }
        foreach (var f in exceptions)
        {
            Of(f.ReviewedBy).Exceptions++;
        }
        foreach (var g in removals.GroupBy(f => Of(f.AppliedBy)))
        {
            g.Key.ChangesApplied = g.Count();
            g.Key.UsersCleanedUp = DistinctPrincipals(g.Where(f => f.IsUser));
            g.Key.LastActivityUtc = Latest(g.Key.LastActivityUtc, g.Max(f => f.AppliedAtUtc));
        }

        return result.Values
            .OrderBy(r => r.Reviewer == NotRecorded)
            .ThenByDescending(r => r.UsersCleanedUp)
            .ThenByDescending(r => r.UsersApprovedForRemoval)
            .ThenByDescending(r => r.Decisions)
            .ThenBy(r => r.Reviewer, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<DepartmentKpi> BuildDepartments(IReadOnlyList<UserRoleState> endUsers, IReadOnlyList<UserRoleState>? startUsers,
        List<DecisionFact> exceptions, List<DecisionFact> removals)
    {
        var result = new Dictionary<string, DepartmentKpi>(StringComparer.OrdinalIgnoreCase);
        DepartmentKpi Of(string? department)
        {
            var key = string.IsNullOrWhiteSpace(department) ? NoDepartment : department.Trim();
            if (!result.TryGetValue(key, out var d)) result[key] = d = new DepartmentKpi { Department = key };
            return d;
        }

        foreach (var u in endUsers)
        {
            var d = Of(u.Department);
            d.UsersWithRoles++;
            if (u.UnusedAssignments > 0)
            {
                d.UsersWithUnusedRoles++;
                d.UnusedAssignments += u.UnusedAssignments;
            }
        }
        foreach (var u in startUsers?.Where(u => u.UnusedAssignments > 0) ?? [])
        {
            var d = Of(u.Department);
            d.UsersWithUnusedRolesAtStart = (d.UsersWithUnusedRolesAtStart ?? 0) + 1;
        }
        foreach (var g in removals.Where(f => f.IsUser).GroupBy(f => Of(f.Department)))
        {
            g.Key.UsersCleanedUp = DistinctPrincipals(g);
        }
        foreach (var g in exceptions.Where(f => f.IsUser).GroupBy(f => Of(f.Department)))
        {
            g.Key.UsersWithExceptions = DistinctPrincipals(g);
        }
        if (startUsers is not null)
        {
            foreach (var d in result.Values) d.UsersWithUnusedRolesAtStart ??= 0;
        }

        return result.Values
            .OrderByDescending(d => d.UsersWithUnusedRoles)
            .ThenByDescending(d => d.UsersWithRoles)
            .ThenBy(d => d.Department, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int DistinctPrincipals(IEnumerable<DecisionFact> facts) =>
        facts.Select(f => f.PrincipalId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static DateTime? Latest(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
