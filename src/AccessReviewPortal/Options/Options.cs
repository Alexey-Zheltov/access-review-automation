namespace AccessReviewPortal.Options;

public sealed class AzureAdOptions
{
    public const string Section = "AzureAd";

    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public bool HasClientSecret =>
        !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class ReviewSettingsOptions
{
    public const string Section = "ReviewSettings";

    /// <summary>Default inactivity threshold. Can be changed in the UI (stored in the AppSettings table).</summary>
    public int InactivityThresholdDays { get; set; } = 90;

    /// <summary>How many days of Azure Activity Log to read through REST (maximum 90).</summary>
    public int ActivityLogLookbackDays { get; set; } = 90;

    /// <summary>Safety cap for very busy subscriptions when the Activity Log is read through REST.</summary>
    public int ActivityLogMaxEventsPerSubscription { get; set; } = 300_000;

    /// <summary>Optional Log Analytics workspace (customer id GUID) with AzureActivity / SigninLogs / AuditLogs.</summary>
    public string? LogAnalyticsWorkspaceId { get; set; }

    public int LogAnalyticsLookbackDays { get; set; } = 90;

    /// <summary>Only Access Review instances that ended within this window are read.</summary>
    public int AccessReviewLookbackDays { get; set; } = 180;

    /// <summary>Native access reviews are an optional source: their collection stops after this time.</summary>
    public int AccessReviewTimeBudgetMinutes { get; set; } = 5;

    /// <summary>Graph sign-in logs are slow and throttled: reading stops after this time (Log Analytics has no such limit).</summary>
    public int SignInTimeBudgetMinutes { get; set; } = 8;

    /// <summary>Parallel requests to the Activity Log REST API (each reads a 2-day slice of one subscription).</summary>
    public int ActivityLogParallelism { get; set; } = 12;

    /// <summary>Optional list of subscription ids. Empty = all subscriptions visible to the service principal.</summary>
    public string[] SubscriptionIds { get; set; } = [];

    /// <summary>Client applications whose sign-ins count as Azure control plane access.</summary>
    public string[] AzureControlPlaneAppIds { get; set; } = [];

    /// <summary>Resource (audience) of Azure Resource Manager sign-ins.</summary>
    public string AzureControlPlaneResourceId { get; set; } = WellKnownIds.AzureServiceManagementApi;

    /// <summary>Admin portals / tools whose sign-ins count as activity for Entra ID roles.</summary>
    public string[] AdminPortalAppIds { get; set; } = [];

    public int MaxParallelism { get; set; } = 6;

    public IReadOnlyList<string> EffectiveControlPlaneAppIds =>
        AzureControlPlaneAppIds.Length > 0 ? AzureControlPlaneAppIds : [WellKnownIds.AzurePortal];

    public IReadOnlyList<string> EffectiveAdminPortalAppIds =>
        AdminPortalAppIds.Length > 0 ? AdminPortalAppIds : [WellKnownIds.AzurePortal];
}

public sealed class PortalOptions
{
    public const string Section = "Portal";

    /// <summary>Shared key for Authentication__Mode=AccessKey (fallback mode without Entra ID sign-in).</summary>
    public string? AccessKey { get; set; }

    /// <summary>Public URL of the portal, used in links of notifications. Example: https://accessreview.contoso.com</summary>
    public string? PublicBaseUrl { get; set; }
}

/// <summary>How users sign in to the portal.</summary>
public sealed class PortalAuthOptions
{
    public const string Section = "Authentication";

    /// <summary>EntraId (OpenID Connect, default) or AccessKey (shared key, read and review only, no revoke).</summary>
    public string Mode { get; set; } = "EntraId";

    /// <summary>Shows "Sign in with a code from another device" (OAuth device code flow) on the sign-in page.</summary>
    public bool EnableDeviceCodeSignIn { get; set; } = true;

    /// <summary>Lifetime of the portal session cookie.</summary>
    public int SessionHours { get; set; } = 12;

    public bool IsEntraId => string.Equals(Mode, "EntraId", StringComparison.OrdinalIgnoreCase);
}

/// <summary>App roles of the portal application registration.</summary>
public sealed class PortalAuthorizationOptions
{
    public const string Section = "Authorization";

    /// <summary>When true, users need one of the app roles below. When false, every user of the tenant can open the portal.</summary>
    public bool RequireAppRole { get; set; } = true;

    public string AdminRole { get; set; } = "AccessReview.Admin";
    public string ReviewerRole { get; set; } = "AccessReview.Reviewer";
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>Apply EF Core migrations at start-up. Set false when the CI/CD pipeline runs the migration bundle (efbundle).</summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;

    /// <summary>Rows per INSERT batch when a scan is saved.</summary>
    public int InsertChunkSize { get; set; } = 2000;
}

public sealed class DiscoveryOptions
{
    public const string Section = "Discovery";

    /// <summary>New subscriptions that the scanner can read are monitored automatically.</summary>
    public bool MonitorNewSubscriptionsByDefault { get; set; } = true;
}

public sealed class DefenderCspmOptions
{
    public const string Section = "DefenderCspm";

    /// <summary>Read Microsoft Defender for Cloud CSPM (CIEM) recommendations during the scan.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Defaults for the scheduled scans. The values can be changed on the Automation page.</summary>
public sealed class AutomationOptions
{
    public const string Section = "Automation";

    public bool MonthlySnapshotEnabled { get; set; } = true;
    public int MonthlyDayOfMonth { get; set; } = 1;
    public string MonthlyTimeUtc { get; set; } = "02:00";

    public bool QuarterlyCampaignEnabled { get; set; } = true;
    public int[] QuarterlyMonths { get; set; } = [1, 4, 7, 10];
    public int QuarterlyDayOfMonth { get; set; } = 1;
    public string QuarterlyTimeUtc { get; set; } = "04:00";

    /// <summary>Optional webhook (Power Automate / Logic Apps / Teams workflow) that receives "campaign ready" messages.</summary>
    public string? NotificationWebhookUrl { get; set; }

    /// <summary>How often the worker checks the schedule.</summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>A failed scheduled run is started again after this time (within 7 days of the planned time).</summary>
    public int RetryAfterMinutes { get; set; } = 60;
}

public static class WellKnownIds
{
    public const string AzurePortal = "c44b4083-3bb0-49c1-b47d-974e53cbdf3c";
    public const string AzureServiceManagementApi = "797f4846-ba00-4fd7-ba43-dac1f8f63013";
    public const string MicrosoftGraphAppId = "00000003-0000-0000-c000-000000000000";

    // Built-in Azure roles.
    public const string ReaderRole = "acdd72a7-3385-48ef-bd42-f606fba81ae7";
    public const string MonitoringReaderRole = "43d0d8ad-25c7-4714-9337-8ba259a9fe05";
    public const string OwnerRole = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
    public const string ContributorRole = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    public const string UserAccessAdministratorRole = "18d7d88d-d35e-4fb5-a5c3-7773c20a72d9";
    public const string RbacAdministratorRole = "f58310d9-a9f6-439a-9e8d-f62e7b41a168";

    // Built-in Entra ID roles (template ids).
    public const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";
    public const string PrivilegedRoleAdministrator = "e8611ab8-c189-46e8-94e1-60213ab1f814";
    public const string GroupsAdministrator = "fdd7a751-b60b-444a-984c-02652fe8fa1c";
    public const string UserAdministrator = "fe930be7-5e62-47db-91af-98c3a49a38b1";
}
