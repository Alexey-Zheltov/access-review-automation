using AccessReviewPortal.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders.Distributed;
using Microsoft.Identity.Web.UI;

namespace AccessReviewPortal.Services.Identity;

/// <summary>Sign-in of portal users: Microsoft Entra ID (OpenID Connect + token acquisition) or the access key fallback.</summary>
public static class PortalAuthenticationSetup
{
    /// <summary>
    /// The cookie is the default scheme; OpenID Connect is used only for the explicit sign-in endpoint. Delegated tokens
    /// must therefore be requested for <see cref="OpenIdConnectDefaults.AuthenticationScheme"/> (see UserTokenService).
    /// Needs an IDistributedCache for the MSAL token cache.
    /// </summary>
    public static IServiceCollection AddPortalAuthentication(this IServiceCollection services, IConfiguration configuration,
        PortalAuthOptions authOptions, PortalAuthorizationOptions authorizationOptions)
    {
        var authentication = services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        });

        void ConfigureCookie(CookieAuthenticationOptions cookie)
        {
            cookie.LoginPath = authOptions.IsEntraId ? "/account/signin" : "/login";
            cookie.AccessDeniedPath = "/account/denied";
            cookie.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, authOptions.SessionHours));
            cookie.SlidingExpiration = true;
            cookie.Cookie.Name = "arp_session";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        }

        if (authOptions.IsEntraId)
        {
            // OpenID Connect sign-in. The same app registration holds the read-only application permissions of the scanner
            // and the delegated permissions used for revoke "as the signed-in user" (incremental consent).
            authentication
                .AddMicrosoftIdentityWebApp(identity => configuration.Bind(AzureAdOptions.Section, identity), ConfigureCookie,
                    OpenIdConnectDefaults.AuthenticationScheme, CookieAuthenticationDefaults.AuthenticationScheme)
                .EnableTokenAcquisitionToCallDownstreamApi(UserTokenService.ArmScopes)
                .AddDistributedTokenCaches();
            services.Configure<MsalDistributedTokenCacheAdapterOptions>(o =>
            {
                o.Encrypt = true;
                o.SlidingExpiration = TimeSpan.FromDays(14);
            });
            services.AddControllersWithViews().AddMicrosoftIdentityUI();
        }
        else
        {
            authentication.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, ConfigureCookie);
        }

        services.AddAuthorization(o =>
        {
            o.AddPolicy(PortalPolicies.Reviewer, p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.IsPortalReviewer(authorizationOptions)));
            o.AddPolicy(PortalPolicies.Admin, p => p.RequireAuthenticatedUser().RequireAssertion(c => c.User.IsPortalAdmin(authorizationOptions)));
            o.FallbackPolicy = o.GetPolicy(PortalPolicies.Reviewer);
        });
        return services;
    }
}
