using AccessReviewPortal.Domain;
using AccessReviewPortal.Services.Review;

namespace AccessReviewPortal.Tests;

public class RecommendationEngineTests
{
    private const int Threshold = 90;

    [Fact]
    public void Unused_user_assignment_is_remove()
    {
        var row = TestData.Row();
        RecommendationEngine.Evaluate(row, TestData.User(row.PrincipalId), null, null, TestData.Now, Threshold);

        Assert.Equal(UsageEvidenceStatus.Unused90Days, row.UsageEvidenceStatus);
        Assert.Equal(RecommendedAction.Remove, row.RecommendedAction);
        Assert.StartsWith(Reasons.NoRoleUsage, row.RecommendationReason);
    }

    [Fact]
    public void Write_evidence_inside_threshold_is_no_action()
    {
        var row = TestData.Row();
        row.LastWriteEvidenceUtc = TestData.Now.AddDays(-10);
        row.Justification = "Team lead";
        RecommendationEngine.Evaluate(row, TestData.User(row.PrincipalId), null, null, TestData.Now, Threshold);

        Assert.Equal(UsageEvidenceStatus.ConfirmedUsed, row.UsageEvidenceStatus);
        Assert.Equal(RecommendedAction.NoAction, row.RecommendedAction);
        Assert.Equal(10, row.DaysSinceLastActivity);
    }

    [Theory]
    [InlineData(DefenderCspmStatus.Overprovisioned)]
    [InlineData(DefenderCspmStatus.PermanentPrivileged)]
    public void Used_assignment_with_defender_finding_is_review(DefenderCspmStatus finding)
    {
        var row = TestData.Row();
        row.LastWriteEvidenceUtc = TestData.Now.AddDays(-3);
        row.DefenderCspmStatus = finding;
        row.DefenderCspmFindings = finding.ToString();
        RecommendationEngine.Evaluate(row, TestData.User(row.PrincipalId), null, null, TestData.Now, Threshold);

        Assert.Equal(RecommendedAction.Review, row.RecommendedAction);
        Assert.Equal(Reasons.DefenderCspm, row.RecommendationReason);
    }

    [Fact]
    public void Defender_rule_also_reads_the_secondary_findings()
    {
        var row = TestData.Row();
        row.LastWriteEvidenceUtc = TestData.Now.AddDays(-3);
        row.DefenderCspmStatus = DefenderCspmStatus.Inactive45d;
        row.DefenderCspmFindings = "Inactive45d,Overprovisioned";
        RecommendationEngine.Evaluate(row, TestData.User(row.PrincipalId), null, null, TestData.Now, Threshold);

        Assert.Equal(RecommendedAction.Review, row.RecommendedAction);
        Assert.Equal(Reasons.DefenderCspm, row.RecommendationReason);
    }

    [Fact]
    public void Unused_assignment_stays_remove_even_with_defender_finding()
    {
        var row = TestData.Row();
        row.DefenderCspmStatus = DefenderCspmStatus.Overprovisioned;
        row.DefenderCspmFindings = "Overprovisioned";
        RecommendationEngine.Evaluate(row, TestData.User(row.PrincipalId), null, null, TestData.Now, Threshold);

        Assert.Equal(RecommendedAction.Remove, row.RecommendedAction);
    }

    [Fact]
    public void Inactive45d_only_does_not_trigger_the_defender_review_rule()
    {
        var row = TestData.Row();
        row.LastWriteEvidenceUtc = TestData.Now.AddDays(-3);
        row.DefenderCspmStatus = DefenderCspmStatus.Inactive45d;
        row.DefenderCspmFindings = "Inactive45d";
        RecommendationEngine.Evaluate(row, TestData.User(row.PrincipalId), null, null, TestData.Now, Threshold);

        Assert.Equal(RecommendedAction.NoAction, row.RecommendedAction);
    }

    [Fact]
    public void Mover_is_remove()
    {
        var row = TestData.Row();
        row.LastWriteEvidenceUtc = TestData.Now.AddDays(-1);
        var baseline = TestData.User(row.PrincipalId, department: "IT");
        var current = TestData.User(row.PrincipalId, department: "Finance");
        RecommendationEngine.Evaluate(row, current, baseline, null, TestData.Now, Threshold);

        Assert.Equal(RecommendedAction.Remove, row.RecommendedAction);
        Assert.StartsWith(Reasons.DepartmentManagerChanged, row.RecommendationReason);
    }

    [Fact]
    public void Leaver_is_remove()
    {
        var row = TestData.Row();
        row.LastWriteEvidenceUtc = TestData.Now.AddDays(-1);
        RecommendationEngine.Evaluate(row, TestData.User(row.PrincipalId, enabled: false), null, null, TestData.Now, Threshold);

        Assert.Equal(RecommendedAction.Remove, row.RecommendedAction);
        Assert.StartsWith(Reasons.LeftOrganization, row.RecommendationReason);
    }

    [Fact]
    public void Sign_in_only_with_write_role_is_review()
    {
        var row = TestData.Row();
        row.LastSignInUtc = TestData.Now.AddDays(-5);
        RecommendationEngine.Evaluate(row, TestData.User(row.PrincipalId), null, null, TestData.Now, Threshold);

        Assert.Equal(UsageEvidenceStatus.PortalSignInOnlyNoWrite, row.UsageEvidenceStatus);
        Assert.Equal(RecommendedAction.Review, row.RecommendedAction);
    }

    [Fact]
    public void Eligible_role_needs_activation_not_sign_in()
    {
        var row = TestData.Row(type: AssignmentType.PimEligible);
        row.LastSignInUtc = TestData.Now.AddDays(-5);
        RecommendationEngine.Evaluate(row, TestData.User(row.PrincipalId), null, null, TestData.Now, Threshold);

        Assert.Equal(UsageEvidenceStatus.Unused90Days, row.UsageEvidenceStatus);
    }

    [Fact]
    public void New_since_baseline_is_detected()
    {
        var row = TestData.Row();
        RecommendationEngine.Evaluate(row, null, null, new HashSet<string> { "other" }, TestData.Now, Threshold);
        Assert.True(row.IsNewSinceBaseline);
    }
}
