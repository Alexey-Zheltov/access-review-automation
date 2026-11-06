using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;

namespace AccessReviewPortal.Services.Identity;

/// <summary>The user must sign in again or give consent (incremental consent) before the delegated call can run.</summary>
public sealed class UserConsentRequiredException(string[] scopes, bool isDeviceCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string[] Scopes { get; } = scopes;
    public bool IsDeviceCode { get; } = isDeviceCode;

    /// <summary>Exception to pass to MicrosoftIdentityConsentAndConditionalAccessHandler (OpenID Connect users).</summary>
    public Exception? ChallengeException => InnerException;
}

/// <summary>
/// Delegated access tokens of the signed-in reviewer. Every revoke runs with these tokens, never with the scanner's
/// application permissions: the portal can only do what the user is allowed to do in Azure / Entra ID.
/// </summary>
public sealed class UserTokenService(IServiceProvider services, DeviceCodeLoginService deviceCode)
{
    public static readonly string[] ArmScopes = ["https://management.azure.com/user_impersonation"];
    public static readonly string[] GraphAssignmentScheduleScopes = ["https://graph.microsoft.com/RoleAssignmentSchedule.ReadWrite.Directory"];
    public static readonly string[] GraphEligibilityScheduleScopes = ["https://graph.microsoft.com/RoleEligibilitySchedule.ReadWrite.Directory"];
    public static readonly string[] GraphGroupMemberScopes = ["https://graph.microsoft.com/GroupMember.ReadWrite.All"];

    public async Task<string> GetAccessTokenAsync(ClaimsPrincipal user, string[] scopes, CancellationToken ct)
    {
        if (!user.CanUseDelegatedTokens())
        {
            throw new InvalidOperationException("Revoke runs only for users signed in with Microsoft Entra ID (Authentication__Mode=EntraId).");
        }

        if (user.IsDeviceCodeLogin())
        {
            return await deviceCode.AcquireTokenSilentAsync(user, scopes, ct);
        }

        var tokenAcquisition = services.GetService<ITokenAcquisition>()
                               ?? throw new InvalidOperationException("Microsoft Entra ID sign-in is not configured.");
        try
        {
            // The default scheme of the portal is the cookie: without the OpenID Connect scheme Microsoft.Identity.Web
            // cannot find the Entra ID settings (IDW10503).
            return await tokenAcquisition.GetAccessTokenForUserAsync(scopes, authenticationScheme: OpenIdConnectDefaults.AuthenticationScheme, user: user);
        }
        catch (MicrosoftIdentityWebChallengeUserException ex)
        {
            throw new UserConsentRequiredException(scopes, isDeviceCode: false, "Consent or a new sign-in is needed.", ex);
        }
        catch (Microsoft.Identity.Client.MsalUiRequiredException ex)
        {
            throw new UserConsentRequiredException(scopes, isDeviceCode: false, "Consent or a new sign-in is needed.",
                new MicrosoftIdentityWebChallengeUserException(ex, scopes));
        }
    }
}
