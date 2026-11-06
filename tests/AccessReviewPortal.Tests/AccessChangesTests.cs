using AccessReviewPortal.Data.Entities;
using AccessReviewPortal.Domain;
using AccessReviewPortal.Services.Review;

namespace AccessReviewPortal.Tests;

public class AccessChangesTests
{
    private const string Group = "44444444-4444-4444-4444-444444444444";
    private const string Member = "55555555-5555-5555-5555-555555555555";
    private static readonly Dictionary<string, GroupSnapshot> NoGroups = new(StringComparer.OrdinalIgnoreCase);

    private static RoleAssignmentSnapshot Approved(RoleAssignmentSnapshot row, string by = "alice@contoso.com")
    {
        row.ReviewerDecision = ReviewerDecision.Removed;
        row.ReviewedByUpn = by;
        row.ReviewedAtUtc = TestData.Now;
        return row;
    }

    [Theory]
    [InlineData(ScopeLevel.ResourceGroup, AssignmentType.Permanent, AccessChangeKind.AzureRoleAssignment, "Remove Azure role assignment")]
    [InlineData(ScopeLevel.Subscription, AssignmentType.PimEligible, AccessChangeKind.AzurePimEligible, "Remove Azure PIM eligible assignment")]
    [InlineData(ScopeLevel.Resource, AssignmentType.PimActive, AccessChangeKind.AzurePimActive, "Remove Azure PIM active assignment")]
    [InlineData(ScopeLevel.EntraRole, AssignmentType.Permanent, AccessChangeKind.EntraRoleAssignment, "Remove Entra ID role assignment")]
    [InlineData(ScopeLevel.EntraRole, AssignmentType.PimEligible, AccessChangeKind.EntraPimEligible, "Remove Entra ID PIM eligible assignment")]
    public void Direct_rows_become_one_change_each(ScopeLevel level, AssignmentType type, AccessChangeKind kind, string text)
    {
        var row = Approved(TestData.Row(level: level, type: type));

        var change = Assert.Single(AccessChanges.Build([row], [], NoGroups));

        Assert.Equal(kind, change.Kind);
        Assert.Equal(text, change.ChangeText);
        Assert.Equal([row.Id], change.RowIds);
        Assert.Equal("alice@contoso.com", change.ApprovedBy);
        Assert.True(change.PortalCanApply);
        Assert.False(change.IsApplied);
    }

    [Fact]
    public void Rows_via_the_same_group_and_the_member_decision_become_one_membership_change()
    {
        var first = Approved(TestData.Row(Member, viaGroup: Group));
        var second = Approved(TestData.Row(Member, scope: $"/subscriptions/{TestData.SubscriptionId}", viaGroup: Group));
        second.RoleName = "Reader";
        var decision = new GroupMemberDecision
        {
            GroupId = Group, MemberId = Member, Decision = GroupMemberDecisionType.RemoveFromGroup,
            ReviewedByUpn = "bob@contoso.com", ReviewedAtUtc = TestData.Now.AddMinutes(5), Comment = "Moved to Finance"
        };
        var groups = new Dictionary<string, GroupSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            [Group] = new() { GroupId = Group, DisplayName = "AR-POC-Operators" }
        };

        var change = Assert.Single(AccessChanges.Build([first, second], [decision], groups));

        Assert.Equal(AccessChangeKind.CloudGroupMembership, change.Kind);
        Assert.Equal("Member of 'AR-POC-Operators'", change.Access);
        Assert.Equal(2, change.RowIds.Count);
        Assert.True(change.HasMemberDecision);
        Assert.Equal("bob@contoso.com", change.ApprovedBy);
        Assert.Equal("Moved to Finance", change.Reason);
        Assert.Equal(2, change.GroupAccess.Count);
    }

    [Fact]
    public void Member_decision_without_rows_is_a_change_too()
    {
        var decision = new GroupMemberDecision
        {
            GroupId = Group, MemberId = Member, MemberDisplayName = "AR POC Mover", Decision = GroupMemberDecisionType.RemoveFromGroup
        };

        var change = Assert.Single(AccessChanges.Build([], [decision], NoGroups));

        Assert.Equal("AR POC Mover", change.Principal);
        Assert.Empty(change.RowIds);
        Assert.Equal(Group, change.GroupId);
    }

    [Fact]
    public void Groups_synced_from_ad_cannot_be_applied_from_the_portal()
    {
        var row = Approved(TestData.Row(Member, viaGroup: Group, viaGroupOnPrem: true));

        var change = Assert.Single(AccessChanges.Build([row], [], NoGroups));

        Assert.Equal(AccessChangeKind.OnPremisesGroupMembership, change.Kind);
        Assert.False(change.PortalCanApply);
    }

    [Fact]
    public void Applied_status_is_detected()
    {
        var row = Approved(TestData.Row());
        row.RemediationStatus = "Revoked 2026-11-05 17:37 UTC: Role assignment deleted.";
        row.RemediatedByUpn = "alice@contoso.com";

        var change = Assert.Single(AccessChanges.Build([row], [], NoGroups));

        Assert.True(change.IsApplied);
        Assert.Equal("alice@contoso.com", change.AppliedBy);
        Assert.False(AccessChanges.IsDone("Failed: Forbidden"));
    }

    [Fact]
    public void Count_matches_the_built_changes()
    {
        var count = AccessChanges.CountChanges(
            [(null, "u1"), (null, "u2"), (Group, Member), (Group.ToUpperInvariant(), Member)],
            [(Group, Member), (Group, "66666666-6666-6666-6666-666666666666")]);

        Assert.Equal(4, count);
    }

    [Fact]
    public void Capability_is_taken_from_the_rows_or_the_member_decision()
    {
        var row = Approved(TestData.Row());
        var decision = new GroupMemberDecision { GroupId = Group, MemberId = Member, Decision = GroupMemberDecisionType.RemoveFromGroup };
        var changes = AccessChanges.Build([row], [decision], NoGroups);
        var report = new CapabilityReport();
        report.Rows[row.Id] = new RevokeCapability(true, "Allowed");
        report.Members[UserCapabilityChecker.MemberKey(Group, Member)] = new RevokeCapability(false, "Requires group owner");

        Assert.True(AccessChanges.CapabilityOf(changes.Single(c => c.GroupId is null), report)!.CanRevoke);
        Assert.False(AccessChanges.CapabilityOf(changes.Single(c => c.GroupId is not null), report)!.CanRevoke);
        Assert.Null(AccessChanges.CapabilityOf(changes[0], null));
    }
}
