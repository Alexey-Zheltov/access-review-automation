using AccessReviewPortal.Domain;
using AccessReviewPortal.Services.Reporting;

namespace AccessReviewPortal.Tests;

public class ReportPeriodTests
{
    [Fact]
    public void Presets_use_whole_utc_days()
    {
        var today = new DateOnly(2026, 11, 6);
        Assert.Equal(new ReportPeriod(new(2026, 10, 1), new(2026, 12, 31)), ReportPeriod.FromPreset("quarter", today));
        Assert.Equal(new ReportPeriod(new(2026, 7, 1), new(2026, 9, 30)), ReportPeriod.FromPreset("prev-quarter", today));
        Assert.Equal(new ReportPeriod(new(2026, 10, 8), today), ReportPeriod.FromPreset("30d", today));
        Assert.Equal(new ReportPeriod(new(2026, 1, 1), today), ReportPeriod.FromPreset("ytd", today));
        Assert.Equal(new ReportPeriod(new(2025, 11, 7), today), ReportPeriod.FromPreset("12m", today));
        Assert.Equal(new ReportPeriod(new(2025, 10, 1), new(2025, 12, 31)), ReportPeriod.FromPreset("prev-quarter", new DateOnly(2026, 2, 15)));
        Assert.Equal(ReportPeriod.FromPreset("quarter", today), ReportPeriod.FromPreset("unknown", today));
    }

    [Fact]
    public void The_last_day_is_included()
    {
        var p = new ReportPeriod(new(2026, 10, 1), new(2026, 10, 31));
        Assert.True(p.Contains(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(p.Contains(new DateTime(2026, 10, 31, 23, 59, 59, DateTimeKind.Utc)));
        Assert.False(p.Contains(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(p.Contains(new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Utc)));
        Assert.False(p.Contains(null));
    }

    [Fact]
    public void Parse_swaps_reversed_dates_and_falls_back_to_the_quarter()
    {
        var today = new DateOnly(2026, 11, 6);
        Assert.Equal(new ReportPeriod(new(2026, 10, 1), new(2026, 10, 31)), ReportPeriod.Parse("2026-10-31", "2026-10-01", today));
        Assert.Equal(ReportPeriod.FromPreset("quarter", today), ReportPeriod.Parse("31.10.2026", null, today));
        Assert.Equal("from=2026-10-01&to=2026-10-31", new ReportPeriod(new(2026, 10, 1), new(2026, 10, 31)).Query);
    }
}

public class KpiCalculatorTests
{
    private const string Anna = "anna@contoso.com";
    private const string Bob = "bob@contoso.com";
    private const string Carl = "carl@contoso.com";

    private static readonly ReportPeriod Q4 = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31));
    private static readonly DateTime Nov1 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Nov5 = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sep20 = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private static long _nextRow = 1;

    private static DecisionFact Fact(
        string principal,
        bool remove,
        UsageEvidenceStatus usage = UsageEvidenceStatus.Unused90Days,
        RecommendedAction recommendation = RecommendedAction.Remove,
        string type = "User",
        int snapshot = 8,
        string? group = null,
        string? item = null,
        ReviewItemKind kind = ReviewItemKind.RoleAssignment,
        string? reviewer = Anna,
        DateTime? reviewedAt = null,
        bool applied = false,
        string? appliedBy = null,
        DateTime? appliedAt = null,
        string? department = "Finance",
        string? comment = null) => new()
    {
        SnapshotId = snapshot,
        Kind = kind,
        PrincipalId = principal,
        PrincipalType = type,
        DisplayName = principal,
        Upn = principal + "@contoso.com",
        Department = department,
        Access = group is null ? "Contributor on Apps / rg-test" : $"Member of group '{group}'",
        ItemKey = item ?? Guid.NewGuid().ToString("N"),
        GroupId = group,
        ChangeKey = group is null ? $"{snapshot}:row:{_nextRow++}" : DecisionFact.MemberChangeKey(snapshot, group, principal),
        ChangeText = group is null ? "Contributor on Apps / rg-test" : $"Member of group '{group}'",
        Usage = usage,
        Recommendation = recommendation,
        ApprovedRemoval = remove,
        Comment = comment,
        ReviewedAtUtc = reviewedAt ?? Nov5,
        ReviewedBy = reviewer,
        Applied = applied,
        AppliedAtUtc = applied ? appliedAt ?? Nov5 : null,
        AppliedBy = applied ? appliedBy : null
    };

    private static KpiReport Build(IReadOnlyList<DecisionFact> facts, IReadOnlyList<ReviewerRowCounts>? counts = null,
        IReadOnlyList<UserRoleState>? endUsers = null, IReadOnlyList<UserRoleState>? startUsers = null,
        IReadOnlyList<SnapshotKpi>? snapshots = null, int? endId = null, int? startId = null,
        IReadOnlyList<DailyDecisionCounts>? daily = null, ReportPeriod? period = null, DateTime? now = null) =>
        KpiCalculator.Build(period ?? Q4, now ?? Nov5, "tester@contoso.com", snapshots ?? [], endId, startId, endUsers ?? [], startUsers,
            counts ?? [], daily ?? [], facts);

    [Fact]
    public void Keep_on_an_unused_role_or_on_a_removal_recommendation_is_an_exception()
    {
        var unusedKept = Fact("u1", remove: false, comment: "Break-glass account");
        var moverKept = Fact("u2", remove: false, usage: UsageEvidenceStatus.ConfirmedUsed, recommendation: RecommendedAction.Remove);
        var signInOnlyKept = Fact("u3", remove: false, usage: UsageEvidenceStatus.PortalSignInOnlyNoWrite, recommendation: RecommendedAction.Review);
        var usedKept = Fact("u4", remove: false, usage: UsageEvidenceStatus.ConfirmedUsed, recommendation: RecommendedAction.NoAction);
        var unusedRemoved = Fact("u5", remove: true);
        var idleServicePrincipalKept = Fact("sp1", remove: false, recommendation: RecommendedAction.Review, type: "ServicePrincipal");

        Assert.True(KpiCalculator.IsException(unusedKept));
        Assert.True(KpiCalculator.IsException(moverKept));
        Assert.False(KpiCalculator.IsException(signInOnlyKept));
        Assert.False(KpiCalculator.IsException(usedKept));
        Assert.False(KpiCalculator.IsException(unusedRemoved));
        Assert.True(KpiCalculator.IsException(idleServicePrincipalKept));

        var report = Build([unusedKept, moverKept, signInOnlyKept, usedKept, unusedRemoved, idleServicePrincipalKept]);
        Assert.Equal(3, report.Exceptions.Count);
        Assert.Equal(2, report.UsersWithExceptions);
        Assert.Equal(1, report.ServicePrincipalsWithExceptions);
        Assert.Equal(2, report.ExceptionsWithoutJustification);
    }

    [Fact]
    public void An_exception_renewed_later_counts_once_and_a_later_removal_ends_it()
    {
        var report = Build(
        [
            Fact("u1", remove: false, snapshot: 7, item: "k1", reviewer: Anna, reviewedAt: Nov1),
            Fact("u1", remove: false, snapshot: 8, item: "k1", reviewer: Bob, reviewedAt: Nov5, comment: "Seasonal work"),
            Fact("u2", remove: false, snapshot: 7, item: "k2", reviewer: Anna, reviewedAt: Nov1),
            Fact("u2", remove: true, snapshot: 8, item: "k2", reviewer: Anna, reviewedAt: Nov5),
            Fact("u3", remove: false, item: "k3", reviewedAt: Sep20)
        ]);

        var exception = Assert.Single(report.Exceptions);
        Assert.Equal("u1", exception.PrincipalId);
        Assert.Equal(Bob, exception.ReviewedBy);
        Assert.Equal(0, report.ExceptionsWithoutJustification);
        Assert.Equal(1, report.Reviewers.Single(r => r.Reviewer == Bob).Exceptions);
        Assert.Equal(0, report.Reviewers.Single(r => r.Reviewer == Anna).Exceptions);
    }

    [Fact]
    public void Changes_are_counted_like_changes_pending()
    {
        var report = Build(
        [
            // u1 leaves group g1: two roles via the group and a "Remove from Group" decision are one change.
            Fact("u1", remove: true, group: "g1", applied: true, appliedBy: Carl),
            Fact("u1", remove: true, group: "g1", applied: true, appliedBy: Carl),
            Fact("u1", remove: true, group: "g1", kind: ReviewItemKind.GroupMembership, applied: true, appliedBy: Carl),
            Fact("u1", remove: true, applied: true, appliedBy: Carl),
            Fact("sp1", remove: true, type: "ServicePrincipal", recommendation: RecommendedAction.Review, applied: true, appliedBy: Carl),
            Fact("u2", remove: true),
            Fact("u3", remove: true, reviewedAt: Sep20, applied: true, appliedBy: Carl, appliedAt: Sep20)
        ]);

        Assert.Equal(3, report.ChangesApplied);
        Assert.Equal(2, report.RoleAssignmentsRemoved);
        Assert.Equal(1, report.GroupMembershipsRemoved);
        Assert.Equal(1, report.UsersCleanedUp);
        Assert.Equal(1, report.ServicePrincipalsCleanedUp);
        Assert.Equal(1, report.ChangesWaitingForApply);
        Assert.Equal(3, report.Removals.Count);
    }

    [Fact]
    public void Reviewers_get_their_decisions_approvals_and_applied_changes()
    {
        var report = Build(
            [
                Fact("u1", remove: true, reviewer: Anna, applied: true, appliedBy: Carl),
                Fact("u1", remove: true, reviewer: Anna, applied: true, appliedBy: Carl),
                Fact("u2", remove: true, reviewer: Anna),
                Fact("u4", remove: false, usage: UsageEvidenceStatus.ConfirmedUsed, recommendation: RecommendedAction.NoAction,
                    group: "g1", kind: ReviewItemKind.GroupMembership, reviewer: Anna)
            ],
            counts: [new ReviewerRowCounts(Anna, 3, 2, Nov5), new ReviewerRowCounts(null, 1, 0, Nov1)]);

        var anna = report.Reviewers.Single(r => r.Reviewer == Anna);
        Assert.Equal(6, anna.Decisions);
        Assert.Equal(3, anna.ApprovedRemoval);
        Assert.Equal(3, anna.Kept);
        Assert.Equal(2, anna.UsersApprovedForRemoval);
        Assert.Equal(0, anna.UsersCleanedUp);

        var carl = report.Reviewers.Single(r => r.Reviewer == Carl);
        Assert.Equal(0, carl.Decisions);
        Assert.Equal(1, carl.UsersCleanedUp);
        Assert.Equal(2, carl.ChangesApplied);

        Assert.Equal(KpiCalculator.NotRecorded, report.Reviewers[^1].Reviewer);
        Assert.Equal(1, report.Reviewers[^1].Decisions);
        Assert.Equal(7, report.Decisions);
    }

    [Fact]
    public void Departments_group_users_and_compare_with_the_start()
    {
        var report = Build(
            [
                Fact("u1", remove: true, department: "Finance", applied: true, appliedBy: Carl),
                Fact("u4", remove: false, department: "IT")
            ],
            endUsers:
            [
                new UserRoleState("u1", "Finance", 3, 2),
                new UserRoleState("u2", "finance ", 1, 0),
                new UserRoleState("u3", null, 2, 2),
                new UserRoleState("u4", "IT", 1, 1)
            ],
            startUsers:
            [
                new UserRoleState("u1", "Finance", 3, 1),
                new UserRoleState("u3", " ", 2, 1),
                new UserRoleState("u5", "HR", 1, 1)
            ]);

        Assert.Equal(new[] { "Finance", KpiCalculator.NoDepartment, "IT", "HR" }, report.Departments.Select(d => d.Department));

        var finance = report.Departments[0];
        Assert.Equal((2, 1, 2, 1, 1, 0), (finance.UsersWithRoles, finance.UsersWithUnusedRoles, finance.UnusedAssignments,
            finance.UsersWithUnusedRolesAtStart ?? -1, finance.UsersCleanedUp, finance.UsersWithExceptions));
        Assert.Equal(50, finance.UnusedPercent);

        var none = report.Departments[1];
        Assert.Equal((1, 1, 2, 1), (none.UsersWithRoles, none.UsersWithUnusedRoles, none.UnusedAssignments, none.UsersWithUnusedRolesAtStart ?? -1));

        var it = report.Departments[2];
        Assert.Equal((1, 0, 1), (it.UsersWithUnusedRoles, it.UsersWithUnusedRolesAtStart ?? -1, it.UsersWithExceptions));

        var hr = report.Departments[3];
        Assert.Equal((0, 1), (hr.UsersWithRoles, hr.UsersWithUnusedRolesAtStart ?? -1));
    }

    [Fact]
    public void State_comes_from_the_end_snapshot_and_the_trend_lists_snapshots_in_the_period()
    {
        static SnapshotKpi Snapshot(int id, DateTime at, int unused) => new()
        {
            Snapshot = new SnapshotRef(id, "S" + id, at, 90),
            UsersWithRoles = 10,
            UsersWithUnusedRoles = unused
        };

        var before = Snapshot(3, Sep20, 7);
        var first = Snapshot(4, Nov1, 6);
        var last = Snapshot(8, Nov5, 4);

        var report = Build(
            [Fact("u1", remove: true, snapshot: 8, applied: true, appliedBy: Carl), Fact("u2", remove: true, snapshot: 8, applied: true, appliedBy: Carl)],
            snapshots: [before, first, last], endId: 8, startId: 3);

        Assert.Same(last, report.End);
        Assert.Same(before, report.Start);
        Assert.Equal(new[] { 4, 8 }, report.Snapshots.Select(s => s.Snapshot.Id));
        Assert.Equal(2, last.ChangesApplied);
        Assert.Equal(0, first.ChangesApplied);
        Assert.Equal(40, last.UnusedPercent);

        var sameSnapshot = Build([], snapshots: [last], endId: 8, startId: 8);
        Assert.Null(sameSnapshot.Start);
    }

    [Fact]
    public void Activity_is_counted_per_day_up_to_today()
    {
        var report = Build(
            [
                Fact("u1", remove: true, reviewedAt: Nov1, applied: true, appliedBy: Carl, appliedAt: Nov1),
                Fact("u2", remove: true, reviewedAt: Nov5, applied: true, appliedBy: Carl, appliedAt: Nov5),
                Fact("u1", remove: true, reviewedAt: Nov5, applied: true, appliedBy: Carl, appliedAt: Nov5),
                Fact("u3", remove: false, reviewedAt: Nov5),
                Fact("u4", remove: false, usage: UsageEvidenceStatus.ConfirmedUsed, recommendation: RecommendedAction.NoAction,
                    group: "g1", kind: ReviewItemKind.GroupMembership, reviewedAt: Nov5),
                Fact("u5", remove: true, group: "g2", kind: ReviewItemKind.GroupMembership, reviewedAt: Nov1)
            ],
            daily: [new DailyDecisionCounts(new DateOnly(2026, 11, 1), 2, 1), new DailyDecisionCounts(new DateOnly(2026, 11, 5), 1, 3)]);

        Assert.Equal("day", report.ActivityUnit);
        Assert.Equal(36, report.Activity.Count);
        Assert.Equal(new DateOnly(2026, 10, 1), report.Activity[0].Start);
        Assert.Equal(new DateOnly(2026, 11, 5), report.Activity[^1].End);

        var nov1 = report.Activity.Single(a => a.Start == new DateOnly(2026, 11, 1));
        Assert.Equal((3, 1, 0, 1, 1), (nov1.ApprovedRemoval, nov1.Kept, nov1.Exceptions, nov1.ChangesApplied, nov1.UsersCleanedUpToDate));

        var nov5 = report.Activity[^1];
        Assert.Equal((1, 4, 1, 2, 2, 1), (nov5.ApprovedRemoval, nov5.Kept, nov5.Exceptions, nov5.ChangesApplied, nov5.UsersCleanedUpToDate, nov5.ExceptionsToDate));
        Assert.Equal(0, report.Activity[0].Decisions);
    }

    [Fact]
    public void Long_periods_use_weeks_or_months()
    {
        var now = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);

        var year = Build([], period: new ReportPeriod(new(2026, 1, 1), new(2026, 12, 31)), now: now);
        Assert.Equal("week", year.ActivityUnit);
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 7)), (year.Activity[0].Start, year.Activity[0].End));
        Assert.Equal(new DateOnly(2026, 11, 6), year.Activity[^1].End);

        var twoYears = Build([], period: new ReportPeriod(new(2025, 1, 1), new(2026, 12, 31)), now: now);
        Assert.Equal("month", twoYears.ActivityUnit);
        Assert.Equal(23, twoYears.Activity.Count);
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 6)), (twoYears.Activity[^1].Start, twoYears.Activity[^1].End));

        var pastQuarter = Build([], period: new ReportPeriod(new(2026, 7, 1), new(2026, 9, 30)), now: now);
        Assert.Equal("week", pastQuarter.ActivityUnit);
        Assert.Equal(14, pastQuarter.Activity.Count);
        Assert.Equal(new DateOnly(2026, 9, 30), pastQuarter.Activity[^1].End);
    }
}
