using Microsoft.AspNetCore.Components;
using Microsoft.Identity.Web;

namespace AccessReviewPortal.Services.Identity;

/// <summary>
/// Sends the user to the right place when a delegated token needs consent or a new sign-in (incremental consent):
/// OpenID Connect users go through Microsoft.Identity.Web's challenge, device code users get a new code for the scopes.
/// </summary>
public sealed class ConsentRedirector(IServiceProvider services, NavigationManager navigation)
{
    public void Handle(UserConsentRequiredException ex, string? returnUrl = null)
    {
        var back = returnUrl ?? "/" + navigation.ToBaseRelativePath(navigation.Uri);
        var scopes = string.Join(' ', ex.Scopes);
        if (ex.IsDeviceCode)
        {
            navigation.NavigateTo($"/account/device-login?scopes={Uri.EscapeDataString(scopes)}&returnUrl={Uri.EscapeDataString(back)}", forceLoad: true);
            return;
        }

        var handler = services.GetService<MicrosoftIdentityConsentAndConditionalAccessHandler>();
        if (handler is not null && ex.ChallengeException is not null)
        {
            handler.HandleException(ex.ChallengeException);
            return;
        }

        navigation.NavigateTo(
            $"/MicrosoftIdentity/Account/Challenge?redirectUri={Uri.EscapeDataString(back)}&scope={Uri.EscapeDataString(scopes + " openid profile offline_access")}",
            forceLoad: true);
    }
}
