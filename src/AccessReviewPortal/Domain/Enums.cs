namespace AccessReviewPortal.Domain;

public enum ScopeLevel
{
    EntraRole,
    ManagementGroup,
    Subscription,
    ResourceGroup,
    Resource
}

public enum AssignmentType
{
    Permanent,
    PimActive,
    PimEligible
}

public enum UsageEvidenceStatus
{
    ConfirmedUsed,
    PortalSignInOnlyNoWrite,
    Unused90Days
}

public enum RecommendedAction
{
    NoAction,
    Review,
    Remove
}

public enum ReviewerDecision
{
    Pending,
    NoAction,
    Removed
}

public enum NativeReviewDecision
{
    None,
    Approve,
    Deny,
    NotReviewed
}

public enum ScanStatus
{
    Running,
    Completed,
    Failed
}

/// <summary>Primary Microsoft Defender for Cloud CSPM (CIEM) finding of an assignment.</summary>
public enum DefenderCspmStatus
{
    None,
    Overprovisioned,
    Inactive45d,
    PermanentPrivileged,
    BlockedOrGuest
}

/// <summary>Reviewer decision for one member of a group that has role assignments.</summary>
public enum GroupMemberDecisionType
{
    Pending,
    KeepInGroup,
    RemoveFromGroup
}

public enum ScanTrigger
{
    Manual,
    MonthlySchedule,
    QuarterlyCampaign
}

public enum CampaignStatus
{
    Running,
    Completed,
    Failed
}

public static class DisplayText
{
    public static string Of(AssignmentType t) => t switch
    {
        AssignmentType.Permanent => "Permanent",
        AssignmentType.PimActive => "PIM Active",
        AssignmentType.PimEligible => "PIM Eligible",
        _ => t.ToString()
    };

    public static string Of(RecommendedAction a) => a switch
    {
        RecommendedAction.NoAction => "No Action",
        _ => a.ToString()
    };

    public static string Of(ReviewerDecision d) => d switch
    {
        ReviewerDecision.NoAction => "No Action",
        _ => d.ToString()
    };

    /// <summary>Wording used for reviewer decisions in the portal (the Excel export keeps "No Action" / "Removed").</summary>
    public static string ReviewLabel(ReviewerDecision d) => d switch
    {
        ReviewerDecision.NoAction => "Keep",
        ReviewerDecision.Removed => "Approve Removal",
        _ => "Pending"
    };

    public static string Of(DefenderCspmStatus s) => s switch
    {
        DefenderCspmStatus.None => "None",
        DefenderCspmStatus.Inactive45d => "Inactive 45d",
        DefenderCspmStatus.PermanentPrivileged => "Permanent Privileged",
        DefenderCspmStatus.BlockedOrGuest => "Blocked / Guest",
        _ => s.ToString()
    };

    public static string Of(GroupMemberDecisionType d) => d switch
    {
        GroupMemberDecisionType.KeepInGroup => "Keep in Group",
        GroupMemberDecisionType.RemoveFromGroup => "Remove from Group",
        _ => "Pending"
    };

    public static string Of(ScanTrigger t) => t switch
    {
        ScanTrigger.MonthlySchedule => "Monthly schedule",
        ScanTrigger.QuarterlyCampaign => "Quarterly campaign",
        _ => "Manual"
    };

    public static string Of(ScopeLevel s) => s switch
    {
        ScopeLevel.EntraRole => "Entra Role",
        ScopeLevel.ManagementGroup => "Management Group",
        ScopeLevel.ResourceGroup => "Resource Group",
        _ => s.ToString()
    };
}

/// <summary>Recommendation reason prefixes. Statistics group by these prefixes.</summary>
public static class Reasons
{
    public const string LeftOrganization = "Left Organization / Account Disabled";
    public const string DepartmentManagerChanged = "Department/Manager Changed";
    public const string DeniedInSelfReview = "Denied in Entra Self-Review";
    public const string NoRoleUsage = "No role usage";
    public const string NoWriteActions = "No Write actions";
    public const string ServicePrincipalNoActivity = "No logged activity for service principal";
    public const string DefenderCspm = "Defender CSPM: Overprovisioned / Permanent Privileged (Right-size or move to PIM)";

    public static readonly string[] All =
    [
        LeftOrganization, DepartmentManagerChanged, DeniedInSelfReview, NoRoleUsage, NoWriteActions, ServicePrincipalNoActivity, DefenderCspm
    ];

    public static string GroupOf(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return "";
        foreach (var r in All)
        {
            if (reason.StartsWith(r, StringComparison.Ordinal)) return r;
        }
        return reason;
    }
}
