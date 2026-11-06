using System.Security.Claims;
using AccessReviewPortal.Options;

namespace AccessReviewPortal.Services.Identity;

public static class PortalClaims
{
    /// <summary>How the user signed in: "oidc", "device_code" or "access_key".</summary>
    public const string LoginMethod = "arp_login";

    /// <summary>MSAL home account id of a device code user (key of the token cache entry).</summary>
    public const string MsalAccountId = "arp_msal_account";

    public const string LoginOidc = "oidc";
    public const string LoginDeviceCode = "device_code";
    public const string LoginAccessKey = "access_key";

    private const string ObjectIdClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string TenantIdClaimType = "http://schemas.microsoft.com/identity/claims/tenantid";

    public static string GetLoginMethod(this ClaimsPrincipal user) =>
        user.FindFirst(LoginMethod)?.Value ?? (user.Identity?.IsAuthenticated == true ? LoginOidc : "");

    public static bool IsDeviceCodeLogin(this ClaimsPrincipal user) => user.GetLoginMethod() == LoginDeviceCode;

    public static bool IsAccessKeyLogin(this ClaimsPrincipal user) => user.GetLoginMethod() == LoginAccessKey;

    /// <summary>True when the user signed in with Entra ID, so delegated tokens (revoke "as the user") are possible.</summary>
    public static bool CanUseDelegatedTokens(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && !user.IsAccessKeyLogin() && !string.IsNullOrEmpty(user.GetUserObjectId());

    public static string? GetUserObjectId(this ClaimsPrincipal user) =>
        user.FindFirst("oid")?.Value ?? user.FindFirst(ObjectIdClaimType)?.Value;

    public static string? GetUserTenantId(this ClaimsPrincipal user) =>
        user.FindFirst("tid")?.Value ?? user.FindFirst(TenantIdClaimType)?.Value;

    /// <summary>UPN for audit fields (ReviewedByUpn, RemediatedByUpn).</summary>
    public static string GetUpn(this ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value
        ?? user.FindFirst(ClaimTypes.Upn)?.Value
        ?? user.FindFirst("upn")?.Value
        ?? user.FindFirst(ClaimTypes.Email)?.Value
        ?? user.Identity?.Name
        ?? "unknown";

    public static string GetDisplayName(this ClaimsPrincipal user) =>
        user.FindFirst("name")?.Value ?? user.Identity?.Name ?? user.GetUpn();

    public static bool HasAppRole(this ClaimsPrincipal user, string role) =>
        user.Claims.Any(c => (c.Type == "roles" || c.Type == ClaimTypes.Role) && string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase));

    public static bool IsPortalAdmin(this ClaimsPrincipal user, PortalAuthorizationOptions options) =>
        user.Identity?.IsAuthenticated == true && (!options.RequireAppRole || user.IsAccessKeyLogin() || user.HasAppRole(options.AdminRole));

    public static bool IsPortalReviewer(this ClaimsPrincipal user, PortalAuthorizationOptions options) =>
        user.IsPortalAdmin(options) || (user.Identity?.IsAuthenticated == true && user.HasAppRole(options.ReviewerRole));
}

public static class PortalPolicies
{
    public const string Reviewer = "PortalReviewer";
    public const string Admin = "PortalAdmin";
}
