using System.Security.Claims;
using AccessReviewPortal.Options;
using AccessReviewPortal.Services.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AccessReviewPortal.Tests;

public class UserTokenServiceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string UserObjectId = "33333333-3333-3333-3333-333333333333";

    /// <summary>
    /// Regression: users who signed in with "Sign in with Microsoft" got IDW10503 ("Cannot determine the cloud Instance")
    /// because the token request used the default (cookie) scheme. With the OpenID Connect scheme the request reaches MSAL;
    /// with an empty token cache that means "consent or a new sign-in is needed".
    /// </summary>
    [Fact]
    public async Task Openid_connect_users_get_tokens_for_the_openid_connect_scheme()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
            ["AzureAd:TenantId"] = TenantId,
            ["AzureAd:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["AzureAd:ClientSecret"] = "test-secret",
            ["AzureAd:CallbackPath"] = "/signin-oidc"
        });
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddPortalAuthentication(builder.Configuration, new PortalAuthOptions(), new PortalAuthorizationOptions());
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var tokens = new UserTokenService(scope.ServiceProvider, null!);

        var ex = await Assert.ThrowsAsync<UserConsentRequiredException>(
            () => tokens.GetAccessTokenAsync(OpenIdConnectUser(), UserTokenService.ArmScopes, CancellationToken.None));

        Assert.False(ex.IsDeviceCode);
        Assert.Equal(UserTokenService.ArmScopes, ex.Scopes);
    }

    private static ClaimsPrincipal OpenIdConnectUser() => new(new ClaimsIdentity(
    [
        new Claim("oid", UserObjectId),
        new Claim("tid", TenantId),
        new Claim("uid", UserObjectId),
        new Claim("utid", TenantId),
        new Claim("preferred_username", "alice@contoso.com"),
        new Claim("name", "Alice")
    ], "AuthenticationTypes.Federation", "name", "roles"));
}
