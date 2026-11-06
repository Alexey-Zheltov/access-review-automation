using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Services.Automation;
using AccessReviewPortal.Services.Discovery;
using AccessReviewPortal.Services.Review;
using AccessReviewPortal.Services.Scanning;

namespace AccessReviewPortal.Tests;

public class PermissionMatcherTests
{
    [Theory]
    [InlineData("*", "Microsoft.Authorization/roleAssignments/delete", true)]
    [InlineData("Microsoft.Authorization/*", "Microsoft.Authorization/roleAssignments/delete", true)]
    [InlineData("*/read", "Microsoft.Authorization/roleAssignments/delete", false)]
    [InlineData("microsoft.authorization/roleassignments/*", "Microsoft.Authorization/roleAssignments/delete", true)]
    [InlineData("Microsoft.Authorization/roleAssignments/write", "Microsoft.Authorization/roleAssignments/delete", false)]
    public void Wildcards_and_case(string pattern, string action, bool expected) =>
        Assert.Equal(expected, PermissionMatcher.Matches(pattern, action));

    [Theory]
    [InlineData("*/read", true)]
    [InlineData("Microsoft.OperationalInsights/workspaces/search/action", true)]
    [InlineData("Microsoft.Support/*", true)]
    [InlineData("*", false)]
    [InlineData("Microsoft.Authorization/roleAssignments/write", false)]
    [InlineData("Microsoft.Storage/storageAccounts/listKeys/action", false)]
    public void Read_only_actions_of_reader_roles(string action, bool readOnly) =>
        Assert.Equal(readOnly, PermissionMatcher.IsReadOnlyAction(action));

    [Fact]
    public void Monitoring_reader_is_not_a_write_role()
    {
        var monitoringReader = new PermissionBlock
        {
            Actions = ["*/read", "Microsoft.OperationalInsights/workspaces/search/action", "Microsoft.Support/*"]
        };
        var contributor = new PermissionBlock { Actions = ["*"], NotActions = ["Microsoft.Authorization/*/Delete"] };

        Assert.False(PermissionMatcher.AzureRoleHasWrite([monitoringReader]));
        Assert.True(PermissionMatcher.AzureRoleHasWrite([contributor]));
    }
}

public class UserCapabilityCheckerTests
{
    [Fact]
    public void Kind_of_assignment()
    {
        Assert.Equal(RevokeTargetKind.AzureRoleAssignment, UserCapabilityChecker.KindOf(TestData.Row()));
        Assert.Equal(RevokeTargetKind.EntraRoleAssignment, UserCapabilityChecker.KindOf(TestData.Row(level: ScopeLevel.EntraRole)));
        Assert.Equal(RevokeTargetKind.CloudGroupMembership, UserCapabilityChecker.KindOf(TestData.Row(viaGroup: "22222222-2222-2222-2222-222222222222")));
        Assert.Equal(RevokeTargetKind.OnPremisesGroupMembership,
            UserCapabilityChecker.KindOf(TestData.Row(viaGroup: "22222222-2222-2222-2222-222222222222", viaGroupOnPrem: true)));
    }

    [Theory]
    [InlineData(AssignmentType.Permanent, "Microsoft.Authorization/roleAssignments/delete")]
    [InlineData(AssignmentType.PimActive, "Microsoft.Authorization/roleAssignmentScheduleRequests/write")]
    [InlineData(AssignmentType.PimEligible, "Microsoft.Authorization/roleEligibilityScheduleRequests/write")]
    public void Required_azure_action(AssignmentType type, string action) =>
        Assert.Equal(action, UserCapabilityChecker.RequiredAzureAction(type));

    [Fact]
    public void Member_key_is_case_insensitive() =>
        Assert.Equal(UserCapabilityChecker.MemberKey("AB", "CD"), UserCapabilityChecker.MemberKey("ab", "cd"));
}

public class AutomationScheduleTests
{
    [Fact]
    public void Due_after_planned_time_and_not_run_yet()
    {
        var now = new DateTime(2026, 10, 1, 4, 30, 0, DateTimeKind.Utc);
        var planned = AutomationService.ScheduledThisMonth(now, 1, "04:00", [1, 4, 7, 10]);

        Assert.Equal(new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc), planned);
        Assert.True(AutomationService.IsDue(now, planned, null));
        Assert.False(AutomationService.IsDue(now, planned, now.AddMinutes(-5)));
        Assert.False(AutomationService.IsDue(now.AddMinutes(-31), planned, null));
    }

    [Fact]
    public void Missed_run_catches_up_for_seven_days_only()
    {
        var planned = new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc);
        Assert.True(AutomationService.IsDue(planned.AddDays(6), planned, new DateTime(2026, 7, 1, 4, 0, 0, DateTimeKind.Utc)));
        Assert.False(AutomationService.IsDue(planned.AddDays(8), planned, null));
    }

    [Fact]
    public void Quarterly_schedule_skips_other_months()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(AutomationService.ScheduledThisMonth(now, 1, "04:00", [1, 4, 7, 10]));
        Assert.Equal(new DateTime(2027, 1, 1, 4, 0, 0, DateTimeKind.Utc), AutomationService.NextRun(now, true, 1, "04:00", [1, 4, 7, 10], null));
        Assert.Null(AutomationService.NextRun(now, false, 1, "04:00", [1, 4, 7, 10], null));
    }

    [Fact]
    public void Monthly_next_run_and_bad_time_fallback()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        // The run of Nov 1 never happened: it is still due (catch-up). After it ran, the next run is Dec 1.
        Assert.Equal(new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc), AutomationService.NextRun(now, true, 1, "02:00", null, null));
        Assert.Equal(new DateTime(2026, 12, 1, 2, 0, 0, DateTimeKind.Utc),
            AutomationService.NextRun(now, true, 1, "02:00", null, new DateTime(2026, 11, 1, 2, 1, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromHours(2), AutomationService.ParseTime("25:99"));
        Assert.Equal(new TimeSpan(7, 5, 0), AutomationService.ParseTime("7:05"));
    }

    [Theory]
    [InlineData(1, "2026-Q1")]
    [InlineData(4, "2026-Q2")]
    [InlineData(9, "2026-Q3")]
    [InlineData(12, "2026-Q4")]
    public void Quarter_name(int month, string expected) =>
        Assert.Equal(expected, AutomationService.QuarterName(new DateTime(2026, month, 15, 0, 0, 0, DateTimeKind.Utc)));
}

public class GroupPermissionsTests
{
    private const string GroupId = "33333333-3333-3333-3333-333333333333";
    private const string Active = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Unused = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Mover = "cccccccc-0000-0000-0000-000000000003";

    [Fact]
    public void Group_summary_counts_usage_and_movers()
    {
        var groupRow = TestData.Row(GroupId, principalType: "Group");
        var rows = new List<RoleAssignmentSnapshot> { groupRow };
        foreach (var (member, status) in new[] { (Active, UsageEvidenceStatus.ConfirmedUsed), (Unused, UsageEvidenceStatus.Unused90Days), (Mover, UsageEvidenceStatus.Unused90Days) })
        {
            var r = TestData.Row(member, viaGroup: GroupId);
            r.UsageEvidenceStatus = status;
            rows.Add(r);
        }
        var users = new Dictionary<string, UserStateSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [Active] = TestData.User(Active), [Unused] = TestData.User(Unused), [Mover] = TestData.User(Mover, department: "Finance")
        };
        var baseline = new Dictionary<string, UserStateSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [Active] = TestData.User(Active), [Unused] = TestData.User(Unused), [Mover] = TestData.User(Mover, department: "IT")
        };
        var group = new GroupSnapshot { GroupId = GroupId, DisplayName = "AR-POC-Operators", TotalTransitiveMembers = 3 };

        var summary = Assert.Single(GroupPermissionsService.Build(rows, [group], [], users, baseline));

        Assert.Equal(3, summary.TotalMembers);
        Assert.Equal(1, summary.ActiveMembers);
        Assert.Equal(2, summary.UnusedMembers);
        Assert.Equal(1, summary.MoversOrDisabled);
        var assignment = Assert.Single(summary.Assignments);
        Assert.Equal(33, assignment.UsagePercent);
        Assert.Equal(GroupMemberDecisionType.RemoveFromGroup, summary.Members.Single(m => m.MemberId == Unused).RecommendedDecision);
        Assert.StartsWith("Mover", summary.Members.Single(m => m.MemberId == Mover).MoverStatus);
        Assert.Equal(GroupMemberDecisionType.KeepInGroup, summary.Members.Single(m => m.MemberId == Active).RecommendedDecision);
    }

    [Fact]
    public void Zero_usage_assignment_has_zero_percent()
    {
        var groupRow = TestData.Row(GroupId, principalType: "Group");
        var member = TestData.Row(Unused, viaGroup: GroupId);
        member.UsageEvidenceStatus = UsageEvidenceStatus.Unused90Days;

        var summary = Assert.Single(GroupPermissionsService.Build([groupRow, member], [], [],
            new Dictionary<string, UserStateSnapshot>(), new Dictionary<string, UserStateSnapshot>()));

        Assert.Equal(0, Assert.Single(summary.Assignments).UsagePercent);
        Assert.Equal("Cloud-Only", summary.Source);
    }
}

public class ScriptTests
{
    [Fact]
    public void Onboarding_script_assigns_only_read_roles()
    {
        var ps = SubscriptionDiscoveryService.BuildOnboardingScript(SubscriptionDiscoveryService.ScriptFormat.PowerShell, "obj-1", "app-1", "tenant-1",
            ["mg-platform"], [TestData.SubscriptionId]);
        var cli = SubscriptionDiscoveryService.BuildOnboardingScript(SubscriptionDiscoveryService.ScriptFormat.AzureCli, "obj-1", "app-1", "tenant-1",
            [], [TestData.SubscriptionId]);

        Assert.Contains("/providers/Microsoft.Management/managementGroups/mg-platform", ps);
        Assert.Contains($"/subscriptions/{TestData.SubscriptionId}", ps);
        Assert.Contains("'Reader', 'Monitoring Reader'", ps);
        Assert.DoesNotContain("Owner", ps.Replace("Run as Owner", ""));
        Assert.Contains("--role \"Monitoring Reader\"", cli);
        Assert.DoesNotContain("Contributor", cli);
    }

    [Fact]
    public void Remediation_script_contains_role_rows_and_member_decisions()
    {
        const string group = "44444444-4444-4444-4444-444444444444";
        var azure = TestData.Row("66666666-6666-6666-6666-666666666666");
        azure.ReviewerDecision = ReviewerDecision.Removed;
        var kept = TestData.Row("77777777-7777-7777-7777-777777777777");
        kept.ReviewerDecision = ReviewerDecision.NoAction;
        var decision = new GroupMemberDecision
        {
            GroupId = group, MemberId = "55555555-5555-5555-5555-555555555555", MemberDisplayName = "Member Five",
            Decision = GroupMemberDecisionType.RemoveFromGroup
        };
        var groups = new Dictionary<string, GroupSnapshot> { [group] = new() { GroupId = group, DisplayName = "AR-POC-Operators" } };
        var snapshot = new ScanSnapshot { Id = 7, Label = "Q2 Review", CreatedAtUtc = TestData.Now };

        var script = RemediationService.BuildScript(snapshot, [azure, kept], [decision], groups, "tenant-1");

        Assert.Contains("Remove-AzRoleAssignment -ObjectId '66666666-6666-6666-6666-666666666666'", script);
        Assert.DoesNotContain("77777777-7777-7777-7777-777777777777", script);
        Assert.Contains("Remove-MgGroupMemberByRef -GroupId '44444444-4444-4444-4444-444444444444' -DirectoryObjectId '55555555-5555-5555-5555-555555555555'", script);
        Assert.Contains("AR-POC-Operators", script);
    }
}
