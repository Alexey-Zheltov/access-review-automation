using AccessReviewPortal.Domain;

namespace AccessReviewPortal.Data.Entities;

public class ScanSnapshot
{
    public int Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Label { get; set; } = "";
    public ScanStatus Status { get; set; }
    public string? Error { get; set; }
    public string? TenantId { get; set; }
    public int TotalUsers { get; set; }
    public int TotalAssignments { get; set; }
    public long DatabaseSizeBytes { get; set; }
    public int InactivityThresholdDays { get; set; }

    /// <summary>Baseline used for the last recommendation calculation.</summary>
    public int? BaselineSnapshotId { get; set; }

    /// <summary>Short description of which usage sources were available during the scan.</summary>
    public string? UsageSources { get; set; }

    public ScanTrigger Trigger { get; set; }
    public string? StartedByUpn { get; set; }

    public List<UserStateSnapshot> Users { get; set; } = [];
    public List<RoleAssignmentSnapshot> Assignments { get; set; } = [];
    public List<ScanLogEntry> Log { get; set; } = [];
    public List<GroupSnapshot> Groups { get; set; } = [];
}

public class UserStateSnapshot
{
    public long Id { get; set; }
    public int ScanSnapshotId { get; set; }
    public string UserId { get; set; } = "";
    public string? Upn { get; set; }
    public string? DisplayName { get; set; }
    public string? Mail { get; set; }
    public bool AccountEnabled { get; set; }

    /// <summary>False when Graph returned 404 (user deleted from Entra ID).</summary>
    public bool ExistsInDirectory { get; set; } = true;

    public string? Department { get; set; }
    public string? JobTitle { get; set; }
    public string? ManagerId { get; set; }
    public string? ManagerUpn { get; set; }
    public string? ManagerDisplayName { get; set; }
    public DateTime? CreatedDateTimeUtc { get; set; }
    public bool OnPremisesSyncEnabled { get; set; }
    public string? OnPremisesSamAccountName { get; set; }

    /// <summary>signInActivity: latest interactive or non-interactive sign-in to any application.</summary>
    public DateTime? LastEntraSignInUtc { get; set; }

    /// <summary>Latest successful sign-in to Azure Portal / Azure Resource Manager (CLI, PowerShell, portal).</summary>
    public DateTime? LastAzurePortalSignInUtc { get; set; }

    /// <summary>Latest successful sign-in to admin portals used with Entra roles.</summary>
    public DateTime? LastAdminPortalSignInUtc { get; set; }

    /// <summary>Latest directory audit event initiated by the user on other objects (admin activity).</summary>
    public DateTime? LastDirectoryActivityUtc { get; set; }
    public string? LastDirectoryActivity { get; set; }
}

public class RoleAssignmentSnapshot
{
    public long Id { get; set; }
    public int ScanSnapshotId { get; set; }

    /// <summary>Stable key of the assignment. The same assignment has the same key in every snapshot.</summary>
    public string AssignmentKey { get; set; } = "";

    /// <summary>ARM role assignment id, PIM schedule id or Entra assignment id.</summary>
    public string? SourceAssignmentId { get; set; }

    public string PrincipalId { get; set; } = "";
    public string PrincipalType { get; set; } = "";
    public string? PrincipalDisplayName { get; set; }
    public string? PrincipalUpn { get; set; }

    /// <summary>False when the principal (user, group, service principal) no longer exists in Entra ID.</summary>
    public bool PrincipalExists { get; set; } = true;

    public string? AssignedViaGroupId { get; set; }
    public string? AssignedViaGroupName { get; set; }
    public bool AssignedViaGroupOnPremises { get; set; }
    public string? AssignedViaGroupSamAccountName { get; set; }

    public ScopeLevel ScopeLevel { get; set; }
    public string ScopeId { get; set; } = "";
    public string? ScopeDisplayName { get; set; }
    public string? SubscriptionId { get; set; }
    public string? SubscriptionName { get; set; }

    public string RoleDefinitionId { get; set; } = "";
    public string RoleName { get; set; } = "";
    public bool RoleHasWritePermissions { get; set; }

    public AssignmentType AssignmentType { get; set; }
    public DateTime? StartDateTimeUtc { get; set; }
    public DateTime? EndDateTimeUtc { get; set; }
    public string? Justification { get; set; }

    // Raw usage evidence. The status below is derived from these values and the inactivity threshold.
    public DateTime? LastWriteEvidenceUtc { get; set; }
    public string? LastWriteOperation { get; set; }
    public string? WriteEvidenceSource { get; set; }
    public DateTime? LastPimActivationUtc { get; set; }
    public string? PimActivationDetails { get; set; }
    public DateTime? LastSignInUtc { get; set; }
    public string? SignInSource { get; set; }

    public UsageEvidenceStatus UsageEvidenceStatus { get; set; }
    public DateTime? LastRoleUsedUtc { get; set; }
    public int? DaysSinceLastActivity { get; set; }
    public string? UsageEvidenceDetails { get; set; }

    public NativeReviewDecision NativeSelfReviewDecision { get; set; }
    public string? NativeReviewDetails { get; set; }

    // Microsoft Defender for Cloud CSPM (CIEM) findings for the principal / assignment.
    public DefenderCspmStatus DefenderCspmStatus { get; set; }

    /// <summary>All finding types, comma separated (DefenderCspmStatus names). Used for filtering.</summary>
    public string? DefenderCspmFindings { get; set; }
    public int DefenderCspmFindingCount { get; set; }
    public string? DefenderCspmDetails { get; set; }

    public bool IsNewSinceBaseline { get; set; }
    public RecommendedAction RecommendedAction { get; set; }
    public string? RecommendationReason { get; set; }

    public ReviewerDecision ReviewerDecision { get; set; }
    public string? ReviewerComment { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewedByUpn { get; set; }

    public string? RemediationStatus { get; set; }
    public DateTime? RemediatedAtUtc { get; set; }
    public string? RemediatedByUpn { get; set; }
}

/// <summary>Group that has at least one role assignment in a snapshot (Group Permissions view).</summary>
public class GroupSnapshot
{
    public long Id { get; set; }
    public int ScanSnapshotId { get; set; }
    public string GroupId { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool OnPremisesSyncEnabled { get; set; }
    public string? OnPremisesSamAccountName { get; set; }
    public bool IsAssignableToRole { get; set; }

    /// <summary>Owners as "Display Name (upn)", separated by "; ".</summary>
    public string? Owners { get; set; }
    public int OwnerCount { get; set; }

    /// <summary>Object ids of the owners, separated by ",". Used for the "group owner" permission check.</summary>
    public string? OwnerIds { get; set; }

    public int TotalTransitiveMembers { get; set; }
    public int UserMembers { get; set; }
    public int ServicePrincipalMembers { get; set; }
    public string? ExpansionError { get; set; }
}

/// <summary>Reviewer decision "Keep in Group" / "Remove from Group" for one member of a group.</summary>
public class GroupMemberDecision
{
    public long Id { get; set; }
    public int ScanSnapshotId { get; set; }
    public string GroupId { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string? MemberDisplayName { get; set; }
    public string? MemberUpn { get; set; }
    public GroupMemberDecisionType Decision { get; set; }
    public string? Comment { get; set; }
    public string? ReviewedByUpn { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? RemediationStatus { get; set; }
    public string? RemediatedByUpn { get; set; }
    public DateTime? RemediatedAtUtc { get; set; }
}

/// <summary>Subscription found by Subscription Discovery. Only monitored subscriptions are scanned.</summary>
public class MonitoredSubscription
{
    public string SubscriptionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? TenantId { get; set; }
    public string State { get; set; } = "";
    public string? ManagementGroupId { get; set; }
    public string? ManagementGroupName { get; set; }

    /// <summary>Management group chain from the root, for example "Tenant Root Group / Platform / Prod".</summary>
    public string? ManagementGroupPath { get; set; }

    public bool IsMonitored { get; set; }

    /// <summary>False when the subscription is visible only to the signed-in user (not onboarded for the scanner).</summary>
    public bool VisibleToScanner { get; set; }

    public bool ScannerReaderOk { get; set; }
    public bool ScannerMonitoringReaderOk { get; set; }
    public bool ActivityLogReadable { get; set; }

    /// <summary>Roles of the scanner identity that apply to the subscription (assigned here or inherited).</summary>
    public string? ScannerRoles { get; set; }

    /// <summary>True when the scanner has roles that can change something (zero-privilege violation).</summary>
    public bool ScannerHasPrivilegedRoles { get; set; }

    public bool DefenderCspmEnabled { get; set; }
    public bool CiemEnabled { get; set; }
    public int ResourceCount { get; set; }
    public DateTime? LastDiscoveredUtc { get; set; }
    public DateTime? LastScannedUtc { get; set; }
    public string? DiscoveryError { get; set; }
}

/// <summary>Quarterly review campaign: snapshot, baseline, Excel report archive and notification.</summary>
public class ReviewCampaign
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ScanTrigger Trigger { get; set; }
    public string? StartedByUpn { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public CampaignStatus Status { get; set; }
    public int? ScanSnapshotId { get; set; }
    public int? BaselineSnapshotId { get; set; }
    public int TotalRows { get; set; }
    public int RemoveCount { get; set; }
    public int ReviewCount { get; set; }
    public string? ReportFileName { get; set; }
    public byte[]? ReportContent { get; set; }
    public long ReportSizeBytes { get; set; }
    public string? NotificationStatus { get; set; }
    public string? Error { get; set; }
}

/// <summary>IDistributedCache entry stored in PostgreSQL (MSAL token cache, survives container restarts).</summary>
public class DistributedCacheEntry
{
    public string Id { get; set; } = "";
    public byte[] Value { get; set; } = [];
    public DateTimeOffset ExpiresAtTime { get; set; }
    public long? SlidingExpirationInSeconds { get; set; }
    public DateTimeOffset? AbsoluteExpiration { get; set; }
}

public class ScanLogEntry
{
    public long Id { get; set; }
    public int ScanSnapshotId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = "";
}

public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
